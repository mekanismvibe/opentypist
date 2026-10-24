using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenTypist;

/// Minimal settings (phase 1): general options and the model picker.
public partial class SettingsWindow : Window
{
    private readonly TrayApp app;
    private readonly AppSettings settings;
    private bool loading;

    public SettingsWindow(TrayApp app)
    {
        this.app = app;
        settings = app.Settings;
        InitializeComponent();

        foreach (var length in Enum.GetValues<CompletionLength>()) LengthBox.Items.Add(new ComboBoxItem { Content = length.Title(), Tag = length });
        foreach (var level in Enum.GetValues<ConfidenceLevel>()) ConfidenceBox.Items.Add(new ComboBoxItem { Content = level.Title(), Tag = level });

        LoadValues();
        EnabledBox.Click += (_, _) => Update(() => settings.Enabled = EnabledBox.IsChecked == true);
        TrailingSpaceBox.Click += (_, _) => Update(() => settings.AcceptTrailingSpace = TrailingSpaceBox.IsChecked == true);
        AppContextBox.Click += (_, _) => Update(() => settings.UseAppContext = AppContextBox.IsChecked == true);
        GpuBox.Click += (_, _) => Update(() => settings.UseGpu = GpuBox.IsChecked == true);
        LoginBox.Click += (_, _) => AppSettings.LaunchAtLogin = LoginBox.IsChecked == true;
        LengthBox.SelectionChanged += (_, _) => Update(() => settings.Length = (CompletionLength)((ComboBoxItem)LengthBox.SelectedItem).Tag);
        ConfidenceBox.SelectionChanged += (_, _) =>
        {
            Update(() => settings.Confidence = (ConfidenceLevel)((ComboBoxItem)ConfidenceBox.SelectedItem).Tag);
            UpdateConfidenceHelp();
        };
        ChooseFileButton.Click += (_, _) => ChooseFile();
        OpenFolderButton.Click += (_, _) =>
        {
            Directory.CreateDirectory(app.Models.ModelsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{app.Models.ModelsDirectory}\"") { UseShellExecute = true });
        };

        app.Models.DownloadChanged += OnDownloadChanged;
        app.Engine.StateChanged += OnEngineChanged;
        AppStatus.Changed += RefreshStatus;
        Closed += (_, _) =>
        {
            app.Models.DownloadChanged -= OnDownloadChanged;
            app.Engine.StateChanged -= OnEngineChanged;
            AppStatus.Changed -= RefreshStatus;
        };

