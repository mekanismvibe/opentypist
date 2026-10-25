using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenTypist;

/// Settings → accept keys (General), Autocorrect, Snippets and Apps: the
/// Windows side of SettingsView.swift's KeysSettings, AutocorrectSettings,
/// AppsSettings and ReplacementsSettingsView.swift.
public partial class SettingsWindow
{
    // Key recording: the next key pressed in this window (Esc cancels).

    private Button? recordingButton;
    private TextBlock? recordingMessage;
    private Action<KeyCombo>? onRecorded;
    private Action? afterRecording;

    private void StartRecording(Button button, TextBlock message, Action<KeyCombo> onKey, Action restore)
    {
        StopRecording();
        recordingButton = button;
        recordingMessage = message;
        onRecorded = onKey;
        afterRecording = restore;
        button.Content = "Press a key...";
        message.Visibility = Visibility.Collapsed;
    }

    private void StopRecording()
    {
        var restore = afterRecording;
        recordingButton = null;
        onRecorded = null;
        afterRecording = null;
        restore?.Invoke();
    }

    private void OnRecordingKey(object sender, KeyEventArgs e)
    {
        if (recordingButton is null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed or Key.None) return;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopRecording();
            return;
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        uint mapped = Win32.MapVirtualKey((uint)vk, Win32.MAPVK_VK_TO_CHAR) & 0x7FFF;
        string? character = mapped is > 0x20 and < 0x7F ? char.ToUpperInvariant((char)mapped).ToString() : null;
        var combo = new KeyCombo(vk, modifiers.HasFlag(ModifierKeys.Control), modifiers.HasFlag(ModifierKeys.Alt),
                                 modifiers.HasFlag(ModifierKeys.Shift), modifiers.HasFlag(ModifierKeys.Windows), character);
        if (combo.Problem is string problem)
        {
            recordingMessage!.Text = problem;
            recordingMessage.Visibility = Visibility.Visible;
            return;
        }
        // Save first, then redraw the buttons with the new key.
        onRecorded!(combo);
        StopRecording();
    }

    // General → Accept keys

    private void SetUpKeys()
    {
        PreviewKeyDown += OnRecordingKey;
        UpdateKeyButtons();
        WordKeyButton.Click += (_, _) => StartRecording(WordKeyButton, KeysMessage, combo =>
        {
            settings.AcceptKeys = settings.AcceptKeys with { Word = combo };
            settings.Save();
        }, UpdateKeyButtons);
        AllKeyButton.Click += (_, _) => StartRecording(AllKeyButton, KeysMessage, combo =>
        {
            settings.AcceptKeys = settings.AcceptKeys with { All = combo };
            settings.Save();
        }, UpdateKeyButtons);
        ResetKeysButton.Click += (_, _) =>
        {
            settings.AcceptKeys = AcceptKeys.Standard;
            settings.Save();
            UpdateKeyButtons();
        };
    }

    private void UpdateKeyButtons()
    {
        WordKeyButton.Content = settings.AcceptKeys.Word.DisplayName;
        AllKeyButton.Content = settings.AcceptKeys.All.DisplayName;
        ResetKeysButton.IsEnabled = settings.AcceptKeys != AcceptKeys.Standard;
        bool same = settings.AcceptKeys.Word == settings.AcceptKeys.All;
        KeysMessage.Text = same ? "Both actions use the same key; the whole suggestion wins." : "";
        KeysMessage.Visibility = same ? Visibility.Visible : Visibility.Collapsed;
        KeysMessage.Foreground = Brushes.DarkOrange;
    }

    // Autocorrect

