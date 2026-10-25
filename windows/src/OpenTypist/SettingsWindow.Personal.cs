using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenTypist;

/// Settings → General (memory & battery, updates, screen context), Personal
/// (profile, learned words, never-correct words, writing samples) and Stats.
/// The Windows side of PersonalSettingsView.swift and the Mac Settings'
/// PowerSettings, UpdatesPanel and StatsSettings.
public partial class SettingsWindow
{
    private static readonly int[] IdleChoices = [5, 15, 30, 60, 120];

    // General: screen context, memory & battery, updates

    private void SetUpPowerAndUpdates()
    {
        ScreenContextBox.IsChecked = settings.UseScreenContext;
        ScreenContextBox.Click += (_, _) => Update(() => settings.UseScreenContext = ScreenContextBox.IsChecked == true);
        if (!ScreenContext.IsAvailable)
        {
            ScreenContextHelp.Text = "Windows has no text recognition for your languages (Settings > Time & language > Language & region > a language's options > Optical character recognition).";
        }

        foreach (var minutes in IdleChoices)
        {
            IdleMinutesBox.Items.Add(new ComboBoxItem { Content = minutes < 60 ? $"{minutes} minutes" : $"{minutes / 60} hour{(minutes > 60 ? "s" : "")}", Tag = minutes });
        }
        foreach (var behavior in Enum.GetValues<PowerBehavior>())
        {
            BatteryBox.Items.Add(new ComboBoxItem { Content = behavior.Title(), Tag = behavior });
            SaverBox.Items.Add(new ComboBoxItem { Content = behavior.Title(), Tag = behavior });
        }
        loading = true;
        UnloadBox.IsChecked = settings.UnloadWhenIdle;
        int idle = IdleChoices.MinBy(m => Math.Abs(m - settings.IdleUnloadMinutes));
        IdleMinutesBox.SelectedIndex = Array.IndexOf(IdleChoices, idle);
        BatteryBox.SelectedIndex = (int)settings.BatteryBehavior;
        SaverBox.SelectedIndex = (int)settings.LowPowerBehavior;
        CheckUpdatesBox.IsChecked = settings.CheckForUpdates;
        loading = false;
        BuildSmallerModelBox();

        UnloadBox.Click += (_, _) => { Update(() => settings.UnloadWhenIdle = UnloadBox.IsChecked == true); RefreshPower(); };
        IdleMinutesBox.SelectionChanged += (_, _) => Update(() => settings.IdleUnloadMinutes = (int)((ComboBoxItem)IdleMinutesBox.SelectedItem).Tag);
        BatteryBox.SelectionChanged += (_, _) => { Update(() => settings.BatteryBehavior = (PowerBehavior)((ComboBoxItem)BatteryBox.SelectedItem).Tag); RefreshPower(); };
        SaverBox.SelectionChanged += (_, _) => { Update(() => settings.LowPowerBehavior = (PowerBehavior)((ComboBoxItem)SaverBox.SelectedItem).Tag); RefreshPower(); };
        SmallerModelBox.SelectionChanged += (_, _) =>
        {
            if (SmallerModelBox.SelectedItem is ComboBoxItem { Tag: string id }) Update(() => settings.SmallerModelId = id);
            RefreshPower();
        };
        DownloadSmallerButton.Click += (_, _) =>
        {
            if (ModelCatalog.Model(settings.SmallerModelId) is { } model) _ = app.Models.StartDownload(model);
        };

        VersionText.Text = $"Version {Updater.CurrentVersion}";
        CheckUpdatesBox.Click += (_, _) => Update(() => settings.CheckForUpdates = CheckUpdatesBox.IsChecked == true);
        CheckNowButton.Click += (_, _) => app.Updater.Check(userInitiated: true);
        InstallUpdateButton.Click += (_, _) => app.Updater.Install();
        ReleaseNotesButton.Click += (_, _) =>
        {
            if (app.Updater.Available is { Page.Length: > 0 } release) Process.Start(new ProcessStartInfo(release.Page) { UseShellExecute = true });
        };

        app.Updater.Changed += RefreshUpdates;
        app.Power.Changed += RefreshPower;
        Closed += (_, _) =>
        {
            app.Updater.Changed -= RefreshUpdates;
            app.Power.Changed -= RefreshPower;
        };
        RefreshPower();
        RefreshUpdates();
    }

