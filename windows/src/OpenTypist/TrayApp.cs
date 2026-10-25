using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using static OpenTypist.Win32;

namespace OpenTypist;

/// The notification-area (tray) icon and the app's wiring: the Windows side
/// of AppDelegate.swift, with the same menu as the Mac menu bar item.
public sealed class TrayApp : IDisposable
{
    private readonly Application app;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly AppSettings settings = AppSettings.Load();
    private readonly EngineService engine = new();
    private readonly ModelManager models = new();
    private readonly FocusReader reader = new();
    private readonly KeyboardHook hook = new();
    private CompletionController? controller;
    private Autocorrector? autocorrector;
    private TextReplacements? replacements;
    // Created in Start, on the UI thread (they post changes to it).
    private Personalization personal = null!;
    private WritingSamples samples = null!;
    private ScreenContext screen = null!;
    private readonly Stats stats = new(Path.Combine(AppSettings.DataDirectory, "stats.json"));
    private PowerMonitor power = null!;
    private Updater updater = null!;
    private Forms.NotifyIcon? notifyIcon;
    private Icon? activeIcon, inactiveIcon;
    private SettingsWindow? settingsWindow;
    private EventWaitHandle? showSettingsEvent;
    private RegisteredWaitHandle? showSettingsWait;
    private IntPtr foregroundHook;
    private WinEventProc? foregroundProc;
    /// The app the user was in before opening the menu, for "Disable in <app>".
    private (string Id, string Name)? lastExternalApp;
    /// The model file currently loaded (or loading).
    private string? loadedModelPath;
    private bool loadedOnGpu;
    /// Unloaded after a stretch without typing; reloads on the next keystroke.
    private bool sleeping;
    /// Paused by the battery / battery saver setting.
    private bool powerPaused;
    /// Last keystroke (UTC ticks), written by the hook thread.
    private long lastTypingTicks = DateTime.UtcNow.Ticks;
    private DispatcherTimer? idleTimer;
    private bool shutDown;

    public AppSettings Settings => settings;
    public EngineService Engine => engine;
    public ModelManager Models => models;
    public bool HookRunning => hook.IsRunning;
    public Autocorrector? Autocorrector => autocorrector;
    public TextReplacements? Replacements => replacements;
    public Personalization Personal => personal;
    public WritingSamples Samples => samples;
    public Stats Stats => stats;
    public PowerMonitor Power => power;
    public Updater Updater => updater;
    /// The app the user was in before opening the menu or Settings.
    public (string Id, string Name)? LastExternalApp => lastExternalApp;

    public TrayApp(Application app)
    {
        this.app = app;
    }

    public void Start(bool showSettings)
    {
        personal = new Personalization(settings);
        samples = new WritingSamples(settings);
        screen = new ScreenContext(settings);
        power = new PowerMonitor(settings);
        updater = new Updater(settings);
        controller = new CompletionController(engine, reader, settings, personal, samples, screen, stats);
        replacements = new TextReplacements(settings);
        autocorrector = new Autocorrector(engine, reader, settings, replacements, personal, samples, screen, stats);
        controller.Accepted += autocorrector.NoteInserted;
        autocorrector.TextChanged += controller.TextChangedExternally;

        // Order matters: accepting a suggestion wins over autocorrect, which
        // wins over normal typing.
        var completion = controller;
        var corrector = autocorrector;
        hook.OnKeyDown = e =>
        {
            NoteTyping();
            if (completion.TryAcceptOnHookThread(e)) return true;
            if (corrector.HandleKeyDown(e, ForegroundAppId())) return true;
            completion.PostKeyDown(e);
            return false;
        };
        hook.OnMouseDown = () =>
        {
            corrector.Reset();
            dispatcher.BeginInvoke(() =>
            {
                AppStatus.Log("mouse down: suggestion dismissed");
                completion.HandleMouseDown();
            });
        };
        // Autocorrect and learning check words in the hook; start the spell
        // checker there before the first word ends.
        hook.Prepare = SpellCheck.Warm;
        if (hook.Start()) AppStatus.Note("Keyboard monitor started");
        else AppStatus.Note("Windows refused the keyboard hook");

        foregroundProc = OnForegroundChanged;
        foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        engine.StateChanged += state =>
        {
            AppStatus.Log($"engine: {state.Status} {state.Detail}{(state.Status == EngineService.Status.Ready ? $" ({(engine.UsesGpu ? "GPU" : "CPU")}, {engine.LastLoadSeconds:0.0} s)" : "")}");
            UpdateIcon();
        };
        models.Installed += model =>
        {
            settings.SelectedModelId = model.Id;
            settings.CustomModelPath = null;
            settings.Save();
            LoadModel(force: true);
        };
        settings.Changed += () =>
        {
            controller.Dismiss();
            // Turning the idle unload off wakes the model up.
            if (sleeping && !settings.UnloadWhenIdle) sleeping = false;
            LoadModel();
            UpdateIcon();
        };
        AppStatus.Changed += UpdateIcon;
        // Battery or battery saver changed: maybe switch model or pause.
        power.Changed += () =>
        {
            LoadModel();
            UpdateIcon();
        };
        power.Start();
        StartIdleTimer();
        updater.Changed += UpdateIcon;

        CreateTrayIcon();
        showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSettingsEventName);
        showSettingsWait = ThreadPool.RegisterWaitForSingleObject(showSettingsEvent, (_, _) => dispatcher.BeginInvoke(() => ShowSettings()), null, -1, false);