        HardwareText.Text = "This PC: " + Hardware.Current.Summary;
        BuildModelList();
        RefreshStatus();
    }

    public void SelectModelTab() => Tabs.SelectedItem = ModelTab;

    private void OnDownloadChanged() { BuildModelList(); RefreshStatus(); }

    private void OnEngineChanged(EngineService.State state) { BuildModelList(); RefreshStatus(); }

    private void LoadValues()
    {
        loading = true;
        EnabledBox.IsChecked = settings.Enabled;
        TrailingSpaceBox.IsChecked = settings.AcceptTrailingSpace;
        AppContextBox.IsChecked = settings.UseAppContext;
        GpuBox.IsChecked = settings.UseGpu;
        LoginBox.IsChecked = AppSettings.LaunchAtLogin;
        LengthBox.SelectedIndex = (int)settings.Length;
        ConfidenceBox.SelectedIndex = (int)settings.Confidence;
        KeysText.Text = $"{settings.AcceptKeys.Word.DisplayName} accepts the next word, {settings.AcceptKeys.All.DisplayName} the whole suggestion, Esc dismisses it.";
        DisabledAppsText.Text = string.Join(", ", settings.DisabledApps) + ". Use \"Disable in <app>\" in the tray menu to add or remove the app you were just in.";
        UpdateConfidenceHelp();
        loading = false;
    }

    private void UpdateConfidenceHelp()
    {
        ConfidenceHelp.Text = settings.Confidence switch
        {
            ConfidenceLevel.More => "Shows a suggestion more often; more of them will be wrong.",
            ConfidenceLevel.Balanced => "Measured on Gemma 4 E2B: a suggestion 74% of the time, half of the suggested words right, and half-typed words finished correctly about 9 times in 10.",
            _ => "Only words the model is quite sure about.",
        };
    }

    private void Update(Action change)
    {
        if (loading) return;
        change();
        settings.Save();
    }

    public void RefreshStatus()
    {
        StatusText.Text = app.StatusText();
        LastCheckText.Text = "Last check: " + AppStatus.LastCheck;
        var download = app.Models.Download;
        DownloadProgress.Visibility = download.Phase == ModelManager.Phase.Downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Value = download.Percent;
        DownloadText.Text = download.Phase switch
        {
            ModelManager.Phase.Downloading => $"Downloading {ModelCatalog.Model(download.ModelId)?.Name}: {download.Received / 1e9:0.00} of {download.Total / 1e9:0.00} GB",
            ModelManager.Phase.Verifying => "Checking the download (SHA-256)...",
            ModelManager.Phase.Failed => $"Download failed: {download.Message}",
            _ => app.StatusText(),
        };
    }

    private void BuildModelList()
    {
        ModelList.Children.Clear();
        var recommended = ModelCatalog.Recommended(Hardware.Current);
        var activePath = app.Models.ActiveModelPath(settings.SelectedModelId, settings.CustomModelPath);
        foreach (var model in ModelCatalog.All)
        {
            bool downloaded = app.Models.IsDownloaded(model);
            bool active = activePath == app.Models.FileFor(model);
            var panel = new StackPanel();
            var title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14 };
            title.Inlines.Add(model.Name);
            if (model == recommended) title.Inlines.Add(new System.Windows.Documents.Run("   Recommended for this PC") { Foreground = Brushes.SeaGreen, FontWeight = FontWeights.Normal, FontSize = 12 });
            if (active) title.Inlines.Add(new System.Windows.Documents.Run("   In use") { Foreground = SystemColors.HighlightBrush, FontWeight = FontWeights.Normal, FontSize = 12 });
            panel.Children.Add(title);
            panel.Children.Add(new TextBlock { Text = model.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            panel.Children.Add(new TextBlock
            {
                Text = $"{model.Family} · {model.SizeText} · speed: {model.Speed} · quality: {model.Quality}",
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 2, 0, 0),
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            if (app.Models.IsDownloading && app.Models.Download.ModelId == model.Id)
            {
                buttons.Children.Add(MakeButton("Cancel download", () => app.Models.CancelDownload()));
            }
            else if (!downloaded)
            {
                var download = MakeButton($"Download ({model.SizeText})", () => app.Models.StartDownload(model));
                download.IsEnabled = !app.Models.IsDownloading;
                buttons.Children.Add(download);
            }
            else
            {
                if (!active)
                {
                    buttons.Children.Add(MakeButton("Use this model", () =>
                    {
                        settings.SelectedModelId = model.Id;
                        settings.CustomModelPath = null;
                        settings.Save();
                        BuildModelList();
                    }));
                }
                buttons.Children.Add(MakeButton("Delete", () =>
                {
                    if (MessageBox.Show(this, $"Delete {model.Name} ({model.SizeText}) from this PC?", "OpenTypist", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                    if (active) app.Engine.Unload();
                    app.Models.Delete(model);
                    app.LoadModel(force: true);
                    BuildModelList();
                }));
            }
            panel.Children.Add(buttons);
            ModelList.Children.Add(new Border
            {
                Child = panel,
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8),
                BorderThickness = new Thickness(1),
                BorderBrush = active ? SystemColors.HighlightBrush : SystemColors.ControlDarkBrush,
                CornerRadius = new CornerRadius(4),
            });
        }
        if (!string.IsNullOrEmpty(settings.CustomModelPath))
        {
            var custom = new StackPanel { Orientation = Orientation.Horizontal };
            custom.Children.Add(new TextBlock { Text = $"Using {Path.GetFileName(settings.CustomModelPath)}", VerticalAlignment = VerticalAlignment.Center });
            custom.Children.Add(MakeButton("Use the catalog model instead", () =>
            {
                settings.CustomModelPath = null;
                settings.Save();
                BuildModelList();
            }, new Thickness(8, 0, 0, 0)));
            ModelList.Children.Insert(0, new Border { Child = custom, Margin = new Thickness(0, 0, 0, 8) });
        }
    }

    private static Button MakeButton(string title, Action action, Thickness? margin = null)
    {
        var button = new Button { Content = title, Padding = new Thickness(10, 3, 10, 3), Margin = margin ?? new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private void ChooseFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a GGUF model", Filter = "GGUF models (*.gguf)|*.gguf|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (!dialog.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, $"{Path.GetFileName(dialog.FileName)} is not a .gguf file", "OpenTypist");
            return;
        }
        settings.CustomModelPath = dialog.FileName;
        settings.Save();
        BuildModelList();
    }
}
