using System;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace VajraPulse.Services;

public record BatterySnapshot(bool HasBattery, int Percent, bool IsPluggedIn, bool IsCharging);

public static class EnvironmentService
{
    public static BatterySnapshot ReadBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return new(false, -1, false, false);
        bool battery = s.BatteryFlag != 255 && (s.BatteryFlag & 128) == 0;
        return new(battery, s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent, s.ACLineStatus == 1, battery && (s.BatteryFlag & 8) != 0);
    }

    public static Rect PrimaryScreenBounds() => ToRect((Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0]).Bounds);
    public static Rect[] AllScreenBounds() => Array.ConvertAll(Forms.Screen.AllScreens, s => ToRect(s.Bounds));
    public static Rect ScreenBoundsAt(double x, double y) => ToRect(Forms.Screen.FromPoint(new System.Drawing.Point((int)x, (int)y)).Bounds);
    private static Rect ToRect(System.Drawing.Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    public static bool IsFullscreenAppActive(IntPtr ownWindow, Rect? islandMonitor = null)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ownWindow || foreground == GetShellWindow() || foreground == GetDesktopWindow() || IsIconic(foreground)) return false;
        if (IsZoomed(foreground) && (GetWindowLongPtr(foreground, -16).ToInt64() & 0x00C00000) == 0x00C00000) return false;
        var name = new System.Text.StringBuilder(256);
        GetClassName(foreground, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!GetWindowRect(foreground, out var r)) return false;
        Rect monitor = ScreenBoundsAt((r.Left + r.Right) / 2d, (r.Top + r.Bottom) / 2d);
        if (islandMonitor.HasValue && monitor != islandMonitor.Value) return false;
        return r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int count);
}