        if (settings.SelectedModelId is null)
        {
            settings.SelectedModelId = ModelCatalog.Recommended(Hardware.Current).Id;
            settings.Save();
        }
        LoadModel();
        UpdateIcon();
        updater.StartAutomaticChecks();

        if (models.ActiveModelPath(settings.SelectedModelId, settings.CustomModelPath) is null)
        {
            // Nothing downloads until the user picks a model.
            ShowSettings("Model");
            notifyIcon?.ShowBalloonTip(5000, "OpenTypist", "Choose a model to start getting suggestions.", Forms.ToolTipIcon.Info);
        }
        else if (showSettings)
        {
            ShowSettings();
        }
    }

    /// For Settings snapshots (--snapshot-settings): the data objects only;
    /// no hooks, tray icon, model or update checks.
    public void StartPreview()
    {
        personal = new Personalization(settings);
        samples = new WritingSamples(settings);
        screen = new ScreenContext(settings);
        power = new PowerMonitor(settings);
        updater = new Updater(settings);
        replacements = new TextReplacements(settings);
        autocorrector = new Autocorrector(engine, reader, settings, replacements, personal, samples, screen, stats);
    }

    // Model lifecycle

    /// Loads the model that fits the current situation: the selected one (or
    /// a custom .gguf), the smaller one on battery or in battery saver if so
    /// configured, or none while paused or sleeping.
    public void LoadModel(bool force = false)
    {
        var behavior = power.Behavior;
        bool wasPaused = powerPaused;
        powerPaused = behavior == PowerBehavior.Pause;
        if (powerPaused)
        {
            controller?.Dismiss();
            if (loadedModelPath is not null || engine.CurrentState.Status != EngineService.Status.Idle) engine.Unload();
            loadedModelPath = null;
            if (!wasPaused) AppStatus.Note($"Suggestions paused {power.Situation}");
            return;
        }
        var path = models.ActiveModelPath(settings.SelectedModelId, settings.CustomModelPath);
        if (behavior == PowerBehavior.SmallerModel)
        {
            if (ModelCatalog.Model(settings.SmallerModelId) is { } small && models.IsDownloaded(small)) path = models.FileFor(small);
            else AppStatus.Note("The smaller model isn't downloaded; using the usual one");
        }
        if (path is null)
        {
            if (loadedModelPath is not null) engine.Unload();
            loadedModelPath = null;
            return;
        }
        if (sleeping) return;
        if (!force && path == loadedModelPath && loadedOnGpu == settings.UseGpu && engine.CurrentState.Status != EngineService.Status.Idle) return;
        loadedModelPath = path;
        loadedOnGpu = settings.UseGpu;
        engine.Load(path, settings.UseGpu);
    }

    /// Hook thread: a key went down.
    private void NoteTyping()
    {
        Interlocked.Exchange(ref lastTypingTicks, DateTime.UtcNow.Ticks);
        if (!Volatile.Read(ref sleeping) || settings.IsDisabled(ForegroundAppId())) return;
        dispatcher.BeginInvoke(() =>
        {
            if (!sleeping) return;
            sleeping = false;
            AppStatus.Note("Loading the model again");
            LoadModel();
            UpdateIcon();
        });
    }

    private void StartIdleTimer()
    {
        idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            var lastTyping = new DateTime(Interlocked.Read(ref lastTypingTicks), DateTimeKind.Utc);
            if (sleeping || !engine.IsReady || !PowerRules.ShouldUnloadForIdle(settings.UnloadWhenIdle, settings.IdleUnloadMinutes, lastTyping, DateTime.UtcNow)) return;
            sleeping = true;
            controller?.Dismiss();
            engine.Unload();
            loadedModelPath = null;
            AppStatus.Note($"Unloaded the model after {settings.IdleUnloadMinutes} minute{(settings.IdleUnloadMinutes == 1 ? "" : "s")} without typing");
            UpdateIcon();
        }, dispatcher);
        idleTimer.Start();
    }

    public string LoadedModelName
    {
        get
        {
            if (loadedModelPath is null) return "model";
            var file = Path.GetFileName(loadedModelPath);
            return ModelCatalog.All.FirstOrDefault(m => m.FileNames.Contains(file))?.Name ?? Path.GetFileNameWithoutExtension(file);
        }
    }

    public string StatusText()
    {
        if (!hook.IsRunning) return "Keyboard hook blocked by Windows";
        var download = models.Download;
        switch (download.Phase)
        {
            case ModelManager.Phase.Downloading:
                return $"Downloading {ModelCatalog.Model(download.ModelId)?.Name ?? "model"}... {download.Percent}%";
            case ModelManager.Phase.Verifying:
                return "Checking downloaded model...";
            case ModelManager.Phase.Failed when loadedModelPath is null:
                return $"Download failed: {download.Message}";
        }
        if (powerPaused) return $"Paused {power.Situation} to save battery";
        if (sleeping) return "Model unloaded to save memory; loads when you type";
        var state = engine.CurrentState;
        return state.Status switch
        {
            EngineService.Status.Idle => loadedModelPath is null ? "No model yet: choose one" : "Starting...",
            EngineService.Status.Loading => $"Loading {LoadedModelName}...",
            EngineService.Status.Failed => $"Model error: {state.Detail}",
            _ => settings.Enabled ? $"Ready · {LoadedModelName}{(engine.UsesGpu ? "" : " (CPU)")}" : "Paused",
        };
    }

    // Foreground changes

    private IntPtr lastForeground;

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Windows also reports the same window becoming foreground again.
        if (hwnd == lastForeground) return;
        lastForeground = hwnd;
        AppStatus.Log($"foreground changed: {ClassName(hwnd)}");
        controller?.Dismiss();
        autocorrector?.Reset();
        samples?.Flush();
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == Environment.ProcessId || ClassName(hwnd) == "Shell_TrayWnd") return;
        var path = ProcessImagePath(pid);
        if (path is null) return;
        lastExternalApp = (Path.GetFileName(path).ToLowerInvariant(), FocusReader.DisplayName(path));
        autocorrector?.SetCurrentApp(lastExternalApp.Value.Id, lastExternalApp.Value.Name);
    }

    // Tray icon and menu

    private void CreateTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
        activeIcon = File.Exists(iconPath) ? new Icon(iconPath, Forms.SystemInformation.SmallIconSize) : (Icon)SystemIcons.Application.Clone();
        inactiveIcon = Grayscale(activeIcon);
        notifyIcon = new Forms.NotifyIcon { Icon = activeIcon, Text = "OpenTypist", Visible = true, ContextMenuStrip = new Forms.ContextMenuStrip() };
        notifyIcon.ContextMenuStrip.Opening += (_, e) =>
        {
            BuildMenu(notifyIcon.ContextMenuStrip);
            e.Cancel = false;
        };
        notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowSettings();
        };
    }

    private static Icon Grayscale(Icon icon)
    {
        using var source = icon.ToBitmap();
        using var gray = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(gray))
        {
            var matrix = new ColorMatrix(
            [
                [0.3f, 0.3f, 0.3f, 0, 0],
                [0.59f, 0.59f, 0.59f, 0, 0],
                [0.11f, 0.11f, 0.11f, 0, 0],
                [0, 0, 0, 0.5f, 0],
                [0, 0, 0, 0, 1],
            ]);
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix);
            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return Icon.FromHandle(gray.GetHicon());
    }

    private void UpdateIcon()
    {
        if (notifyIcon is null) return;
        bool active = settings.Enabled && engine.IsReady && hook.IsRunning;
        notifyIcon.Icon = active ? activeIcon : inactiveIcon;
        var text = "OpenTypist: " + StatusText();
        notifyIcon.Text = text.Length > 127 ? text[..127] : text;
        settingsWindow?.RefreshStatus();
    }

    private void BuildMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new Forms.ToolStripMenuItem(StatusText()) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripMenuItem($"Last check: {AppStatus.LastCheck}") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add(Item("Enable Completions", settings.Enabled, () =>
        {
            settings.Enabled = !settings.Enabled;
            settings.Save();
        }));
        if (lastExternalApp is (string id, string name))
        {
            menu.Items.Add(Item($"Disable in {name}", settings.IsDisabled(id), () => settings.SetDisabled(id, !settings.IsDisabled(id))));
        }
        var lengthMenu = new Forms.ToolStripMenuItem("Suggestion Length");
        foreach (var length in Enum.GetValues<CompletionLength>())
        {
            lengthMenu.DropDownItems.Add(Item(length.Title(), settings.Length == length, () =>
            {
                settings.Length = length;
                settings.Save();
            }));
        }
        menu.Items.Add(lengthMenu);
        menu.Items.Add(Item("Add Space After Accepting", settings.AcceptTrailingSpace, () =>
        {
            settings.AcceptTrailingSpace = !settings.AcceptTrailingSpace;
            settings.Save();
        }));

        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var line in new[]
        {
            $"Today: {stats.WordsAcceptedToday} words accepted",
            $"All time: {stats.WordsAccepted} words, {stats.KeystrokesSaved} keystrokes saved",
        })
        {
            menu.Items.Add(new Forms.ToolStripMenuItem(line) { Enabled = false });
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        if (models.IsDownloading) menu.Items.Add(Item("Cancel Model Download", false, models.CancelDownload));
        if (updater.Available is { } release) menu.Items.Add(Item($"Update to OpenTypist {release.Version}...", false, () => ShowSettings("General")));
        else menu.Items.Add(Item("Check for Updates...", false, () => updater.Check(userInitiated: true)));
        if (personal.Profile.Proposal is not null) menu.Items.Add(Item("Profile Update Ready...", false, () => ShowSettings("Personal")));
        menu.Items.Add(Item("Choose Model...", false, () => ShowSettings("Model")));
        menu.Items.Add(Item("Settings...", false, () => ShowSettings()));
        menu.Items.Add(Item("Quit OpenTypist", false, Quit));
    }

    private static Forms.ToolStripMenuItem Item(string title, bool isChecked, Action action)
    {
        var item = new Forms.ToolStripMenuItem(title) { Checked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    /// Opens Settings, on the tab with that header if given.
    public void ShowSettings(string? tab = null)
    {
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        settingsWindow.Show();
        if (tab is not null) settingsWindow.SelectTab(tab);
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    private void Quit() => app.Shutdown();

    /// Frees the model and removes the hooks before the process exits.
    public void Shutdown()
    {
        if (shutDown) return;
        shutDown = true;
        controller?.Dismiss();
        hook.Dispose();
        samples?.Flush();
        personal?.Flush();
        stats.Flush();
        power?.Dispose();
        idleTimer?.Stop();
        if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
        foregroundHook = IntPtr.Zero;
        models.CancelDownload();
        engine.Shutdown();
        reader.Dispose();
        showSettingsWait?.Unregister(null);
        showSettingsEvent?.Dispose();
        if (notifyIcon is not null)
        {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            notifyIcon = null;
        }
    }

    public void Dispose() => Shutdown();
}
