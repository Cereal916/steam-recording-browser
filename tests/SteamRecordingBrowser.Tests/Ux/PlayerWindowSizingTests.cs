using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Utilities;
using Xunit;
using static SteamRecordingBrowser.Tests.Ux.IsolatedWpfTest;

namespace SteamRecordingBrowser.Tests.Ux;

[Trait("Category", "UX")]
public sealed class PlayerWindowSizingTests
{
    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 1.5)]
    [InlineData(true, true, 2)]
    public Task Opening_ReservesFullWidthVideoAboveVisiblePanels(
        bool annotations, bool events, double scale) => Run(host =>
    {
        var window = LoadPlayer(annotations, events);
        var video = Find<Grid>(window, "VideoArea");
        var workArea = new Rect(0, 0, 1920, 1200);
        window.Loaded += (_, _) => PlayerWindowSizing.FitToVideo(window, video, workArea);

        host.ShowDialog(window, () =>
        {
            Assert.Equal(1100, window.ActualWidth);
            AssertVideoFits(window, video, workArea);
            AssertPanelFits(window, "AnnotationPanel", annotations);
            AssertPanelFits(window, "TimelineEventBrowser", events);
            Render((FrameworkElement)window.Content, $"player-opening-{annotations}-{events}-{scale:0.0}x", scale);

            // Startup sizing must leave subsequent manual resizing alone.
            window.Height = 700;
            Pump();
            Assert.Equal(700, window.ActualHeight);
        });
    });

    [Theory]
    [InlineData(1280, 900, true, true)]
    [InlineData(720, 700, false, false)]
    public Task Opening_OnSmallWorkAreaReducesWidthAndKeepsControlsOnScreen(
        double width, double height, bool annotations, bool events) => Run(host =>
    {
        var window = LoadPlayer(annotations, events);
        var video = Find<Grid>(window, "VideoArea");
        // Include a negative monitor origin and a taskbar offset.
        var workArea = new Rect(-width, 30, width, height);
        window.Loaded += (_, _) => PlayerWindowSizing.FitToVideo(window, video, workArea);

        host.ShowDialog(window, () =>
        {
            Assert.InRange(window.ActualWidth, window.MinWidth, 1099);
            AssertVideoFits(window, video, workArea);
            AssertPanelFits(window, "AnnotationPanel", annotations);
            AssertPanelFits(window, "TimelineEventBrowser", events);
            Render((FrameworkElement)window.Content, $"player-small-{width:0}-{height:0}");
        });
    });

    private static void AssertVideoFits(Window window, FrameworkElement video, Rect workArea)
    {
        // Allow for rounding the requested height and native window pixels.
        Assert.InRange(video.ActualHeight - video.ActualWidth * 9 / 16, -0.1, 2);
        Assert.InRange(window.Left, workArea.Left, workArea.Right - window.ActualWidth + 0.1);
        Assert.InRange(window.Top, workArea.Top, workArea.Bottom - window.ActualHeight + 0.1);
    }

    private static void AssertPanelFits(Window window, string name, bool visible)
    {
        var panel = Find<Border>(window, name);
        Assert.Equal(visible, panel.IsVisible);
        if (!visible) return;
        var content = (FrameworkElement)window.Content;
        var bounds = panel.TransformToAncestor(content).TransformBounds(new Rect(panel.RenderSize));
        Assert.True(bounds.Bottom <= content.ActualHeight + 0.1);
        Assert.True(bounds.Right <= content.ActualWidth + 0.1);
    }

    private static Window LoadPlayer(bool annotations, bool events)
    {
        // Exercise the actual player layout without media playback, native
        // input hooks, or any access to the user's recordings and metadata.
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestResources", "PlayerWindow.xaml"));
        var root = document.Root!;
        root.Attribute(x + "Class")!.Remove();
        string[] handlers = ["PreviewMouseDown", "PreviewMouseUp", "PreviewMouseMove", "MouseEnter",
            "MouseLeave", "ValueChanged", "KeyDown", "SizeChanged", "Click", "SelectionChanged"];
        root.DescendantsAndSelf().Attributes()
            .Where(attribute => attribute.Name.Namespace == XNamespace.None && handlers.Contains(attribute.Name.LocalName))
            .Remove();
        foreach (var nativeVideo in root.Descendants().Where(element => element.Name.LocalName == "VideoView").ToArray())
        {
            nativeVideo.ReplaceWith(new XElement(ns + "Viewbox",
                new XAttribute("Stretch", "Uniform"),
                new XElement(ns + "Border", new XAttribute("Width", 1600), new XAttribute("Height", 900),
                    new XAttribute("Background", "#163044"),
                    new XElement(ns + "TextBlock", new XAttribute("Text", "16:9 video"),
                        new XAttribute("FontSize", 48), new XAttribute("HorizontalAlignment", "Center"),
                        new XAttribute("VerticalAlignment", "Center")))));
        }

        var window = (Window)XamlReader.Parse(root.ToString());
        Find<Border>(window, "AnnotationPanel").Visibility = annotations ? Visibility.Visible : Visibility.Collapsed;
        Find<Border>(window, "TimelineEventBrowser").Visibility = events ? Visibility.Visible : Visibility.Collapsed;
        Find<TextBlock>(window, "ClipInfoText").Text = "Sample game • Sep 14, 2026 • Clip";
        Find<TextBlock>(window, "DescriptionText").Text =
            "A longer clip description that wraps across several lines, leaving room for the playback controls, " +
            "timeline events, and tags below a full-width video.";
        var tags = Find<WrapPanel>(window, "TagsPanel");
        foreach (var tag in new[] { "Boss fight", "Cooperative gameplay", "Favorite moments", "Replay later" })
            tags.Children.Add(new TextBlock { Text = tag, Margin = new Thickness(0, 0, 12, 6) });
        Find<ItemsControl>(window, "TimelineEventList").ItemsSource = Enumerable.Range(0, 8)
            .Select(index => new SteamTimelineEvent
            {
                Id = $"event-{index}", Type = "achievement", TypeLabel = "Achievement",
                Title = $"A memorable moment {index}", PositionSeconds = index * 30, Timestamp = DateTime.UnixEpoch
            }).ToArray();
        return window;
    }
}