    /// Models small enough to be worth switching to on battery.
    private void BuildSmallerModelBox()
    {
        loading = true;
        SmallerModelBox.Items.Clear();
        foreach (var model in ModelCatalog.All.Where(m => m.Size < 4_000_000_000))
        {
            var item = new ComboBoxItem
            {
                Content = $"{model.Name} ({model.SizeText}){(app.Models.IsDownloaded(model) ? "" : ", not downloaded")}",
                Tag = model.Id,
            };
            SmallerModelBox.Items.Add(item);
            if (model.Id == settings.SmallerModelId) SmallerModelBox.SelectedItem = item;
        }
        loading = false;
    }

    private void RefreshPower()
    {
        IdleMinutesBox.IsEnabled = settings.UnloadWhenIdle;
        var path = app.Models.ActiveModelPath(settings.SelectedModelId, settings.CustomModelPath);
        var size = path is not null && File.Exists(path) ? $"about {new FileInfo(path).Length / 1e9:0.0} GB" : "the model's memory";
        var load = app.Engine.LastLoadSeconds > 0 ? $"{app.Engine.LastLoadSeconds:0.0} seconds" : "a moment";
        UnloadHelp.Text = $"Frees {size}. The model loads again on your next keystroke; the last load took {load}, and the first suggestion waits for it.";

        bool usesSmaller = settings.BatteryBehavior == PowerBehavior.SmallerModel || settings.LowPowerBehavior == PowerBehavior.SmallerModel;
        SmallerModelBox.IsEnabled = usesSmaller;
        var smaller = ModelCatalog.Model(settings.SmallerModelId);
        bool missing = usesSmaller && smaller is not null && !app.Models.IsDownloaded(smaller);
        SmallerMissingPanel.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        if (missing && smaller is not null)
        {
            SmallerMissingText.Text = $"{smaller.Name} isn't downloaded yet, so the usual model is used until it is.";
            DownloadSmallerButton.Content = $"Download ({smaller.SizeText})";
            DownloadSmallerButton.IsEnabled = !app.Models.IsDownloading;
        }
        PowerNowText.Text = $"Right now: {(app.Power.OnBattery ? "on battery" : "plugged in")}{(app.Power.BatterySaver ? ", battery saver on" : "")}.";
    }

    private void RefreshUpdates()
    {
        var updater = app.Updater;
        UpdateStatusText.Text = updater.State switch
        {
            Updater.Phase.Idle => settings.LastUpdateCheck is DateTime last
                ? $"Last checked {last.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)}"
                : "Not checked yet",
            Updater.Phase.Checking => "Checking...",
            Updater.Phase.UpToDate => "OpenTypist is up to date.",
            Updater.Phase.Available => $"OpenTypist {updater.Available?.Version} is available.",
            _ => updater.Message,
        };
        UpdateStatusText.Foreground = updater.State == Updater.Phase.Failed ? Brushes.Firebrick : SystemColors.ControlTextBrush;
        bool available = updater.Available is not null && updater.State is Updater.Phase.Available or Updater.Phase.Failed;
        ReleaseNotesButton.Visibility = InstallUpdateButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        CheckNowButton.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        CheckNowButton.IsEnabled = updater.State is not (Updater.Phase.Checking or Updater.Phase.Installing);
        InstallUpdateButton.IsEnabled = updater.State != Updater.Phase.Installing;
    }

    // Personal

