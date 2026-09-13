using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Xml.Linq;
using SteamRecordingBrowser.Models;
using Xunit;
using static SteamRecordingBrowser.Tests.Ux.IsolatedWpfTest;

namespace SteamRecordingBrowser.Tests.Ux;

[Trait("Category", "UX")]
public sealed class ClipScrollingTests
{
    [Theory]
    [InlineData("RecordingList", 850, 600, 1)]
    [InlineData("RecordingList", 1180, 820, 2)]
    [InlineData("RecordingTable", 850, 600, 1)]
    [InlineData("RecordingTable", 1180, 820, 1.5)]
    [InlineData("TileRecordingList", 850, 600, 1)]
    public Task Wheel_MovesInSmallPixelStepsAndPreservesScrollingControls(
        string viewName, double width, double height, double scale) => Run(host =>
    {
        var window = LoadClipView(viewName);
        window.Width = width;
        window.Height = height;
        var view = Find<ItemsControl>(window, viewName);
        view.ItemsSource = Enumerable.Range(1, 200).Select(CreateRecording).ToArray();

        host.ShowDialog(window, () =>
        {
            var scroll = Descendants<ScrollViewer>(view).First();
            Assert.True(scroll.ScrollableHeight > scroll.ViewportHeight);
            var first = Assert.IsAssignableFrom<FrameworkElement>(view.ItemContainerGenerator.ContainerFromIndex(0));
            var originalTop = first.TransformToAncestor(view).Transform(new Point()).Y;

            Wheel(first, -120);
            // Follow the native Windows line preference, but measure lines in
            // pixels rather than whole 190px clip cards or full table rows.
            var expected = SystemParameters.WheelScrollLines < 0
                ? scroll.ViewportHeight
                : 16 * SystemParameters.WheelScrollLines;
            Assert.Equal(expected, scroll.VerticalOffset, precision: 2);
            if (expected < first.ActualHeight)
            {
                var scrolledTop = first.TransformToAncestor(view).Transform(new Point()).Y;
                Assert.Equal(expected, originalTop - scrolledTop, precision: 2);
            }

            Render(view, $"clips-{viewName}-wheel-{scale:0.0}x", scale);
            Wheel(scroll, 120);
            Assert.Equal(0, scroll.VerticalOffset);

            // Pixel scrolling must retain virtualization for the two views
            // that already virtualize, instead of measuring the entire library.
            if (viewName != "TileRecordingList")
            {
                Assert.True(scroll.CanContentScroll);
                var realized = Enumerable.Range(0, view.Items.Count)
                    .Count(index => view.ItemContainerGenerator.ContainerFromIndex(index) is not null);
                Assert.InRange(realized, 1, view.Items.Count / 2);
            }

            scroll.PageDown();
            Pump();
            Assert.True(scroll.VerticalOffset >= scroll.ViewportHeight / 2);

            if (view is DataGrid)
            {
                Assert.True(scroll.ScrollableWidth > 0);
                var verticalOffset = scroll.VerticalOffset;
                scroll.ScrollToHorizontalOffset(80);
                Pump();
                Assert.Equal(80, scroll.HorizontalOffset);
                Assert.Equal(verticalOffset, scroll.VerticalOffset);
            }

            scroll.ScrollToBottom();
            Pump();
            Wheel(scroll, -120);
            Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, precision: 2);
            Render(view, $"clips-{viewName}-bottom-{scale:0.0}x", scale);
            scroll.ScrollToTop();
            Pump();
            Wheel(scroll, 120);
            Assert.Equal(0, scroll.VerticalOffset);
        });
    });

    private static void Wheel(UIElement target, int delta)
    {
        target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent
        });
        Pump();
    }

    private static Window LoadClipView(string name)
    {
        // Load the real view, item templates, and styles without constructing
        // MainWindow, scanning the user's library, or initializing libVLC.
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestResources", "MainWindow.xaml"));
        var root = document.Root!;
        var view = new XElement(root.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name));
        root.Elements().Where(element => element.Name != ns + "Window.Resources").Remove();
        root.Add(view);
        root.Attribute(x + "Class")!.Remove();
        root.SetAttributeValue("Title", "Clip scrolling test");
        root.Descendants(ns + "EventSetter").Remove();

        // These handlers invoke application services, not the native scrolling
        // being tested. The rest of the view's XAML is kept intact.
        string[] events = ["MouseDoubleClick", "MouseEnter", "MouseLeave", "Click", "Opened", "DragCompleted", "ColumnReordered"];
        root.DescendantsAndSelf().Attributes()
            .Where(attribute => attribute.Name.Namespace == XNamespace.None && events.Contains(attribute.Name.LocalName))
            .Remove();
        return (Window)XamlReader.Parse(root.ToString());
    }

    private static RecordingItem CreateRecording(int index) => new()
    {
        Path = $@"C:\sample-clips\clip-{index}\session.mpd",
        Folder = $"Clip {index:000}",
        GameId = "480",
        GameName = $"Sample game — clip {index:000}",
        Timestamp = DateTime.UnixEpoch.AddMinutes(index),
        SizeBytes = 10_000_000,
        DurationSeconds = 30,
        Description = "Sample clip for checking small wheel movements and keeping your place in the library."
    };
}
