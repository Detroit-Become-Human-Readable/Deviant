using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Deviant.Desktop;

internal static class WindowPlacement
{
    internal static Rect Fit(Rect requested, Rect workArea)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0) return requested;
        var width = Math.Min(requested.Width, workArea.Width);
        var height = Math.Min(requested.Height, workArea.Height);
        return new Rect(Math.Clamp(requested.Left, workArea.Left, workArea.Right - width),
            Math.Clamp(requested.Top, workArea.Top, workArea.Bottom - height), width, height);
    }

    internal static void ApplyInitial(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(handle, out var bounds)) return;
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is not { } target) return;
        var transform = new System.Windows.Media.MatrixTransform(target.TransformFromDevice);
        var requested = transform.TransformBounds(bounds.ToRect());
        var workArea = transform.TransformBounds(info.Work.ToRect());
        var fitted = Fit(requested, workArea);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.MinWidth = Math.Min(window.MinWidth, workArea.Width);
        window.MinHeight = Math.Min(window.MinHeight, workArea.Height);
        window.Width = fitted.Width;
        window.Height = fitted.Height;
        window.Left = fitted.Left;
        window.Top = fitted.Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
}

