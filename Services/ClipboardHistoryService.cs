using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VajraPulse.Services;

public enum ClipboardEntryType
{
    Text,
    Image,
    Files
}

public enum SensitiveClipboardReason
{
    PasswordControl,
    SecretPattern,
    PrivateKey,
    PaymentCard,
    OversizedText
}

public sealed class ClipboardHistoryEntry : INotifyPropertyChanged
{
    private bool _isPinned;

    private ClipboardHistoryEntry(
        ClipboardEntryType type,
        string summary,
        DateTimeOffset timestamp,
        string fingerprint,
        string? text,
        BitmapSource? image,
        IReadOnlyList<string> files)
    {
        Type = type;
        Summary = summary;
        Timestamp = timestamp;
        Fingerprint = fingerprint;
        Text = text;
        Image = image;
        Files = files;
    }

    public ClipboardEntryType Type { get; }
    public string Summary { get; }
    public DateTimeOffset Timestamp { get; }
    public bool IsPinned
    {
        get => _isPinned;
        internal set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
        }
    }

    // Exactly one content property is populated according to Type. Text is bounded,
    // Images are detached and frozen, and Files contains local path references only.
    public string? Text { get; }
    public BitmapSource? Image { get; }
    public IReadOnlyList<string> Files { get; }

    internal string Fingerprint { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal static ClipboardHistoryEntry CreateText(string text, DateTimeOffset timestamp) =>
        new(ClipboardEntryType.Text, ClipboardHistoryPolicy.MakeTextSummary(text), timestamp,
            ClipboardHistoryPolicy.TextFingerprint(text), text, null, Array.Empty<string>());

    internal static ClipboardHistoryEntry CreateImage(BitmapSource image, DateTimeOffset timestamp) =>
        new(ClipboardEntryType.Image, $"Image · {image.PixelWidth} × {image.PixelHeight}", timestamp,
            ClipboardHistoryPolicy.ImageFingerprint(image), null, image, Array.Empty<string>());

    internal static ClipboardHistoryEntry CreateFiles(IReadOnlyList<string> files, DateTimeOffset timestamp) =>
        new(ClipboardEntryType.Files, ClipboardHistoryPolicy.MakeFilesSummary(files), timestamp,
            ClipboardHistoryPolicy.FilesFingerprint(files), null, null, files);
}

public sealed class ClipboardHistoryEntryEventArgs : EventArgs
{
    public ClipboardHistoryEntryEventArgs(ClipboardHistoryEntry entry) => Entry = entry;
    public ClipboardHistoryEntry Entry { get; }
}

public sealed class SensitiveClipboardSkippedEventArgs : EventArgs
{
    public SensitiveClipboardSkippedEventArgs(DateTimeOffset timestamp, SensitiveClipboardReason reason)
    {
        Timestamp = timestamp;
        Reason = reason;
    }

    public DateTimeOffset Timestamp { get; }
    public SensitiveClipboardReason Reason { get; }
}

public static class ClipboardHistoryPolicy
{
    public const int MaxTextCharacters = 10_000;
    public const int MaxFileReferences = 30;
    public const int MaxImageDimension = 2_048;
    public const long MaxImageBytes = 16L * 1024 * 1024;
    public static readonly TimeSpan PasswordFocusWindow = TimeSpan.FromSeconds(2);

