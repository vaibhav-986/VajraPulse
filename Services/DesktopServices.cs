using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace VajraPulse.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _icon;

    public TrayService(Action toggle, Action settings, Action exit)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var dark = new SolidBrush(Color.FromArgb(8, 9, 11));
            using var accent = new SolidBrush(Color.FromArgb(169, 156, 255));
            g.FillEllipse(dark, 1, 1, 30, 30);
            g.FillPolygon(accent, new[] { new System.Drawing.Point(16, 5), new System.Drawing.Point(24, 16), new System.Drawing.Point(16, 27), new System.Drawing.Point(8, 16) });
        }
        IntPtr handle = bitmap.GetHicon();
        using (Icon native = Icon.FromHandle(handle)) _icon = (Icon)native.Clone();
        DestroyIcon(handle);
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Show / hide", null, (_, _) => toggle());
        _menu.Items.Add("Settings", null, (_, _) => settings());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Quit Vajra Pulse", null, (_, _) => exit());
        _tray = new Forms.NotifyIcon { Text = "Vajra Pulse", Icon = _icon, ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => toggle();
    }
    public void Dispose() { _tray.Visible = false; _tray.Dispose(); _menu.Dispose(); _icon.Dispose(); }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}

public sealed class HotkeyService : IDisposable
{
    private const int Id = 0x5650;
    private readonly Window _window;
    private readonly Action _toggle;
    private HwndSource? _source;
    public bool IsRegistered { get; private set; }

    public HotkeyService(Window window, Action toggle)
    {
        _window = window;
        _toggle = toggle;
        window.SourceInitialized += SourceInitialized;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Register();
    }
    private void SourceInitialized(object? sender, EventArgs e) => Register();
    private void Register()
    {
        if (_source != null) return;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(Hook);
        IsRegistered = _source != null && RegisterHotKey(_source.Handle, Id, 0x4003, 0x20);
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x312 && wParam.ToInt32() == Id) { _toggle(); handled = true; }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        _window.SourceInitialized -= SourceInitialized;
        if (_source != null)
        {
            if (IsRegistered) UnregisterHotKey(_source.Handle, Id);
            _source.RemoveHook(Hook);
            _source = null;
        }
        IsRegistered = false;
    }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

public sealed class AppSlotHotkeyService : IDisposable
{
    private const int BaseId = 0x5660;
    private readonly Window _window;
    private readonly Action<int>[] _launchers;
    private HwndSource? _source;

    public AppSlotHotkeyService(Window window, Action<int>[] launchers)
    {
        _window = window;
        _launchers = launchers;
        window.SourceInitialized += SourceInitialized;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Register();
    }
    private void SourceInitialized(object? sender, EventArgs e) => Register();
    private void Register()
    {
        if (_source != null) return;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(Hook);
        for (int i = 0; i < _launchers.Length; i++)
            RegisterHotKey(_source.Handle, BaseId + i, 0x4003, (uint)(0x31 + i));
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x312)
        {
            int id = wParam.ToInt32();
            if (id >= BaseId && id < BaseId + _launchers.Length)
            {
                _launchers[id - BaseId](id - BaseId);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        _window.SourceInitialized -= SourceInitialized;
        if (_source != null)
        {
            for (int i = 0; i < _launchers.Length; i++)
                UnregisterHotKey(_source.Handle, BaseId + i);
            _source.RemoveHook(Hook);
            _source = null;
        }
    }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
