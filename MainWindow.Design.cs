using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VajraPulse.Core;
using VajraPulse.Services;

namespace VajraPulse;

public partial class MainWindow
{
    private BitmapSource? _accentArtwork;

    private void LoadDesignPreferences()
    {
        string mode = _settings.VisualMode is "Glass" or "Neon" ? _settings.VisualMode : "Cute";
        CuteModeCard.IsChecked = mode == "Cute";
        GlassModeCard.IsChecked = mode == "Glass";
        NeonModeCard.IsChecked = mode == "Neon";
        DockLeftButton.IsChecked = _settings.DockPosition == IslandDockPosition.TopLeft;
        DockCenterButton.IsChecked = _settings.DockPosition == IslandDockPosition.TopCenter;
        DockRightButton.IsChecked = _settings.DockPosition == IslandDockPosition.TopRight;
        ArtworkAccentCheck.IsChecked = _settings.UseArtworkAccent;
        UpdateAccentPreview();
        ApplyAccessoryLayout(_targetWidth);
    }

    private void UpdateAccentPreview() =>
        AccentHexText.Text = string.IsNullOrWhiteSpace(_settings.AccentColor) ? "Theme default" : _settings.AccentColor.ToUpperInvariant();

    private void AccentColor_Click(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not FrameworkElement { Tag: string hex }) return;
        if (!App.TryParseColor(hex, out _)) return;
        _settings.AccentColor = hex;
        ((App)Application.Current).ApplyAccentColor(hex);
        SavePreferences();
        UpdateAccentPreview();
        SettingsStatus.Text = $"Accent color set to {hex.ToUpperInvariant()}.";
    }

    private void AccentCustom_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        if (App.TryParseColor(_settings.AccentColor, out var current))
            dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        _featuresModal = true;
        try
        {
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        }
        finally { _featuresModal = false; }
        string hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        _settings.AccentColor = hex;
        ((App)Application.Current).ApplyAccentColor(hex);
        SavePreferences();
        UpdateAccentPreview();
        SettingsStatus.Text = $"Accent color set to {hex}.";
    }

    private void AccentReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.AccentColor = "";
        ((App)Application.Current).ApplyAccentColor("");
        SavePreferences();
        UpdateAccentPreview();
        SettingsStatus.Text = "Accent color reset to theme default.";
    }

    private void VisualMode_Click(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not FrameworkElement { Tag: string mode }
            || mode is not ("Cute" or "Glass" or "Neon")) return;
        _settings.VisualMode = mode;
        ((App)Application.Current).ApplyVisualMode(mode);
        ((App)Application.Current).ApplyAccentColor(_settings.AccentColor);
        _accentArtwork = null;
        UpdateArtworkAccent(_media.Snapshot.Artwork);
        LoadDesignPreferences();
        ApplyRailVisibility();
        RenderToolSection();
        UpdateActivity();
        UpdateCompact();
        SavePreferences();
        SettingsStatus.Text = mode switch
        {
            "Glass" => "Glass mode uses a safe translucent fallback on this transparent window.",
            "Neon" => "Neon Pulse mode selected.",
            _ => "Cute Companion mode selected."
        };
    }

    private void DockPosition_Click(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not FrameworkElement { Tag: string value }
            || !Enum.TryParse(value, out IslandDockPosition position)
            || !Enum.IsDefined(position)) return;
        _settings.DockPosition = position;
        ApplyAccessoryLayout(_targetWidth);
        PositionIsland();
        LoadDesignPreferences();
        SavePreferences();
        SettingsStatus.Text = position switch
        {
            IslandDockPosition.TopLeft => "Docked at the top left.",
            IslandDockPosition.TopRight => "Docked at the top right.",
            _ => "Docked at the top center."
        };
    }

    private void ApplyAccessoryLayout(double islandWidth)
    {
        double distance = islandWidth / 2 + 22;
        bool rightDock = _settings.DockPosition == IslandDockPosition.TopRight;
        double rail = rightDock ? -distance : distance;
        double satellite = rightDock ? distance : -distance;
        if (_ready)
        {
            AnimateTransform(SideRailPosition, TranslateTransform.XProperty, rail);
            AnimateTransform(SatellitePosition, TranslateTransform.XProperty, satellite);
        }
        else
        {
            SideRailPosition.X = rail;
            SatellitePosition.X = satellite;
        }
    }

    private double DockCenterX(double scale, double islandWidth)
    {
        const double screenInset = 8;
        const double accessoryEnvelope = 40;
        return _settings.DockPosition switch
        {
            IslandDockPosition.TopLeft => _monitor.Left + (screenInset + accessoryEnvelope + islandWidth / 2) * scale,
            IslandDockPosition.TopRight => _monitor.Right - (screenInset + accessoryEnvelope + islandWidth / 2) * scale,
            _ => _monitor.Left + _monitor.Width / 2d
        };
    }

    private int CalculateWindowLeft(double scale)
    {
        double islandWidth = Island.ActualWidth > 0 ? Island.ActualWidth : _targetWidth;
        return (int)Math.Round(DockCenterX(scale, islandWidth) - Width * scale / 2d);
    }

    private double CalculateRevealCenter(double scale)
    {
        double islandWidth = Math.Max(124, _targetWidth);
        return DockCenterX(scale, islandWidth);
    }

    private void UpdateArtworkAccent(BitmapSource? artwork)
    {
        if (!_settings.UseArtworkAccent || artwork == null)
        {
            _accentArtwork = null;
            Resources.Remove("ArtworkDominantBrush");
            Resources.Remove("ArtworkMutedBrush");
            return;
        }
        if (ReferenceEquals(_accentArtwork, artwork)) return;
        _accentArtwork = artwork;
        if (!TryExtractArtworkColors(artwork, out Color dominant, out Color muted))
        {
            Resources.Remove("ArtworkDominantBrush");
            Resources.Remove("ArtworkMutedBrush");
            return;
        }
        var dominantBrush = new SolidColorBrush(dominant);
        var mutedBrush = new SolidColorBrush(muted);
        dominantBrush.Freeze();
        mutedBrush.Freeze();
        Resources["ArtworkDominantBrush"] = dominantBrush;
        Resources["ArtworkMutedBrush"] = mutedBrush;
    }

    private static bool TryExtractArtworkColors(BitmapSource source, out Color dominant, out Color muted)
    {
        dominant = default;
        muted = default;
        try
        {
            BitmapSource image = source;
            if (image.Format != PixelFormats.Bgra32 && image.Format != PixelFormats.Pbgra32)
            {
                var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                image = converted;
            }
            int width = image.PixelWidth;
            int height = image.PixelHeight;
            if (width <= 0 || height <= 0 || (long)width * height > 16_384) return false;
            int stride = checked(width * 4);
            byte[] pixels = new byte[checked(stride * height)];
            image.CopyPixels(pixels, stride, 0);
            int step = Math.Max(1, (width * height) / 1024);
            double red = 0, green = 0, blue = 0, weightTotal = 0;
            for (int pixel = 0; pixel < width * height; pixel += step)
            {
                int offset = pixel * 4;
                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];
                byte a = pixels[offset + 3];
                if (a < 160) continue;
                byte maximum = Math.Max(r, Math.Max(g, b));
                byte minimum = Math.Min(r, Math.Min(g, b));
                double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                if (luminance < 22 || luminance > 238) continue;
                double chroma = maximum - minimum;
                double weight = 0.35 + chroma / 255d;
                red += r * weight;
                green += g * weight;
                blue += b * weight;
                weightTotal += weight;
            }
            if (weightTotal < 1) return false;
            byte rr = (byte)Math.Clamp((int)Math.Round(red / weightTotal), 0, 255);
            byte gg = (byte)Math.Clamp((int)Math.Round(green / weightTotal), 0, 255);
            byte bb = (byte)Math.Clamp((int)Math.Round(blue / weightTotal), 0, 255);
            int max = Math.Max(rr, Math.Max(gg, bb));
            if (max < 150)
            {
                double boost = 150d / Math.Max(1, max);
                rr = (byte)Math.Min(255, Math.Round(rr * boost));
                gg = (byte)Math.Min(255, Math.Round(gg * boost));
                bb = (byte)Math.Min(255, Math.Round(bb * boost));
            }
            dominant = Color.FromRgb(rr, gg, bb);
            muted = Color.FromRgb(
                (byte)Math.Round(rr * 0.62 + 104 * 0.38),
                (byte)Math.Round(gg * 0.62 + 104 * 0.38),
                (byte)Math.Round(bb * 0.62 + 104 * 0.38));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException or NotSupportedException)
        {
            return false;
        }
    }

    private void UpdateCompactProgressRing(bool notice, bool focus, bool mediaPresent)
    {
    }
}