    /// The profile text as last saved, to tell edits apart.
    private string profileBase = "";

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private void SetUpPersonal()
    {
        var personal = app.Personal;
        foreach (var mode in Enum.GetValues<ProfileUpdateMode>())
        {
            ProfileModeBox.Items.Add(new ComboBoxItem { Content = mode == ProfileUpdateMode.Automatic ? "Update it automatically" : "Ask me first", Tag = mode });
        }
        loading = true;
        UseProfileBox.IsChecked = settings.UseProfile;
        LearnBox.IsChecked = settings.LearnHabits;
        SamplesBox.IsChecked = settings.KeepWritingSamples;
        profileBase = personal.Profile.Text;
        ProfileBox.Text = profileBase;
        loading = false;

        ProfileBox.TextChanged += (_, _) => RefreshProfileEditing();
        SaveProfileButton.Click += (_, _) => SaveProfile();
        ProfileBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { SaveProfile(); e.Handled = true; }
        };
        RevertProfileButton.Click += (_, _) => ProfileBox.Text = profileBase;
        UseProfileBox.Click += (_, _) => Update(() => settings.UseProfile = UseProfileBox.IsChecked == true);
        LockProfileBox.Click += (_, _) => personal.SetLocked(LockProfileBox.IsChecked == true);
        ProfileModeBox.SelectionChanged += (_, _) =>
        {
            if (!loading && ProfileModeBox.SelectedItem is ComboBoxItem { Tag: ProfileUpdateMode mode }) personal.SetMode(mode);
        };
        UpdateProfileButton.Click += (_, _) =>
        {
            var note = personal.UpdateProfile();
            ProfileMessage.Text = note is null ? "Nothing new to add." : $"Profile: {note}";
            AppStatus.Note(note is null ? "Profile: nothing new to add" : $"Profile: {note}");
        };
        ApplyProposalButton.Click += (_, _) => personal.ApplyProposal();
        DismissProposalButton.Click += (_, _) => personal.DismissProposal();

