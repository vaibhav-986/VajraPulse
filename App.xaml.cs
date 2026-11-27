using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace VajraPulse;

public partial class App : Application
{
    private Mutex? _instance;

    public string CurrentVisualMode { get; private set; } = "Cute";

    public void ApplyVisualMode(string requestedMode)
    {
        string mode = requestedMode is "Glass" or "Neon" ? requestedMode : "Cute";
        var replacement = new ResourceDictionary { Source = new Uri($"UI/{mode}Theme.xaml", UriKind.Relative) };
        var dictionaries = Resources.MergedDictionaries;
        int existingIndex = -1;
        for (int index = 0; index < dictionaries.Count; index++)
        {
            string file = dictionaries[index].Source?.OriginalString.Replace('\\', '/') ?? "";
            if (file.EndsWith("/CuteTheme.xaml", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith("/GlassTheme.xaml", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith("/NeonTheme.xaml", StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = index;
                break;
            }
        }
        if (existingIndex >= 0) dictionaries[existingIndex] = replacement;
        else dictionaries.Insert(0, replacement);
        CurrentVisualMode = mode;
    }

    public void ApplyAccentColor(string? hex)
    {
        if (!TryParseColor(hex, out Color color))
        {
            Resources.Remove("AccentBrush");
            Resources.Remove("OnAccentBrush");
            return;
        }
        double luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;
        Color onAccent = luminance > 0.55 ? Color.FromRgb(20, 20, 26) : Colors.White;
        var accent = new SolidColorBrush(color);
        var on = new SolidColorBrush(onAccent);
        accent.Freeze();
        on.Freeze();
        Resources["AccentBrush"] = accent;
        Resources["OnAccentBrush"] = on;
    }

    public static bool TryParseColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string value = hex.Trim().TrimStart('#');
        if (value.Length == 6 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        if (value.Length == 8 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
        {
            color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return true;
        }
        return false;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, "Local\\VajraPulse.Desktop", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Vajra Pulse is already running. Look for its tray icon or press Ctrl + Alt + Space.",
                "Vajra Pulse", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VajraPulse");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "last-error.txt"), args.Exception.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            args.Handled = true;
            MessageBox.Show("Vajra Pulse encountered an error and will close. Details were saved to %LOCALAPPDATA%\\VajraPulse\\last-error.txt.", "Vajra Pulse");
            Shutdown(1);
        };
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
