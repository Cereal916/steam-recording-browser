using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Services;

namespace SteamRecordingBrowser.Dialogs;

public partial class TagEditorDialog : Window
{
    private readonly TagEditorModel _editor;
    private bool _updatingInput;

    public IReadOnlyList<string> Tags => _editor.Tags.ToArray();

    public TagEditorDialog(IEnumerable<string> initialTags, IEnumerable<string> knownTags)
    {
        _editor = new TagEditorModel(initialTags, knownTags);
        InitializeComponent();
        TagItems.ItemsSource = _editor.Tags;
        UpdateSuggestions();
        SourceInitialized += (_, _) => WindowThemeService.ApplyDarkTitleBar(this);
        Loaded += (_, _) => TagInput.Focus();
        Deactivated += (_, _) => SuggestionsPopup.IsOpen = false;
        Closed += (_, _) => SuggestionsPopup.IsOpen = false;
    }

    private void TagInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingInput) return;

        // Commit complete comma-separated tokens, keeping the final token available for completion.
        var lastComma = TagInput.Text.LastIndexOf(',');
        if (lastComma >= 0)
        {
            _editor.AddTags(TagInput.Text[..lastComma]);
            SetInput(TagInput.Text[(lastComma + 1)..].TrimStart());
        }

        UpdateSuggestions();
    }

    private void UpdateSuggestions()
    {
        var query = TagInput.Text.Trim();
        var suggestions = _editor.GetSuggestions(query);
        SuggestionsList.ItemsSource = suggestions;
        SuggestionsList.SelectedIndex = -1;
        SuggestionsPopup.IsOpen = TagInput.IsKeyboardFocused && suggestions.Count > 0;
        EmptyTagsText.Visibility = _editor.Tags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddButton.IsEnabled = query.Length > 0;

        SetStatus(query.Length == 0
            ? suggestions.Count > 0
                ? "Choose a tag with ↑/↓, or type to filter or add a new tag."
                : "Type to add a new tag."
            : _editor.Tags.Contains(query, StringComparer.OrdinalIgnoreCase)
                ? "This tag is already added."
                : suggestions.Count > 0
                    ? $"{suggestions.Count} matching tags. Use ↑/↓ to choose, Enter or Tab to add."
                    : "New tag. Press Enter or Add to add it.");
    }

    private void TagInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                if (!SuggestionsPopup.IsOpen) UpdateSuggestions();
                if (SuggestionsList.Items.Count > 0)
                {
                    var index = SuggestionsList.SelectedIndex;
                    SuggestionsList.SelectedIndex = e.Key == Key.Down
                        ? Math.Min(index + 1, SuggestionsList.Items.Count - 1)
                        : index < 0 ? SuggestionsList.Items.Count - 1 : Math.Max(index - 1, 0);
                    var selected = (string)SuggestionsList.SelectedItem;
                    SuggestionsList.ScrollIntoView(selected);
                    SetStatus($"{selected}. Press Enter or Tab to add. Escape closes suggestions.");
                }
                e.Handled = true;
                break;
            case Key.Enter:
                CommitInput(useSuggestion: true);
                // Enter adds a tag without activating the dialog's default Save button.
                e.Handled = true;
                break;
            case Key.Tab:
                if (!string.IsNullOrWhiteSpace(TagInput.Text) ||
                    (SuggestionsPopup.IsOpen && SuggestionsList.SelectedItem is string))
                {
                    CommitInput(useSuggestion: true);
                    e.Handled = true;
                }
                break;
            case Key.Escape when SuggestionsPopup.IsOpen:
                SuggestionsPopup.IsOpen = false;
                SetStatus("Suggestions closed. Press Enter to add the typed tag or ↓ to show suggestions.");
                e.Handled = true;
                break;
        }
    }

    private void SuggestionsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(SuggestionsList, source) is ListBoxItem { Content: string tag })
        {
            AddTags(tag);
            e.Handled = true;
        }
    }

    private void TagInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateSuggestions();

    private void TagInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        SuggestionsPopup.IsOpen = false;

    private void CommitInput(bool useSuggestion)
    {
        var text = useSuggestion && SuggestionsPopup.IsOpen && SuggestionsList.SelectedItem is string selected
            ? selected
            : TagInput.Text;
        AddTags(text);
    }

    private void AddTags(string text)
    {
        var added = _editor.AddTags(text);
        SetInput("");
        TagInput.Focus();
        UpdateSuggestions();
        if (!string.IsNullOrWhiteSpace(text))
            SetStatus(added == 0 ? "This tag is already added." : $"Added {added} tag{(added == 1 ? "" : "s")}. Type to add more.");
    }

    private void SetInput(string text)
    {
        _updatingInput = true;
        TagInput.Text = text;
        TagInput.CaretIndex = text.Length;
        _updatingInput = false;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        UIElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void Add_Click(object sender, RoutedEventArgs e) => CommitInput(useSuggestion: false);

    private void RemoveTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string tag })
        {
            _editor.RemoveTag(tag);
            TagInput.Focus();
            UpdateSuggestions();
            SetStatus($"Removed {tag}.");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Save also includes text that has not yet been committed with Enter or a comma.
        _editor.AddTags(TagInput.Text);
        DialogResult = true;
    }
}