        LearnBox.Click += (_, _) => Update(() => settings.LearnHabits = LearnBox.IsChecked == true);
        ForgetLearnedButton.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Forget the names, words, spelling, greetings and app styles OpenTypist has learned? Your profile text and never-correct words stay.",
                    "OpenTypist", MessageBoxButton.OKCancel) == MessageBoxResult.OK) personal.ForgetLearned();
        };
        AddNeverCorrectButton.Click += (_, _) => AddNeverCorrect();
        NeverCorrectBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { AddNeverCorrect(); e.Handled = true; }
        };
        NeverCorrectBox.TextChanged += (_, _) => AddNeverCorrectButton.IsEnabled = NeverCorrectBox.Text.Trim().Length > 0;

        SamplesBox.Click += (_, _) => Update(() => settings.KeepWritingSamples = SamplesBox.IsChecked == true);
        ShowSamplesButton.Click += (_, _) => new SamplesWindow(app.Samples) { Owner = this }.ShowDialog();
        DeleteSamplesButton.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Delete all {app.Samples.Samples.Count} writing samples?", "OpenTypist", MessageBoxButton.OKCancel) == MessageBoxResult.OK) app.Samples.DeleteAll();
        };

        personal.Changed += RefreshPersonal;
        app.Samples.Changed += RefreshPersonal;
        Closed += (_, _) =>
        {
            personal.Changed -= RefreshPersonal;
            app.Samples.Changed -= RefreshPersonal;
        };
        RefreshPersonal();
    }

    private bool IsEditingProfile => Normalize(ProfileBox.Text) != profileBase;

    private void SaveProfile()
    {
        if (!IsEditingProfile) return;
        app.Personal.SaveProfileText(ProfileBox.Text);
        profileBase = app.Personal.Profile.Text;
        ProfileBox.Text = profileBase;
        ProfileMessage.Text = "Saved.";
    }

    private void AddNeverCorrect()
    {
        var word = NeverCorrectBox.Text.Trim();
        if (word.Length == 0) return;
        app.Personal.AddNeverCorrect(word);
        NeverCorrectBox.Text = "";
    }

    private void RefreshProfileEditing()
    {
        int length = Normalize(ProfileBox.Text).Length;
        ProfileCountText.Text = $"{length}/{Personalization.MaxProfileLength}";
        ProfileCountText.Foreground = length > Personalization.MaxProfileLength ? Brushes.Firebrick : SystemColors.GrayTextBrush;
        bool editing = IsEditingProfile;
        SaveProfileButton.IsEnabled = RevertProfileButton.IsEnabled = editing;
        UpdateProfileButton.IsEnabled = !editing && LockProfileBox.IsChecked != true;
    }

    private void RefreshPersonal()
    {
        var personal = app.Personal;
        var profile = personal.Profile;
        var learned = personal.Learned;

        loading = true;
        // Keep the user's unsaved edits; otherwise show what's current.
        if (!IsEditingProfile)
        {
            profileBase = profile.Text;
            ProfileBox.Text = profile.Text;
        }
        LockProfileBox.IsChecked = profile.Locked;
        ProfileModeBox.SelectedIndex = (int)profile.Mode;
        ProfileModeBox.IsEnabled = !profile.Locked;
        loading = false;
        RefreshProfileEditing();

        ProposalPanel.Visibility = profile.Proposal is null ? Visibility.Collapsed : Visibility.Visible;
        ProposalNoteText.Text = profile.ProposalNote ?? "";
        ProposalText.Text = profile.Proposal ?? "";

        HistoryPanel.Children.Clear();
        foreach (var version in Enumerable.Reverse(profile.History).Take(12))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), ToolTip = version.Text };
            FrameworkElement action = version.Text == profile.Text
                ? new TextBlock { Text = "Current", Foreground = SystemColors.GrayTextBrush, VerticalAlignment = VerticalAlignment.Center }
                : MakeButton("Restore", () => personal.Restore(version), new Thickness(0));
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = version.Note, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock
            {
                Text = $"{version.Date.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)} · {Source(version.Source)}",
                Foreground = SystemColors.GrayTextBrush,
                FontSize = 11,
            });
            row.Children.Add(text);
            HistoryPanel.Children.Add(row);
        }

        FillChips(NamesPanel, personal.LearnedNames, personal.RemoveLearnedName);
        FillChips(WordsPanel, personal.LearnedWords, personal.RemoveNeverCorrect);
        FillChips(NeverCorrectPanel, personal.NeverCorrectWords, personal.RemoveNeverCorrect);
        AddNeverCorrectButton.IsEnabled = NeverCorrectBox.Text.Trim().Length > 0;
        LearnedLinesPanel.Children.Clear();
        foreach (var (topic, line) in personal.GeneratedLines().Where(l => l.Topic is "spelling" or "greetings" or "style"))
        {
            LearnedLinesPanel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        }
        int typed = learned.Names.Values.Sum() + learned.Words.Values.Sum();
        LearnedCountText.Text = $"Typed {typed} names and words so far; {Personalization.LearnThreshold} uses make one count.";

        int count = app.Samples.Samples.Count;
        SamplesCountText.Text = $"{count} sample{(count == 1 ? "" : "s")} stored";
        ShowSamplesButton.IsEnabled = DeleteSamplesButton.IsEnabled = count > 0;
    }

    private static string Source(ProfileVersion.Origin source) => source switch
    {
        ProfileVersion.Origin.Initial => "default",
        ProfileVersion.Origin.You => "you",
        ProfileVersion.Origin.OpenTypist => "OpenTypist",
        _ => "restored",
    };

    /// A wrapping row of removable words.
    private static void FillChips(WrapPanel panel, List<string> items, Action<string> remove)
    {
        panel.Children.Clear();
        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "None yet", Foreground = SystemColors.GrayTextBrush });
            return;
        }
        foreach (var item in items)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal };
            chip.Children.Add(new TextBlock { Text = item, VerticalAlignment = VerticalAlignment.Center });
            var x = new Button
            {
                Content = "×",
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = SystemColors.GrayTextBrush,
                ToolTip = $"Remove {item}",
                Cursor = Cursors.Hand,
            };
            x.Click += (_, _) => remove(item);
            chip.Children.Add(x);
            panel.Children.Add(new Border
            {
                Child = chip,
                Padding = new Thickness(8, 1, 2, 1),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x80, 0x80, 0x80)),
            });
        }
    }

    // Stats

    private void SetUpStats()
    {
        ResetStatsButton.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Reset all statistics?", "OpenTypist", MessageBoxButton.OKCancel) == MessageBoxResult.OK) app.Stats.Reset();
        };
        WordsChart.HoverChanged += day => ChartHoverText.Text = day is { } d
            ? $"{d.Date.ToString("dddd, d MMM", CultureInfo.CurrentCulture)}: {d.Words} word{(d.Words == 1 ? "" : "s")} accepted"
            : "Hover over a bar to see the day.";
        ChartHoverText.Text = "Hover over a bar to see the day.";
        // Recorded on the keyboard hook thread too.
        void OnStatsChanged() => Dispatcher.BeginInvoke(RefreshStats);
        app.Stats.Changed += OnStatsChanged;
        Closed += (_, _) => app.Stats.Changed -= OnStatsChanged;
        RefreshStats();
    }

    private void RefreshStats()
    {
        var stats = app.Stats;
        var days = stats.RecentDays(30);
        int week = days.TakeLast(7).Sum(d => d.Words);
        int month = days.Sum(d => d.Words);
        var best = days.MaxBy(d => d.Words);

        StatTiles.Children.Clear();
        StatTiles.Children.Add(Tile("This week", $"{week:N0}", null));
        StatTiles.Children.Add(Tile("Last 30 days", $"{month:N0}", null));
        StatTiles.Children.Add(Tile("Best day", best.Words > 0 ? $"{best.Words:N0}" : "–",
            best.Words > 0 ? best.Date.ToString("d MMM", CultureInfo.CurrentCulture) : null));
        WordsChart.SetDays(days);

        StatNumbers.Children.Clear();
        StatNumbers.RowDefinitions.Clear();
        var rate = stats.AcceptRate;
        var rows = new (string, string)[]
        {
            ("Words accepted today", $"{stats.WordsAcceptedToday:N0}"),
            ("Words accepted", $"{stats.WordsAccepted:N0}"),
            ("Characters inserted", $"{stats.CharactersInserted:N0}"),
            ("Keystrokes saved", $"{stats.KeystrokesSaved:N0}"),
            ("Suggestions shown", $"{stats.SuggestionsShown:N0}"),
            ("Typos fixed", $"{stats.CorrectionsMade:N0}"),
            ("Accept rate", rate is double r ? $"{r * 100:0}%" : "–"),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            StatNumbers.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Item1, Margin = new Thickness(0, 2, 0, 2) };
            var value = new TextBlock { Text = rows[i].Item2, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            StatNumbers.Children.Add(label);
            StatNumbers.Children.Add(value);
        }
    }

    private static StackPanel Tile(string title, string value, string? detail)
    {
        var tile = new StackPanel { Margin = new Thickness(0, 0, 32, 0) };
        tile.Children.Add(new TextBlock { Text = title, Foreground = SystemColors.GrayTextBrush, FontSize = 11 });
        tile.Children.Add(new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold });
        if (detail is not null) tile.Children.Add(new TextBlock { Text = detail, Foreground = SystemColors.GrayTextBrush, FontSize = 11 });
        return tile;
    }
}

