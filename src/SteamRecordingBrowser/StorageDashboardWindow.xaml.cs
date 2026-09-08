using System.Windows;
using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Services;

namespace SteamRecordingBrowser;

public partial class StorageDashboardWindow : Window
{
    public StorageDashboardWindow(IReadOnlyCollection<RecordingItem> recordings)
    {
        InitializeComponent();
        UpdateRecordings(recordings);
    }

    public void UpdateRecordings(IReadOnlyCollection<RecordingItem> recordings)
    {
        DataContext = StorageDashboardService.Build(recordings);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