    private void SetUpAutocorrect()
    {
        AutocorrectBox.IsChecked = settings.AutocorrectEnabled;
        AiTypoBox.IsChecked = settings.AiTypoFix;
        AutocorrectBox.Click += (_, _) => Update(() => settings.AutocorrectEnabled = AutocorrectBox.IsChecked == true);
        AiTypoBox.Click += (_, _) => Update(() => settings.AiTypoFix = AiTypoBox.IsChecked == true);
        SpellLanguagesText.Text = SpellCheck.Languages.Count > 0
            ? $"Spell checking in {string.Join(", ", SpellCheck.Languages)}, your Windows languages (Settings > Time & language > Language & region)."
            : "Windows has no spell checker for your languages, so only the lists are used.";
        RefreshLists();

        ImportListButton.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import autocorrect lists (.ahk or .txt)",
                Filter = "Autocorrect lists (*.ahk;*.txt)|*.ahk;*.txt|All files|*.*",
                Multiselect = true,
            };
            if (dialog.ShowDialog(this) != true || app.Autocorrector is not { } autocorrector) return;
            var results = new List<string>();
            foreach (var file in dialog.FileNames)
            {
                try { results.Add($"{Path.GetFileName(file)}: {autocorrector.ImportList(file)} entries"); }
                catch (IOException e) { results.Add($"{Path.GetFileName(file)}: {e.Message}"); }
            }
            ImportMessage.Text = string.Join("\n", results);
            RefreshLists();
        };
        ListsFolderButton.Click += (_, _) =>
        {
            Directory.CreateDirectory(Autocorrector.UserListsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Autocorrector.UserListsDirectory}\"") { UseShellExecute = true });
        };

    }

    private void RefreshLists()
    {
        if (app.Autocorrector is not { } autocorrector) return;
        var lines = new List<string> { $"Built-in: {autocorrector.BuiltInCount} entries (Wikipedia's Lists of common misspellings, CC BY-SA)" };
        lines.AddRange(autocorrector.UserListCounts.Select(l => $"{l.Name}: {l.Rules} entries"));
        ListsText.Text = string.Join("\n", lines);
    }


    // Snippets

    private sealed record SnippetRow(Snippet Snippet, string Shortcut, string Preview);

    private void SetUpSnippets()
    {
        SnippetsBox.IsChecked = settings.TextReplacementsEnabled;
        SnippetsBox.Click += (_, _) => Update(() => settings.TextReplacementsEnabled = SnippetsBox.IsChecked == true);
        RefreshSnippets();
        AddSnippetButton.Click += (_, _) =>
        {
            if (app.Replacements?.Add(ShortcutBox.Text, ExpansionBox.Text) is string problem)
            {
                SnippetMessage.Text = problem;
                SnippetMessage.Foreground = Brushes.Firebrick;
                return;
            }
            ShortcutBox.Text = "";
            ExpansionBox.Text = "";
            SnippetMessage.Text = "Added.";
            SnippetMessage.ClearValue(TextBlock.ForegroundProperty);
            RefreshSnippets();
        };
        RemoveSnippetButton.Click += (_, _) =>
        {
            foreach (var row in SnippetList.SelectedItems.Cast<SnippetRow>().ToList()) app.Replacements?.Remove(row.Snippet);
            RefreshSnippets();
        };
        ImportSnippetsButton.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import snippets (shortcut->text per line)", Filter = "Text files (*.txt)|*.txt|All files|*.*" };
            if (dialog.ShowDialog(this) != true || app.Replacements is null) return;
            try
            {
                var (added, skipped) = app.Replacements.Import(dialog.FileName);
                SnippetMessage.Text = $"Imported {added} snippet(s)." + (skipped.Count > 0 ? $" Skipped {skipped.Count}: {string.Join("; ", skipped.Take(3))}" : "");
                SnippetMessage.ClearValue(TextBlock.ForegroundProperty);
            }
            catch (IOException e)
            {
                SnippetMessage.Text = e.Message;
            }
            RefreshSnippets();
        };
    }

    private void RefreshSnippets()
    {
        SnippetList.ItemsSource = (app.Replacements?.Snippets ?? [])
            .Select(s => new SnippetRow(s, s.Shortcut, s.Expansion.Replace("\n", " ↵ ")))
            .ToList();
    }

    // Apps

    private void SetUpApps()
    {
        RefreshApps();
        AddDisabledAppButton.Click += (_, _) =>
        {
            foreach (var id in ChooseApps()) settings.SetDisabled(id, true);
            RefreshApps();
        };
        RemoveDisabledAppButton.Click += (_, _) =>
        {
            foreach (var id in DisabledAppsList.SelectedItems.Cast<string>().ToList()) settings.SetDisabled(id, false);
            RefreshApps();
        };
        RestoreDisabledAppsButton.Click += (_, _) =>
        {
            settings.DisabledApps = [.. AppSettings.DefaultDisabledApps];
            settings.Save();
            RefreshApps();
        };
        AddAppKeysButton.Click += (_, _) =>
        {
            foreach (var id in ChooseApps().Where(id => !settings.AppAcceptKeys.ContainsKey(id)))
            {
                settings.AppAcceptKeys[id] = new AcceptKeys(KeyCombo.RightArrow, KeyCombo.AltRightArrow);
            }
            settings.Save();
            RefreshApps();
        };
    }

    private void RefreshApps()
    {
        DisabledAppsList.ItemsSource = settings.DisabledApps.Order(StringComparer.OrdinalIgnoreCase).ToList();
        AppKeysPanel.Children.Clear();
        if (settings.AppAcceptKeys.Count == 0)
        {
            AppKeysPanel.Children.Add(new TextBlock { Text = "None: every app uses the keys from General.", Foreground = SystemColors.GrayTextBrush });
        }
        foreach (var (id, keys) in settings.AppAcceptKeys.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = id, Width = 190, VerticalAlignment = VerticalAlignment.Center });
            var word = new Button { Content = keys.Word.DisplayName, MinWidth = 120, ToolTip = "Accept next word" };
            var all = new Button { Content = keys.All.DisplayName, MinWidth = 120, ToolTip = "Accept whole suggestion", Margin = new Thickness(8, 0, 0, 0) };
            var remove = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Use the keys from General in this app" };
            word.Click += (_, _) => StartRecording(word, AppKeysMessage, combo =>
            {
                settings.AppAcceptKeys[id] = settings.AcceptKeysFor(id) with { Word = combo };
                settings.Save();
            }, RefreshApps);
            all.Click += (_, _) => StartRecording(all, AppKeysMessage, combo =>
            {
                settings.AppAcceptKeys[id] = settings.AcceptKeysFor(id) with { All = combo };
                settings.Save();
            }, RefreshApps);
            remove.Click += (_, _) =>
            {
                settings.AppAcceptKeys.Remove(id);
                settings.Save();
                RefreshApps();
            };
            row.Children.Add(word);
            row.Children.Add(all);
            row.Children.Add(remove);
            AppKeysPanel.Children.Add(row);
        }
    }

    /// Lowercase executable names, like the app ids used everywhere else.
    private List<string> ChooseApps()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose apps",
            Filter = "Programs (*.exe)|*.exe",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        return dialog.ShowDialog(this) == true ? dialog.FileNames.Select(f => Path.GetFileName(f).ToLowerInvariant()).ToList() : [];
    }
}
