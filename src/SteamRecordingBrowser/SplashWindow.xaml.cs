using System.Windows;

namespace SteamRecordingBrowser;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
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
