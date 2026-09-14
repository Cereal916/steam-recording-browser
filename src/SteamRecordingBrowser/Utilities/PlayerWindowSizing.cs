using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SteamRecordingBrowser.Utilities;

internal static class PlayerWindowSizing
{
    public static void FitToVideo(Window window, FrameworkElement videoArea) =>
        FitToVideo(window, videoArea, GetWorkArea(window));

    internal static void FitToVideo(Window window, FrameworkElement videoArea, Rect workArea)
    {
        if (window.WindowState != WindowState.Normal)
            return;

        window.UpdateLayout();
        var center = new Point(window.Left + window.ActualWidth / 2,
            window.Top + window.ActualHeight / 2);
        var maximumHeight = Math.Max(window.MinHeight, workArea.Height);
        window.Width = Math.Clamp(window.ActualWidth, window.MinWidth,
            Math.Max(window.MinWidth, workArea.Width));

        while (true)
        {
            window.UpdateLayout();
            // Include the measured controls, optional panels, and native title
            // bar instead of assuming a fixed amount of non-video space.
            var nonVideoHeight = window.ActualHeight - videoArea.ActualHeight;
            var desiredHeight = Math.Ceiling(nonVideoHeight + videoArea.ActualWidth * 9 / 16);
            if (desiredHeight <= maximumHeight || window.Width <= window.MinWidth)
            {
                window.Height = Math.Clamp(desiredHeight, window.MinHeight, maximumHeight);
                break;
            }

            // Narrow the window only when the full-width video would exceed
            // the monitor's work area. Remeasure since descriptions/tags wrap.
            window.Width = Math.Max(window.MinWidth,
                window.Width - Math.Ceiling((desiredHeight - maximumHeight) * 16 / 9));
        }

        window.UpdateLayout();
        window.Left = Math.Clamp(center.X - window.ActualWidth / 2, workArea.Left,
            Math.Max(workArea.Left, workArea.Right - window.ActualWidth));
        window.Top = Math.Clamp(center.Y - window.ActualHeight / 2, workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - window.ActualHeight));
    }

    private static Rect GetWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info) ||
            PresentationSource.FromVisual(window)?.CompositionTarget is not { } target)
            return SystemParameters.WorkArea;

        // Monitor coordinates are physical pixels; WPF sizes use device-
        // independent units on the player's current monitor.
        var transform = target.TransformFromDevice;
        return new Rect(transform.Transform(new Point(info.Work.Left, info.Work.Top)),
            transform.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
