using System.Windows;
using LibVLCSharp.Shared;
using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Services;

namespace SteamRecordingBrowser;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
                WindowThemeService.ApplyDarkTitleBar((Window)sender)));

        var splash = new SplashWindow();
        splash.SetProgress(2, "Starting application…");
        splash.Show();

        // Yield between the expensive early startup stages so the splash can
        // repaint instead of appearing frozen at a single percentage.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(async () =>
            {
                try
                {
                    splash.SetProgress(5, "Starting WPF interface…");
                    await System.Windows.Threading.Dispatcher.Yield(
                        System.Windows.Threading.DispatcherPriority.Background);

                    splash.SetProgress(9, "Loading native video libraries…");
                    await System.Windows.Threading.Dispatcher.Yield(
                        System.Windows.Threading.DispatcherPriority.Background);

                    // Native libVLC discovery and loading can take long enough
                    // for Rider's UI-freeze monitor to flag the dispatcher.
                    // Nothing uses libVLC until this completes, so initialize
                    // it away from the WPF UI thread and keep the splash live.
                    await Task.Run(() => Core.Initialize());

                    splash.SetProgress(14, "Video engine ready…");
                    await System.Windows.Threading.Dispatcher.Yield(
                        System.Windows.Threading.DispatcherPriority.Background);

                    splash.SetProgress(18, "Creating application services…");
                    await System.Windows.Threading.Dispatcher.Yield(
                        System.Windows.Threading.DispatcherPriority.Background);

                    var progress = new Progress<StartupProgress>(p =>
                    {
                        if (splash.IsVisible)
                            splash.SetProgress(p.Percent, p.Status);
                    });

                    var main = new MainWindow(progress);
                    MainWindow = main;

                    main.InitialLoadCompleted += (_, _) =>
                    {
                        try
                        {
                            if (splash.IsVisible)
                            {
                                // Ensure the user can actually see the final
                                // completed state before the splash disappears.
                                splash.SetProgress(
                                    100,
                                    "Ready");

                                var closeTimer =
                                    new System.Windows.Threading.DispatcherTimer
                                    {
                                        Interval = TimeSpan.FromMilliseconds(180)
                                    };

                                closeTimer.Tick += (_, _) =>
                                {
                                    closeTimer.Stop();
                                    splash.Close();
                                    main.Activate();
                                    OfferDesktopShortcut(main);
                                };

                                closeTimer.Start();
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.WriteException(
                                "Splash completion failed",
                                ex);

                            try { splash.Close(); } catch { }
                        }
                    };

                    main.Show();
                }
                catch (Exception ex)
                {
                    AppLogger.WriteException("Application startup failed", ex);

                    try
                    {
                        splash.Close();
                    }
                    catch
                    {
                    }

                    MessageBox.Show(
                        $"Steam Recording Browser could not start.\n\n{ex.Message}",
                        "Startup error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    Shutdown(-1);
                }
            }));
    }

    private static void OfferDesktopShortcut(Window owner)
    {
        var settingsService = new SettingsService();
        if (settingsService.Load().DesktopShortcutPromptShown)
            return;

        var result = MessageBox.Show(
            owner,
            "Would you like to create a desktop shortcut for Steam Recording Browser?",
            "Create desktop shortcut",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        // Remember either answer so the question is only shown on first run.
        settingsService.MarkDesktopShortcutPromptShown();

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            DesktopShortcutService.Create();
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Desktop shortcut creation failed", ex);
            MessageBox.Show(
                owner,
                $"The desktop shortcut could not be created.\n\n{ex.Message}",
                "Shortcut creation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
