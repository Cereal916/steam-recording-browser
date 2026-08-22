using System.IO;
using System.Runtime.InteropServices;

namespace SteamRecordingBrowser.Services;

internal static class DesktopShortcutService
{
    private const string ShortcutName = "Steam Recording Browser.lnk";

    public static string ShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ShortcutName);

    public static bool Exists => File.Exists(ShortcutPath);

    public static string Create()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("The application executable path could not be determined.");

        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopPath))
            throw new InvalidOperationException("The desktop folder could not be determined.");

        var shortcutPath = ShortcutPath;
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");

        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows shortcut support could not be started.");

            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(shortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = executablePath;
            dynamicShortcut.WorkingDirectory = AppContext.BaseDirectory;
            dynamicShortcut.IconLocation = $"{executablePath},0";
            dynamicShortcut.Description = "Browse and play Steam Game Recordings";
            dynamicShortcut.Save();

            AppLogger.Write($"Created desktop shortcut: {shortcutPath}");
            return shortcutPath;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);

            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }
}
