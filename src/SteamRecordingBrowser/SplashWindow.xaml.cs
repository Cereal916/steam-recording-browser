using System.Windows;
using SteamRecordingBrowser.Models;

namespace SteamRecordingBrowser;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{AppInfo.Version}";
    }

    public void SetProgress(double percent, string status)
    {
        percent = Math.Clamp(percent, 0d, 100d);

        StartupProgressBar.Value = percent;
        PercentText.Text = $"{percent:0}%";
        StatusText.Text = status;
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }
}
