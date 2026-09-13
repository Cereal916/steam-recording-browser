using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

namespace SteamRecordingBrowser.Tests.Ux;

/// <summary>
/// Runs real WPF controls on an undisplayed Win32 desktop. Never switches the
/// input desktop, injects global input, or falls back to the user's desktop.
/// </summary>
internal sealed class IsolatedWpfTest
{
    public static Task Run(Action<IsolatedWpfTest> test)
    {
        if (!PrivateDesktopProcess.IsWorker)
            return PrivateDesktopProcess.RunCurrentTest();

        return Task.Run(() => RunOnSta(test));
    }

    private static void RunOnSta(Action<IsolatedWpfTest> test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // New threads must inherit the child's private desktop before WPF initializes.
                if (!PrivateDesktopProcess.IsWorker)
                    throw new InvalidOperationException("WPF test desktop isolation verification failed.");

                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                try
                {
                    // Install shared styles before dialog constructors resolve StaticResource entries.
                    application.Resources = LoadApplicationTheme();
                    test(new IsolatedWpfTest());
                }
                finally
                {
                    application.Shutdown();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        }) { IsBackground = true, Name = "Isolated WPF test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("The isolated WPF test did not finish within 30 seconds.");

        failure?.Throw();
    }

    private IsolatedWpfTest() { }

    public bool? ShowDialog(Window window, Action test)
    {
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = 0;
        ExceptionDispatchInfo? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try { test(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            finally { window.Close(); }
        }));

        var result = window.ShowDialog();
        failure?.Throw();
        return result;
    }

    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    public static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"Missing {typeof(T).Name}: {name}");

    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    public static KeyEventArgs Key(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("The test control must be loaded before raising a key event.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(args);
        Pump();
        return args;
    }

    public static void Click(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button)
            ?? throw new InvalidOperationException("The button has no automation peer.");
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        Pump();
    }

    public static string Render(FrameworkElement element, string name, double scale = 1)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // A VisualBrush removes the element's parent offset/margin from the capture.
        // Composite transparent controls over their actual window background.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(element.RenderSize);
            drawing.DrawRectangle(Window.GetWindow(element)?.Background ?? Brushes.Transparent, null, bounds);
            drawing.DrawRectangle(new VisualBrush(element), null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(PrivateDesktopProcess.ArtifactDirectory, name + ".png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static ResourceDictionary LoadApplicationTheme()
    {
        // Use the actual shared styles without starting App, Steam scanning, or libVLC.
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var app = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestResources", "App.xaml"));
        var resources = new XElement(ns + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            app.Root!.Element(ns + "Application.Resources")!.Elements());
        return (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }
}
