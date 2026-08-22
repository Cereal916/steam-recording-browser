using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SteamRecordingBrowser.Services;

internal static class WindowThemeService
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void ApplyDarkTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var enabled = 1;
        if (DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkMode,
                ref enabled,
                Marshal.SizeOf<int>()) != 0)
        {
            DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkModeBefore20H1,
                ref enabled,
                Marshal.SizeOf<int>());
        }

        // These color attributes are supported on Windows 11. Older Windows
        // versions safely ignore them and still use immersive dark mode above.
        var borderColor = ToColorRef(0x2B, 0x32, 0x40);
        var captionColor = ToColorRef(0x15, 0x19, 0x22);
        var textColor = ToColorRef(0xF4, 0xF7, 0xFB);
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref borderColor, Marshal.SizeOf<int>());
        DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, Marshal.SizeOf<int>());
        DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, Marshal.SizeOf<int>());
    }

    private static int ToColorRef(byte red, byte green, byte blue) =>
        red | (green << 8) | (blue << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