/// Words accepted per day: a bar per day, gridlines on the left, a date
/// label each week, and the hovered day highlighted.
public sealed class WordsChart : FrameworkElement
{
    private const double AxisWidth = 40, LabelHeight = 18, TopPadding = 6;
    private List<(DateTime Date, int Words)> days = [];
    private int hovered = -1;

    public event Action<(DateTime Date, int Words)?>? HoverChanged;

    public void SetDays(List<(DateTime Date, int Words)> value)
    {
        days = value;
        if (hovered >= days.Count) hovered = -1;
        InvalidateVisual();
    }

    private double Slot => days.Count == 0 ? 0 : Math.Max(0, ActualWidth - AxisWidth) / days.Count;

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill so the whole area receives mouse moves.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (days.Count == 0 || ActualWidth <= AxisWidth) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double height = Math.Max(1, ActualHeight - LabelHeight - TopPadding);
        double bottom = TopPadding + height;
        int max = Math.Max(1, days.Max(d => d.Words));
        int step = NiceStep(max);
        int top = (int)Math.Ceiling(max / (double)step) * step;

        var gray = SystemColors.GrayTextBrush;
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)), 1);
        var typeface = new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        for (int value = 0; value <= top; value += step)
        {
            double y = bottom - value / (double)top * height;
            dc.DrawLine(grid, new Point(AxisWidth, y), new Point(ActualWidth, y));
            var label = new FormattedText(value.ToString("N0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, gray, dip);
            dc.DrawText(label, new Point(AxisWidth - 6 - label.Width, y - label.Height / 2));
        }

        var accent = SystemColors.HighlightColor;
        double slot = Slot, barWidth = slot * 0.7;
        for (int i = 0; i < days.Count; i++)
        {
            double barHeight = days[i].Words / (double)top * height;
            byte alpha = hovered < 0 || hovered == i ? (byte)255 : (byte)100;
            var brush = new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
            if (barHeight > 0) dc.DrawRoundedRectangle(brush, null, new Rect(AxisWidth + i * slot + (slot - barWidth) / 2, bottom - barHeight, barWidth, barHeight), 2, 2);
        }
        // Weekly labels, stopping short of the right edge so none clip.
        for (int i = 2; i < days.Count - 3; i += 7)
        {
            var label = new FormattedText(days[i].Date.ToString("d MMM", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, gray, dip);
            dc.DrawText(label, new Point(AxisWidth + (i + 0.5) * slot - label.Width / 2, bottom + 3));
        }
    }

    /// 1, 2 or 5 times a power of ten, giving at most four gridlines.
    private static int NiceStep(int max)
    {
        for (int scale = 1; ; scale *= 10)
        {
            foreach (int step in new[] { 1 * scale, 2 * scale, 5 * scale })
            {
                if (max / (double)step <= 4) return step;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        double x = e.GetPosition(this).X - AxisWidth;
        int index = Slot > 0 && x >= 0 ? (int)(x / Slot) : -1;
        if (index >= days.Count) index = -1;
        if (index == hovered) return;
        hovered = index;
        InvalidateVisual();
        HoverChanged?.Invoke(index >= 0 ? days[index] : null);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        hovered = -1;
        InvalidateVisual();
        HoverChanged?.Invoke(null);
    }
}

/// The stored writing samples, each deletable.
public sealed class SamplesWindow : Window
{
    private readonly WritingSamples samples;
    private readonly StackPanel list = new();

    public SamplesWindow(WritingSamples samples)
    {
        this.samples = samples;
        Title = "Writing samples";
        Width = 600;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        var done = new Button { Content = "Done", Padding = new Thickness(14, 3, 14, 3), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), IsDefault = true, IsCancel = true };
        done.Click += (_, _) => Close();
        var dock = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(done, Dock.Bottom);
        dock.Children.Add(done);
        dock.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;
        samples.Changed += Fill;
        Closed += (_, _) => samples.Changed -= Fill;
        Fill();
    }

    private void Fill()
    {
        list.Children.Clear();
        foreach (var sample in Enumerable.Reverse(samples.Samples))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var delete = new Button { Content = "Delete", Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0), ToolTip = "Delete this sample" };
            delete.Click += (_, _) => samples.Delete(sample);
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = $"{sample.AppName}{(sample.WindowTitle is { Length: > 0 } title ? $" · {title}" : "")} · {sample.Date.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)}",
                Foreground = SystemColors.GrayTextBrush,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock { Text = sample.Text, TextWrapping = TextWrapping.Wrap, MaxHeight = 80, TextTrimming = TextTrimming.WordEllipsis });
            row.Children.Add(text);
            list.Children.Add(row);
        }
        if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = "No samples stored.", Foreground = SystemColors.GrayTextBrush });
    }
}
