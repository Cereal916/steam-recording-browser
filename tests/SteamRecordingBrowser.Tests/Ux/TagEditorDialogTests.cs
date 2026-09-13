using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SteamRecordingBrowser.Dialogs;
using Xunit;
using static SteamRecordingBrowser.Tests.Ux.IsolatedWpfTest;

namespace SteamRecordingBrowser.Tests.Ux;

[Trait("Category", "UX")]
public sealed class TagEditorDialogTests
{
    [Fact]
    public Task Focus_ShowsAvailableTagsAndTypingThenClearingFiltersThem() => Run(host =>
    {
        var dialog = CreateDialog();
        host.ShowDialog(dialog, () =>
        {
            var input = Find<TextBox>(dialog, "TagInput");
            var popup = Find<Popup>(dialog, "SuggestionsPopup");
            var list = Find<ListBox>(dialog, "SuggestionsList");

            Assert.True(input.IsKeyboardFocused);
            Assert.True(popup.IsOpen);
            Assert.Equal(new[] { "Boss", "Boss fight", "Final boss", "Funny" }, list.Items.Cast<string>());
            Assert.False(Find<Button>(dialog, "AddButton").IsEnabled);
            Render((FrameworkElement)dialog.Content, "tags-initial");
            Render((FrameworkElement)popup.Child, "tags-initial-suggestions");

            input.Text = "bOs";
            Pump();
            Assert.True(popup.IsOpen);
            Assert.Equal(new[] { "Boss", "Boss fight", "Final boss" }, list.Items.Cast<string>());
            Render((FrameworkElement)popup.Child, "tags-filtered-suggestions");

            input.Clear();
            Pump();
            Assert.True(popup.IsOpen);
            Assert.Equal(4, list.Items.Count);

            Descendants<Button>(dialog).Single(button => button.IsCancel).Focus();
            Pump();
            Assert.False(popup.IsOpen);
            input.Focus();
            Pump();
            Assert.True(popup.IsOpen);
        });
    });

    [Fact]
    public Task Keyboard_SelectsAddsAndDismissesWithoutSaving() => Run(host =>
    {
        var dialog = CreateDialog();
        host.ShowDialog(dialog, () =>
        {
            var input = Find<TextBox>(dialog, "TagInput");
            var popup = Find<Popup>(dialog, "SuggestionsPopup");
            var list = Find<ListBox>(dialog, "SuggestionsList");
            Assert.True(Key(input, System.Windows.Input.Key.Down).Handled);
            Assert.Equal("Boss", list.SelectedItem);
            Assert.True(Key(input, System.Windows.Input.Key.Enter).Handled);
            Assert.Contains("Boss", dialog.Tags);
            Assert.True(dialog.IsVisible);
            Assert.True(input.IsKeyboardFocused);
            Assert.DoesNotContain("Boss", list.Items.Cast<string>());
            Assert.True(popup.IsOpen);

            Key(input, System.Windows.Input.Key.Up);
            Assert.Equal("Funny", list.SelectedItem);
            Assert.True(Key(input, System.Windows.Input.Key.Tab).Handled);
            Assert.Contains("Funny", dialog.Tags);
            Assert.True(Key(input, System.Windows.Input.Key.Escape).Handled);
            Assert.False(popup.IsOpen);
            Assert.True(dialog.IsVisible);
            Assert.False(Key(input, System.Windows.Input.Key.Escape).Handled);

            Key(input, System.Windows.Input.Key.Down);
            Assert.True(popup.IsOpen);
        });
    });