    private const int MaxSensitivityScanCharacters = 1_000_000;
    private static readonly Regex PrivateKeyPattern = new(
        @"-----BEGIN(?: [A-Z0-9]+)? PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NamedSecretPattern = new(
        @"\b(?:password|passwd|pwd|passphrase|secret|client[_-]?secret|api[_-]?key|access[_-]?token|auth[_-]?token|refresh[_-]?token|private[_-]?key)\b\s*(?::|=|\bis\b)\s*[\""']?\S{4,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex KnownTokenPattern = new(
        @"\b(?:bearer\s+[A-Za-z0-9._~+/=-]{8,}|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CardCandidatePattern = new(
        @"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LongTokenPattern = new(
        @"(?<![A-Za-z0-9])[A-Za-z0-9_+/=-]{20,}(?![A-Za-z0-9])", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsSensitiveText(string? text) => TryClassifySensitiveText(text, out _);

    public static bool TryClassifySensitiveText(string? text, out SensitiveClipboardReason reason)
    {
        reason = SensitiveClipboardReason.SecretPattern;
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Length > MaxSensitivityScanCharacters)
        {
            reason = SensitiveClipboardReason.OversizedText;
            return true;
        }
        if (PrivateKeyPattern.IsMatch(text))
        {
            reason = SensitiveClipboardReason.PrivateKey;
            return true;
        }
        if (NamedSecretPattern.IsMatch(text) || KnownTokenPattern.IsMatch(text) || JwtPattern.IsMatch(text))
        {
            reason = SensitiveClipboardReason.SecretPattern;
            return true;
        }
        foreach (Match match in CardCandidatePattern.Matches(text))
        {
            string digits = new(match.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits))
            {
                reason = SensitiveClipboardReason.PaymentCard;
                return true;
            }
        }
        if (LooksLikeUnlabelledCredential(text))
        {
            reason = SensitiveClipboardReason.SecretPattern;
            return true;
        }
        return false;
    }

    public static bool IsWithinPasswordFocusWindow(DateTimeOffset copiedAt, DateTimeOffset passwordFocusedAt)
    {
        TimeSpan elapsed = copiedAt - passwordFocusedAt;
        return elapsed >= TimeSpan.Zero && elapsed <= PasswordFocusWindow;
    }

    public static string BoundText(string text) =>
        text.Length <= MaxTextCharacters ? text : text.Substring(0, MaxTextCharacters);

    internal static bool InsertNewestDeduplicated(
        ObservableCollection<ClipboardHistoryEntry> entries,
        ClipboardHistoryEntry candidate,
        int maxItems)
    {
        int duplicateIndex = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(entries[i].Fingerprint),
                    Encoding.ASCII.GetBytes(candidate.Fingerprint)))
            {
                duplicateIndex = i;
                break;
            }
        }
        if (duplicateIndex >= 0)
        {
            candidate.IsPinned = entries[duplicateIndex].IsPinned;
            entries.RemoveAt(duplicateIndex);
            entries.Insert(0, candidate);
            return true;
        }
        if (entries.Count >= maxItems)
        {
            int removable = -1;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (!entries[i].IsPinned)
                {
                    removable = i;
                    break;
                }
            }
            if (removable < 0) return false;
            entries.RemoveAt(removable);
        }
        entries.Insert(0, candidate);
        return true;
    }

    internal static BitmapSource? CreateBoundedImage(BitmapSource source)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0) return null;
        double scale = Math.Min(1d, Math.Min(
            MaxImageDimension / (double)source.PixelWidth,
            MaxImageDimension / (double)source.PixelHeight));
        long sourceBytes = (long)source.PixelWidth * source.PixelHeight * 4;
        if (sourceBytes > MaxImageBytes)
            scale = Math.Min(scale, Math.Sqrt(MaxImageBytes / (double)sourceBytes));

        BitmapSource working = source;
        if (scale < 1d)
        {
            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            transformed.Freeze();
            working = transformed;
        }
        if (working.Format != PixelFormats.Bgra32 && working.Format != PixelFormats.Pbgra32)
        {
            var converted = new FormatConvertedBitmap(working, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            working = converted;
        }

        int width = working.PixelWidth;
        int height = working.PixelHeight;
        if (width <= 0 || height <= 0 || width > MaxImageDimension || height > MaxImageDimension ||
            (long)width * height * 4 > MaxImageBytes) return null;
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        working.CopyPixels(pixels, stride, 0);
        double dpiX = source.DpiX > 0 ? source.DpiX : 96;
        double dpiY = source.DpiY > 0 ? source.DpiY : 96;
        BitmapSource clone = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, stride);
        clone.Freeze();
        return clone;
    }

    internal static IReadOnlyList<string> NormalizeLocalFiles(IEnumerable<string> values)
    {
        var result = new List<string>(MaxFileReferences);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int inspected = 0;
        foreach (string? value in values)
        {
            if (++inspected > 300 || result.Count >= MaxFileReferences) break;
            string? path = NormalizeLocalPath(value);
            if (path != null && seen.Add(path)) result.Add(path);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    internal static string? NormalizeLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.StartsWith("\\\\", StringComparison.Ordinal)) return null;
        try
        {
            string path = Path.GetFullPath(value);
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) return null;
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return null;
            uint driveType = GetDriveType(root);
            return driveType is 2 or 3 or 5 or 6 ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static string TextFingerprint(string text) => Hash("text\0" + text);
    internal static string FilesFingerprint(IEnumerable<string> files) =>
        Hash("files\0" + string.Join("\n", files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => p.ToUpperInvariant())));

    internal static string ImageFingerprint(BitmapSource image)
    {
        int stride = checked(image.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * image.PixelHeight)];
        image.CopyPixels(pixels, stride, 0);
        byte[] dimensions = Encoding.ASCII.GetBytes($"image\0{image.PixelWidth}x{image.PixelHeight}\0");
        byte[] input = new byte[dimensions.Length + pixels.Length];
        Buffer.BlockCopy(dimensions, 0, input, 0, dimensions.Length);
        Buffer.BlockCopy(pixels, 0, input, dimensions.Length, pixels.Length);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    internal static string MakeTextSummary(string text)
    {
        var builder = new StringBuilder(80);
        bool previousSpace = false;
        foreach (char character in text)
        {
            bool space = char.IsWhiteSpace(character) || char.IsControl(character);
            if (space)
            {
                if (builder.Length > 0 && !previousSpace) builder.Append(' ');
                previousSpace = true;
            }
            else
            {
                builder.Append(character);
                previousSpace = false;
            }
            if (builder.Length >= 80) break;
        }
        string summary = builder.ToString().Trim();
        return summary.Length == 0 ? "Text" : summary + (text.Length > 80 ? "…" : string.Empty);
    }

    internal static string MakeFilesSummary(IReadOnlyList<string> files)
    {
        string names = string.Join(", ", files.Take(2).Select(path => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))).Where(name => !string.IsNullOrEmpty(name)));
        if (names.Length > 60) names = names.Substring(0, 60) + "…";
        return files.Count == 1 ? $"1 file · {names}" : $"{files.Count} files · {names}";
    }

    private static bool LooksLikeUnlabelledCredential(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length is >= 8 and <= 128 && !trimmed.Any(char.IsWhiteSpace))
        {
            int classes = CharacterClassCount(trimmed);
            if (classes >= 3 && ShannonEntropy(trimmed) >= 2.5) return true;
        }
        foreach (Match match in LongTokenPattern.Matches(text))
        {
            if (CharacterClassCount(match.Value) >= 3 && ShannonEntropy(match.Value) >= 3.5) return true;
        }
        return false;
    }

    private static int CharacterClassCount(string value)
    {
        int classes = 0;
        if (value.Any(char.IsLower)) classes++;
        if (value.Any(char.IsUpper)) classes++;
        if (value.Any(char.IsDigit)) classes++;
        if (value.Any(c => !char.IsLetterOrDigit(c))) classes++;
        return classes;
    }

    private static double ShannonEntropy(string value)
    {
        double entropy = 0;
        foreach (IGrouping<char, char> group in value.GroupBy(c => c))
        {
            double probability = group.Count() / (double)value.Length;
            entropy -= probability * Math.Log2(probability);
        }
        return entropy;
    }

    private static bool PassesLuhn(string digits)
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

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);
}

