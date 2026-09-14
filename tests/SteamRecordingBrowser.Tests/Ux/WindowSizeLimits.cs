using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SteamRecordingBrowser.Tests.Ux;

/// <summary>
/// Supplies native sizing limits for one isolated test window without changing
/// the machine's display settings or depending on its monitor resolution.
/// </summary>
internal sealed class WindowSizeLimits : IDisposable
{
    private const int WmGetMinMaxInfo = 0x0024;
    private readonly Window _window;
    private readonly Size _maximumSize;
    private readonly Size _initialSize;
    private HwndSource? _source;

    public WindowSizeLimits(Window window, Size maximumSize)
    {
        if (!PrivateDesktopProcess.IsWorker)
            throw new InvalidOperationException("Window size simulation requires the isolated test desktop.");

        _window = window;
        _maximumSize = maximumSize;
        _initialSize = new Size(window.Width, window.Height);
        _window.SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source.AddHook(ApplySizeLimits);

        // Creation already delivered WM_GETMINMAXINFO using the real screen.
        // Refresh WPF's cached limits before its first measure/arrange pass.
        var info = new MinMaxInfo();
        SendMessage(_source.Handle, WmGetMinMaxInfo, IntPtr.Zero, ref info);

        // CreateWindowEx may also have capped the HWND itself before the hook
        // existed. Restore the requested size with the simulated limits active.
        var initial = _source.CompositionTarget.TransformToDevice
            .Transform(new Point(_initialSize.Width, _initialSize.Height));
        if (!SetWindowPos(_source.Handle, IntPtr.Zero, 0, 0,
                (int)Math.Round(initial.X), (int)Math.Round(initial.Y),
                0x0002 | 0x0004 | 0x0010)) // SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size the isolated test window.");
    }

    private IntPtr ApplySizeLimits(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo && _source?.CompositionTarget is { } target)
        {
            var maximum = target.TransformToDevice
                .Transform(new Point(_maximumSize.Width, _maximumSize.Height));
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MaximumTrackSize = new NativePoint { X = (int)Math.Ceiling(maximum.X), Y = (int)Math.Ceiling(maximum.Y) };
            Marshal.StructureToPtr(info, lParam, false);
            // Leave the message unhandled so WPF caches these bounds and still
            // applies the window's own MinWidth/MinHeight/MaxWidth/MaxHeight.
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _window.SourceInitialized -= OnSourceInitialized;
        _source?.RemoveHook(ApplySizeLimits);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaximumSize, MaximumPosition, MinimumTrackSize, MaximumTrackSize;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, ref MinMaxInfo lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