    [Fact]
    public Task Mouse_SelectsSuggestionsAndRemovesChips() => Run(host =>
    {
        var dialog = CreateDialog();
        host.ShowDialog(dialog, () =>
        {
            var list = Find<ListBox>(dialog, "SuggestionsList");
            list.UpdateLayout();
            var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(1));
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Pump();
            Assert.Contains("Boss fight", dialog.Tags);
            var remove = Descendants<Button>(Find<ItemsControl>(dialog, "TagItems"))
                .Single(button => Equals(button.DataContext, "Boss fight"));
            Assert.Equal("Remove tag Boss fight", AutomationProperties.GetName(remove));
            Click(remove);
            Assert.DoesNotContain("Boss fight", dialog.Tags);
            Assert.Contains("Boss fight", list.Items.Cast<string>());
            Assert.True(Find<TextBox>(dialog, "TagInput").IsKeyboardFocused);
        });
    });

    [Fact]
    public Task Paste_DeduplicatesAndSaveIncludesPendingText() => Run(host =>
    {
        var original = new[] { "Favorite" };
        var dialog = new TagEditorDialog(original, new[] { "Boss", "Favorite" });
        var saved = host.ShowDialog(dialog, () =>
        {
            Find<TextBox>(dialog, "TagInput").Text = "BOSS, boss, New tag, pending";
            Pump();
            Assert.Equal(new[] { "Favorite", "Boss", "New tag" }, dialog.Tags);
            Assert.Equal("pending", Find<TextBox>(dialog, "TagInput").Text);
            Click(Descendants<Button>(dialog).Single(button => button.IsDefault));
            Assert.False(dialog.IsVisible);
        });
        Assert.True(saved);
        Assert.Equal(new[] { "Favorite", "Boss", "New tag", "pending" }, dialog.Tags);
        Assert.Equal(new[] { "Favorite" }, original);
    });

    [Fact]
    public Task Cancel_DiscardsChanges() => Run(host =>
    {
        var original = new[] { "Favorite" };
        var dialog = new TagEditorDialog(original, []);
        var saved = host.ShowDialog(dialog, () =>
        {
            Find<TextBox>(dialog, "TagInput").Text = "Draft, pending";
            Pump();
            Click(Descendants<Button>(dialog).Single(button => button.IsCancel));
            Assert.False(dialog.IsVisible);
        });
        Assert.False(saved);
        Assert.Equal(new[] { "Favorite" }, original);
    });

    [Fact]
    public Task NoAvailableTags_ClosesDropdownAndAllowsNewTags() => Run(host =>
    {
        var dialog = new TagEditorDialog([], []);
        host.ShowDialog(dialog, () =>
        {
            var input = Find<TextBox>(dialog, "TagInput");
            Assert.False(Find<Popup>(dialog, "SuggestionsPopup").IsOpen);
            Assert.Equal(Visibility.Visible, Find<TextBlock>(dialog, "EmptyTagsText").Visibility);
            input.Text = "New tag";
            Key(input, System.Windows.Input.Key.Enter);
            Assert.Equal(new[] { "New tag" }, dialog.Tags);
            Assert.Equal(Visibility.Collapsed, Find<TextBlock>(dialog, "EmptyTagsText").Visibility);
            Assert.False(Find<Popup>(dialog, "SuggestionsPopup").IsOpen);
        });
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public Task LongTags_RenderWithoutClipping(double scale) => Run(host =>
    {
        const string longTag = "A long tag that wraps onto multiple lines while keeping its remove button visible and its final line readable";
        var dialog = new TagEditorDialog(new[] { longTag }, Enumerable.Range(1, 30).Select(n => $"Highlight {n:00}"));
        host.ShowDialog(dialog, () =>
        {
            var chips = Find<ItemsControl>(dialog, "TagItems");
            var label = Descendants<TextBlock>(chips).Single(block => block.Text == longTag);
            var remove = Descendants<Button>(chips).Single();
            var labelBounds = label.TransformToAncestor(chips).TransformBounds(new Rect(label.RenderSize));
            var buttonBounds = remove.TransformToAncestor(chips).TransformBounds(new Rect(remove.RenderSize));
            Assert.True(label.ActualHeight > 20, "The long label should wrap.");
            Assert.True(labelBounds.Right <= buttonBounds.Left, "Tag text must not overlap its remove button.");
            Assert.True(buttonBounds.Right <= chips.ActualWidth, "The remove button must fit inside the tag area.");
            Assert.True(label.ActualHeight >= label.DesiredSize.Height, "The last line must not be clipped.");
            Render((FrameworkElement)dialog.Content, $"tags-long-{scale:0.0}x", scale);
            var popup = Find<Popup>(dialog, "SuggestionsPopup");
            var list = Find<ListBox>(dialog, "SuggestionsList");
            Key(Find<TextBox>(dialog, "TagInput"), System.Windows.Input.Key.Up);
            Assert.Equal("Highlight 30", list.SelectedItem);
            Assert.True(Descendants<ScrollViewer>(list).Single().ScrollableHeight > 0);
            Render((FrameworkElement)popup.Child, $"tags-scrolled-{scale:0.0}x", scale);
        });
    });

    private static TagEditorDialog CreateDialog() => new(new[] { "Favorite" },
        new[] { "Boss", "Boss fight", "Final boss", "Funny", "Favorite" });
}
