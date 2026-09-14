using System.Windows;
using System.Windows.Controls;
using Xunit;
using static SteamRecordingBrowser.Tests.Ux.IsolatedWpfTest;

namespace SteamRecordingBrowser.Tests.Ux;

[Trait("Category", "UX")]
public sealed class WindowSizeLimitsTests
{
    [Fact]
    public Task SimulatedLimits_AllowLayoutLargerThanTheHostScreen() => Run(host =>
    {
        // Always exceed this host's native limits, even on a large development
        // monitor, so a missing override reproduces the small CI screen failure.
        var size = new Size(SystemParameters.MaximumWindowTrackWidth + 128,
            SystemParameters.MaximumWindowTrackHeight + 128);
        var window = new Window { Width = size.Width, Height = size.Height, Content = new Grid() };
        using var sizeLimits = new WindowSizeLimits(window, size);

        host.ShowDialog(window, () =>
        {
            Assert.InRange(window.ActualWidth, size.Width - 1, size.Width + 1);
            Assert.InRange(window.ActualHeight, size.Height - 1, size.Height + 1);
            var content = (FrameworkElement)window.Content;
            Assert.InRange(window.ActualWidth - content.ActualWidth, 0, 64);
            Assert.InRange(window.ActualHeight - content.ActualHeight, 0, 96);
        });
    });
}
