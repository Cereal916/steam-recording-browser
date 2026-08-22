using System.Windows;

namespace SteamRecordingBrowser.Dialogs;

public partial class TextEntryDialog : Window
{
    public string Value => ValueBox.Text;

    public TextEntryDialog(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue ?? "";
        ValueBox.SelectAll();
        Loaded += (_, _) => ValueBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
