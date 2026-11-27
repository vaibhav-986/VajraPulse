using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VajraPulse.Services;

public enum SensitiveClipboardReason
{
    SecretPattern,
    PasswordFocusWindow
}

public sealed class SensitiveItemSkippedEventArgs : EventArgs
{
    public SensitiveItemSkippedEventArgs(SensitiveClipboardReason reason) => Reason = reason;
    public SensitiveClipboardReason Reason { get; }
}

public sealed class ClipboardHistoryEntry
{
    public string? Text { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public bool IsPinned { get; set; }

    public static ClipboardHistoryEntry CreateText(string text, DateTimeOffset timestamp) =>
        new() { Text = text, Timestamp = timestamp };

    public string Preview
    {
        get
        {
            string flat = (Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length > 80 ? flat[..80] + "…" : flat;
        }
    }

    public string Detail => $"{Text?.Length ?? 0} chars · {Timestamp:HH:mm}{(IsPinned ? " · pinned" : "")}";
    public string PinLabel => IsPinned ? "Unpin" : "Pin";
    public string PinAutomationName => IsPinned ? "Unpin clipboard entry" : "Pin clipboard entry";
}

public static class ClipboardHistoryPolicy
{
    public const int MaxTextCharacters = 10_000;
    public const int MaxFileReferences = 30;
    public const int MaxImageDimension = 512;
    public const long MaxImageBytes = 4L * 1024 * 1024;
    public static readonly TimeSpan PasswordFocusWindow = TimeSpan.FromSeconds(2);

    private static readonly Regex SecretPattern = new(
        @"(?i)\b(pass(word|wd|phrase)?|pwd|api[_-]?key|apikey|secret|client[_-]?secret|access[_-]?key|auth[_-]?token|token|bearer|authorization)\b\s*[:=]",
        RegexOptions.Compiled);

    private static readonly Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{4,}\b",
        RegexOptions.Compiled);

    private static readonly Regex CardCandidate = new(
        @"\b(?:\d[ -]?){12,18}\d\b",
        RegexOptions.Compiled);

    public static bool IsSensitiveText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Contains("BEGIN", StringComparison.Ordinal)
            && text.Contains("PRIVATE KEY-----", StringComparison.Ordinal)) return true;
        if (SecretPattern.IsMatch(text)) return true;
        if (JwtPattern.IsMatch(text)) return true;
        foreach (Match match in CardCandidate.Matches(text))
        {
            string digits = new(match.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && IsLuhnValid(digits)) return true;
        }
        return false;
    }

    public static bool IsWithinPasswordFocusWindow(DateTimeOffset eventTime, DateTimeOffset passwordFocusTime)
    {
        TimeSpan delta = eventTime - passwordFocusTime;
        return delta >= TimeSpan.Zero && delta <= PasswordFocusWindow;
    }

    public static string BoundText(string text) =>
        text.Length <= MaxTextCharacters ? text : text[..MaxTextCharacters];

    public static bool InsertNewestDeduplicated(ObservableCollection<ClipboardHistoryEntry> entries,
        ClipboardHistoryEntry replacement, int capacity)
    {
        if (entries == null || replacement == null) return false;
        ClipboardHistoryEntry? existing = entries.FirstOrDefault(entry =>
            string.Equals(entry.Text, replacement.Text, StringComparison.Ordinal));
        if (existing != null)
        {
            replacement.IsPinned = existing.IsPinned;
            int index = entries.IndexOf(existing);
            if (index >= 0) entries.RemoveAt(index);
        }
        entries.Insert(0, replacement);
        while (entries.Count > Math.Max(1, capacity))
        {
            int evictIndex = -1;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (!entries[i].IsPinned) { evictIndex = i; break; }
            }
            if (evictIndex < 0) evictIndex = entries.Count - 1;
            entries.RemoveAt(evictIndex);
        }
        return true;
    }

    public static string? NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return null;
        if (!Path.IsPathFullyQualified(trimmed)) return null;
        return trimmed;
    }

    public static IReadOnlyList<string> NormalizeLocalFiles(IEnumerable<string>? paths)
    {
        if (paths == null) return Array.Empty<string>();
        return paths
            .Select(NormalizeLocalPath)
            .Where(path => path != null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFileReferences)
            .ToArray();
    }

    public static BitmapSource? CreateBoundedImage(BitmapSource? source)
    {
        if (source == null) return null;
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        if (width <= 0 || height <= 0) return null;
        double scale = Math.Min(1d, (double)MaxImageDimension / Math.Max(width, height));
        int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        if ((long)targetWidth * targetHeight * 4 > MaxImageBytes)
        {
            double byteScale = Math.Sqrt((double)MaxImageBytes / ((long)targetWidth * targetHeight * 4));
            targetWidth = Math.Max(1, (int)(targetWidth * byteScale));
            targetHeight = Math.Max(1, (int)(targetHeight * byteScale));
        }
        BitmapSource result;
        if (targetWidth == width && targetHeight == height)
        {
            result = source;
        }
        else
        {
            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
                context.DrawImage(source, new Rect(0, 0, targetWidth, targetHeight));
            var rendered = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            result = rendered;
        }
        BitmapSource clone = result.Clone();
        clone.Freeze();
        return clone;
    }

    private static bool IsLuhnValid(string digits)
    {
        int sum = 0;
        bool doubleDigit = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int digit = digits[i] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }
}

public sealed class ClipboardHistoryService : IDisposable
{
    private readonly ObservableCollection<ClipboardHistoryEntry> _entries = new();
    private string? _lastSeen;

    public ClipboardHistoryService(int capacity = 20) => Capacity = Math.Clamp(capacity, 1, 100);

    public int Capacity { get; }
    public ObservableCollection<ClipboardHistoryEntry> Entries => _entries;
    public event EventHandler<SensitiveItemSkippedEventArgs>? SensitiveItemSkipped;

    public bool AddCapturedText(string? text, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string bounded = ClipboardHistoryPolicy.BoundText(text);
        if (ClipboardHistoryPolicy.IsSensitiveText(bounded))
        {
            SensitiveItemSkipped?.Invoke(this, new SensitiveItemSkippedEventArgs(SensitiveClipboardReason.SecretPattern));
            return false;
        }
        return ClipboardHistoryPolicy.InsertNewestDeduplicated(_entries,
            ClipboardHistoryEntry.CreateText(bounded, timestamp), Capacity);
    }

    public bool Poll()
    {
        try
        {
            if (!Clipboard.ContainsText()) return false;
            string text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text) || text == _lastSeen) return false;
            _lastSeen = text;
            return AddCapturedText(text, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            return false;
        }
    }

    public bool CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _lastSeen = text;
            return true;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            return false;
        }
    }

    public bool Pin(ClipboardHistoryEntry entry)
    {
        if (!_entries.Contains(entry)) return false;
        entry.IsPinned = true;
        return true;
    }

    public bool Unpin(ClipboardHistoryEntry entry)
    {
        if (!_entries.Contains(entry)) return false;
        entry.IsPinned = false;
        return true;
    }

    public int ClearUnpinned()
    {
        int removed = 0;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].IsPinned) continue;
            _entries.RemoveAt(i);
            removed++;
        }
        return removed;
    }

    public int ClearAll()
    {
        int removed = _entries.Count;
        _entries.Clear();
        _lastSeen = null;
        return removed;
    }

    public void Dispose()
    {
        _entries.Clear();
        _lastSeen = null;
    }
}