public sealed class ClipboardHistoryService : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private const int ClipboardWriteAttempts = 5;
    private readonly ObservableCollection<ClipboardHistoryEntry> _entries = new();
    private readonly ReadOnlyObservableCollection<ClipboardHistoryEntry> _readOnlyEntries;
    private Window? _window;
    private HwndSource? _source;
    private bool _listening;
    private bool _disposed;
    private bool _selfWriteInProgress;
    private uint _ignoredSequenceNumber;
    private DateTimeOffset _passwordFocusedAt = DateTimeOffset.MinValue;

    public ClipboardHistoryService(int maxItems = 20)
    {
        if (maxItems <= 0 || maxItems > 1_000) throw new ArgumentOutOfRangeException(nameof(maxItems));
        MaxItems = maxItems;
        _readOnlyEntries = new ReadOnlyObservableCollection<ClipboardHistoryEntry>(_entries);
    }

    public int MaxItems { get; }
    public ReadOnlyObservableCollection<ClipboardHistoryEntry> Entries => _readOnlyEntries;
    public bool IsAttached => _listening;

    public event EventHandler<ClipboardHistoryEntryEventArgs>? EntryAdded;
    public event EventHandler<SensitiveClipboardSkippedEventArgs>? SensitiveItemSkipped;

    public bool Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(_disposed, this);
        window.Dispatcher.VerifyAccess();
        if (ReferenceEquals(_window, window)) return _listening;
        Detach();
        _window = window;
        window.SourceInitialized += OnSourceInitialized;
        window.Closed += OnWindowClosed;
        window.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus), true);
        return TryStartListener();
    }

    public void Detach()
    {
        if (_window != null && !_window.Dispatcher.CheckAccess())
        {
            if (!_window.Dispatcher.HasShutdownStarted) _window.Dispatcher.Invoke(Detach);
            return;
        }
        StopListener();
        if (_window != null)
        {
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Closed -= OnWindowClosed;
            _window.RemoveHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus));
            _window = null;
        }
        _passwordFocusedAt = DateTimeOffset.MinValue;
    }

    public bool SetClipboard(ClipboardHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_disposed) return false;
        var data = new DataObject();
        switch (entry.Type)
        {
            case ClipboardEntryType.Text when entry.Text != null:
                data.SetData(DataFormats.UnicodeText, entry.Text, false);
                break;
            case ClipboardEntryType.Image when entry.Image != null:
                data.SetData(DataFormats.Bitmap, entry.Image, false);
                break;
            case ClipboardEntryType.Files when entry.Files.Count > 0:
                var files = new StringCollection();
                files.AddRange(entry.Files.ToArray());
                data.SetFileDropList(files);
                break;
            default:
                return false;
        }

        for (int attempt = 0; attempt < ClipboardWriteAttempts; attempt++)
        {
            try
            {
                _selfWriteInProgress = true;
                Clipboard.SetDataObject(data, false);
                _ignoredSequenceNumber = GetClipboardSequenceNumber();
                return true;
            }
            catch (ExternalException) when (attempt + 1 < ClipboardWriteAttempts)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
            catch (ExternalException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            finally
            {
                _selfWriteInProgress = false;
            }
        }
        return false;
    }

    public bool Remove(ClipboardHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _entries.Remove(entry);
    }

    public bool Pin(ClipboardHistoryEntry entry, bool isPinned = true)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!_entries.Contains(entry)) return false;
        entry.IsPinned = isPinned;
        return true;
    }

    public bool Unpin(ClipboardHistoryEntry entry) => Pin(entry, false);

    public int Clear(bool includePinned = false)
    {
        if (includePinned)
        {
            int count = _entries.Count;
            _entries.Clear();
            return count;
        }
        int removed = 0;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].IsPinned) continue;
            _entries.RemoveAt(i);
            removed++;
        }
        return removed;
    }

    public int ClearUnpinned() => Clear(false);
    public int ClearAll() => Clear(true);

    internal bool AddCapturedText(string text, DateTimeOffset timestamp)
    {
        if (ClipboardHistoryPolicy.TryClassifySensitiveText(text, out SensitiveClipboardReason reason))
        {
            OnSensitiveSkipped(timestamp, reason);
            return false;
        }
        string bounded = ClipboardHistoryPolicy.BoundText(text);
        if (bounded.Length == 0) return false;
        return AddEntry(ClipboardHistoryEntry.CreateText(bounded, timestamp));
    }

    internal bool AddCapturedFiles(IEnumerable<string> paths, DateTimeOffset timestamp)
    {
        IReadOnlyList<string> bounded = ClipboardHistoryPolicy.NormalizeLocalFiles(paths);
        return bounded.Count > 0 && AddEntry(ClipboardHistoryEntry.CreateFiles(bounded, timestamp));
    }

    internal bool AddCapturedImage(BitmapSource image, DateTimeOffset timestamp)
    {
        BitmapSource? bounded = ClipboardHistoryPolicy.CreateBoundedImage(image);
        return bounded != null && AddEntry(ClipboardHistoryEntry.CreateImage(bounded, timestamp));
    }

    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => TryStartListener();
    private void OnWindowClosed(object? sender, EventArgs e) => Detach();

    private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is PasswordBox) _passwordFocusedAt = DateTimeOffset.UtcNow;
    }

    private bool TryStartListener()
    {
        if (_disposed || _window == null || _listening) return _listening;
        IntPtr handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return false;
        HwndSource? source = HwndSource.FromHwnd(handle);
        if (source == null) return false;
        source.AddHook(WindowHook);
        if (!AddClipboardFormatListener(handle))
        {
            source.RemoveHook(WindowHook);
            return false;
        }
        _source = source;
        _listening = true;
        return true;
    }

    private void StopListener()
    {
        if (_source != null)
        {
            if (_listening) RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WindowHook);
            _source = null;
        }
        _listening = false;
        _ignoredSequenceNumber = 0;
        _selfWriteInProgress = false;
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmClipboardUpdate || _disposed) return IntPtr.Zero;
        uint sequence = GetClipboardSequenceNumber();
        if (_selfWriteInProgress || (_ignoredSequenceNumber != 0 && sequence == _ignoredSequenceNumber))
        {
            _ignoredSequenceNumber = 0;
            return IntPtr.Zero;
        }
        _ignoredSequenceNumber = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (ClipboardHistoryPolicy.IsWithinPasswordFocusWindow(now, _passwordFocusedAt))
        {
            OnSensitiveSkipped(now, SensitiveClipboardReason.PasswordControl);
            return IntPtr.Zero;
        }
        CaptureStandardClipboardData(now);
        return IntPtr.Zero;
    }

    private void CaptureStandardClipboardData(DateTimeOffset timestamp)
    {
        try
        {
            IDataObject? data = Clipboard.GetDataObject();
            if (data == null) return;
            if (data.GetDataPresent(DataFormats.FileDrop, false))
            {
                object? value = data.GetData(DataFormats.FileDrop, false);
                if (value is string[] paths) AddCapturedFiles(paths, timestamp);
                else if (value is StringCollection collection) AddCapturedFiles(collection.Cast<string>(), timestamp);
                return;
            }
            if (data.GetDataPresent(DataFormats.Bitmap, false))
            {
                if (data.GetData(DataFormats.Bitmap, false) is BitmapSource image) AddCapturedImage(image, timestamp);
                return;
            }
            if (data.GetDataPresent(DataFormats.UnicodeText, false) &&
                data.GetData(DataFormats.UnicodeText, false) is string text)
            {
                AddCapturedText(text, timestamp);
            }
        }
        catch (ExternalException)
        {
            // Another process owns or is updating the clipboard. A later update will be handled normally.
        }
        catch (InvalidOperationException)
        {
            // Clipboard access can transiently fail during shutdown or ownership changes.
        }
    }

    private bool AddEntry(ClipboardHistoryEntry entry)
    {
        bool added = ClipboardHistoryPolicy.InsertNewestDeduplicated(_entries, entry, MaxItems);
        if (added) EntryAdded?.Invoke(this, new ClipboardHistoryEntryEventArgs(entry));
        return added;
    }

    private void OnSensitiveSkipped(DateTimeOffset timestamp, SensitiveClipboardReason reason) =>
        SensitiveItemSkipped?.Invoke(this, new SensitiveClipboardSkippedEventArgs(timestamp, reason));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
