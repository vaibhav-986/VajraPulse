using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace VajraPulse.Services;

public enum SnippingToolLaunchStatus
{
    Launched,
    Unavailable
}

public sealed record SnippingToolLaunchResult(SnippingToolLaunchStatus Status, string? Error)
{
    public bool Launched => Status == SnippingToolLaunchStatus.Launched;
}

public enum OcrReadStatus
{
    Success,
    NoImage,
    ImageTooLarge,
    OcrUnavailable,
    ClipboardUnavailable,
    Cancelled,
    TimedOut,
    Failed
}

public sealed record OcrSourceImageMetadata(
    int PixelWidth,
    int PixelHeight,
    double DpiX,
    double DpiY,
    string PixelFormat,
    long EstimatedBytes);

public sealed record ClipboardOcrResult(
    OcrReadStatus Status,
    string Text,
    IReadOnlyList<string> Lines,
    string? Error,
    OcrSourceImageMetadata? SourceImage,
    bool IsTextTruncated = false)
{
    public bool Succeeded => Status == OcrReadStatus.Success;
}

/// <summary>
/// Opens Windows' own clipping UI and performs local OCR only when one of the read methods is called.
/// It never captures the desktop, sends input, monitors the clipboard, or uses a network service.
/// </summary>
public sealed class ScreenCaptureOcrService
{
    public const long MaxImagePixels = 16_000_000;
    public const long MaxImageBytes = 32L * 1024 * 1024;
    public const int MaxExtractedTextLength = 20_000;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(2);

    public SnippingToolLaunchResult LaunchSnippingTool()
    {
        Exception? protocolError = null;
        try
        {
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "ms-screenclip:",
                UseShellExecute = true
            });
            if (process is not null)
            {
                process.Dispose();
                return new SnippingToolLaunchResult(SnippingToolLaunchStatus.Launched, null);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            protocolError = exception;
        }

        try
        {
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "SnippingTool.exe",
                UseShellExecute = true
            });
            if (process is not null)
            {
                process.Dispose();
                return new SnippingToolLaunchResult(SnippingToolLaunchStatus.Launched, null);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            string message = protocolError is null
                ? exception.Message
                : $"Screen clipping protocol: {protocolError.Message} Snipping Tool: {exception.Message}";
            return new SnippingToolLaunchResult(SnippingToolLaunchStatus.Unavailable, message);
        }

        return new SnippingToolLaunchResult(
            SnippingToolLaunchStatus.Unavailable,
            protocolError?.Message ?? "Windows did not start the screen clipping tool.");
    }

    /// <summary>
    /// Reads the standard bitmap currently on the clipboard. Call this only in direct response to the
    /// user's Read clipboard image action; this service does not watch or poll the clipboard.
    /// </summary>
    public async Task<ClipboardOcrResult> ReadClipboardImageAsync(
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ClipboardImageSnapshot snapshot;
        try
        {
            snapshot = await ReadClipboardSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(OcrReadStatus.Cancelled, "Clipboard image reading was cancelled.");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Result(OcrReadStatus.ClipboardUnavailable, exception.Message);
        }

        if (snapshot.Image is null)
        {
            return new ClipboardOcrResult(
                snapshot.Status,
                string.Empty,
                Array.Empty<string>(),
                snapshot.Error,
                snapshot.Metadata);
        }

        return await RecognizeImageCoreAsync(
            snapshot.Image,
            snapshot.Metadata!,
            cancellationToken,
            timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs local Windows OCR on a caller-supplied image. The image is bounded, cloned, and frozen
    /// before asynchronous work begins.
    /// </summary>
    public async Task<ClipboardOcrResult> RecognizeImageAsync(
        BitmapSource image,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        if (image is null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        OcrSourceImageMetadata metadata = DescribeImage(image);
        if (!IsImageWithinLimits(image.PixelWidth, image.PixelHeight, image.Format.BitsPerPixel, out _))
        {
            return new ClipboardOcrResult(
                OcrReadStatus.ImageTooLarge,
                string.Empty,
                Array.Empty<string>(),
                LimitMessage(metadata),
                metadata);
        }

        BitmapSource frozen;
        try
        {
            frozen = CloneAndFreeze(image);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new ClipboardOcrResult(
                OcrReadStatus.Failed,
                string.Empty,
                Array.Empty<string>(),
                exception.Message,
                metadata);
        }

        return await RecognizeImageCoreAsync(frozen, metadata, cancellationToken, timeout).ConfigureAwait(false);
    }

    public static bool IsImageWithinLimits(int pixelWidth, int pixelHeight, int bitsPerPixel, out long estimatedBytes)
    {
        estimatedBytes = 0;
        if (pixelWidth <= 0 || pixelHeight <= 0 || bitsPerPixel < 0)
        {
            return false;
        }

        try
        {
            long pixels = checked((long)pixelWidth * pixelHeight);
            int effectiveBits = bitsPerPixel == 0 ? 32 : bitsPerPixel;
            long sourceStride = checked(((long)pixelWidth * effectiveBits + 7) / 8);
            long sourceBytes = checked(sourceStride * pixelHeight);
            long convertedBytes = checked(pixels * 4); // Windows OCR receives BGRA8.
            estimatedBytes = Math.Max(sourceBytes, convertedBytes);
            return pixels <= MaxImagePixels && estimatedBytes <= MaxImageBytes;
        }
        catch (OverflowException)
        {
            estimatedBytes = long.MaxValue;
            return false;
        }
    }

    private static async Task<ClipboardOcrResult> RecognizeImageCoreAsync(
        BitmapSource frozenImage,
        OcrSourceImageMetadata metadata,
        CancellationToken cancellationToken,
        TimeSpan? requestedTimeout)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ClipboardOcrResult(
                OcrReadStatus.Cancelled,
                string.Empty,
                Array.Empty<string>(),
                "OCR was cancelled.",
                metadata);
        }

        TimeSpan effectiveTimeout = requestedTimeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > MaximumTimeout)
        {
            return new ClipboardOcrResult(
                OcrReadStatus.Failed,
                string.Empty,
                Array.Empty<string>(),
                $"OCR timeout must be greater than zero and no more than {MaximumTimeout.TotalSeconds:0} seconds.",
                metadata);
        }

        OcrEngine? engine;
        try
        {
            engine = OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new ClipboardOcrResult(
                OcrReadStatus.OcrUnavailable,
                string.Empty,
                Array.Empty<string>(),
                exception.Message,
                metadata);
        }

        if (engine is null)
        {
            return new ClipboardOcrResult(
                OcrReadStatus.OcrUnavailable,
                string.Empty,
                Array.Empty<string>(),
                "Windows OCR is unavailable for the current user profile languages.",
                metadata);
        }

        using var timeoutCancellation = new CancellationTokenSource(effectiveTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            using SoftwareBitmap softwareBitmap = ToSoftwareBitmap(frozenImage);
            OcrResult result = await engine.RecognizeAsync(softwareBitmap).AsTask(linkedCancellation.Token)
                .ConfigureAwait(false);
            return BoundedResult(result, metadata);
        }
        catch (OperationCanceledException)
        {
            bool callerCancelled = cancellationToken.IsCancellationRequested;
            return new ClipboardOcrResult(
                callerCancelled ? OcrReadStatus.Cancelled : OcrReadStatus.TimedOut,
                string.Empty,
                Array.Empty<string>(),
                callerCancelled ? "OCR was cancelled." : "OCR timed out.",
                metadata);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new ClipboardOcrResult(
                OcrReadStatus.Failed,
                string.Empty,
                Array.Empty<string>(),
                exception.Message,
                metadata);
        }
    }

    private static ClipboardOcrResult BoundedResult(OcrResult result, OcrSourceImageMetadata metadata)
    {
        var lines = new List<string>();
        int remaining = MaxExtractedTextLength;
        bool truncated = false;

        foreach (OcrLine line in result.Lines)
        {
            string text = line.Text ?? string.Empty;
            int separatorLength = lines.Count == 0 ? 0 : Environment.NewLine.Length;
            if (separatorLength >= remaining)
            {
                truncated = true;
                break;
            }

            int available = remaining - separatorLength;
            if (text.Length > available)
            {
                text = text[..available];
                truncated = true;
            }

            if (separatorLength > 0)
            {
                remaining -= separatorLength;
            }
            lines.Add(text);
            remaining -= text.Length;

            if (remaining == 0)
            {
                truncated |= result.Lines.Count > lines.Count;
                break;
            }
        }

        if (lines.Count == 0 && !string.IsNullOrEmpty(result.Text))
        {
            string text = result.Text;
            if (text.Length > MaxExtractedTextLength)
            {
                text = text[..MaxExtractedTextLength];
                truncated = true;
            }
            lines.Add(text);
        }

        string combined = string.Join(Environment.NewLine, lines);
        return new ClipboardOcrResult(
            OcrReadStatus.Success,
            combined,
            lines.AsReadOnly(),
            null,
            metadata,
            truncated);
    }

    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource source)
    {
        BitmapSource converted = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            var conversion = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            conversion.Freeze();
            converted = conversion;
        }

        int stride = checked(converted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);

        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        IBuffer buffer = writer.DetachBuffer();
        return SoftwareBitmap.CreateCopyFromBuffer(
            buffer,
            BitmapPixelFormat.Bgra8,
            converted.PixelWidth,
            converted.PixelHeight,
            BitmapAlphaMode.Premultiplied);
    }

    private static Task<ClipboardImageSnapshot> ReadClipboardSnapshotAsync(CancellationToken cancellationToken)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadClipboardSnapshot());
        }

        var completion = new TaskCompletionSource<ClipboardImageSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }
                completion.TrySetResult(ReadClipboardSnapshot());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "VajraPulse clipboard image reader"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(cancellationToken);
    }

    private static ClipboardImageSnapshot ReadClipboardSnapshot()
    {
        if (!System.Windows.Clipboard.ContainsImage())
        {
            return new ClipboardImageSnapshot(
                null,
                null,
                OcrReadStatus.NoImage,
                "The clipboard does not contain a standard bitmap.");
        }

        BitmapSource? source = System.Windows.Clipboard.GetImage();
        if (source is null)
        {
            return new ClipboardImageSnapshot(
                null,
                null,
                OcrReadStatus.NoImage,
                "The clipboard did not provide a readable standard bitmap.");
        }

        OcrSourceImageMetadata metadata = DescribeImage(source);
        if (!IsImageWithinLimits(source.PixelWidth, source.PixelHeight, source.Format.BitsPerPixel, out _))
        {
            return new ClipboardImageSnapshot(
                null,
                metadata,
                OcrReadStatus.ImageTooLarge,
                LimitMessage(metadata));
        }

        BitmapSource frozen = CloneAndFreeze(source);
        return new ClipboardImageSnapshot(frozen, metadata, OcrReadStatus.Success, null);
    }

    private static BitmapSource CloneAndFreeze(BitmapSource source)
    {
        var clone = new WriteableBitmap(source);
        clone.Freeze();
        return clone;
    }

    private static OcrSourceImageMetadata DescribeImage(BitmapSource source)
    {
        IsImageWithinLimits(source.PixelWidth, source.PixelHeight, source.Format.BitsPerPixel, out long bytes);
        return new OcrSourceImageMetadata(
            source.PixelWidth,
            source.PixelHeight,
            source.DpiX,
            source.DpiY,
            source.Format.ToString(),
            bytes);
    }

    private static string LimitMessage(OcrSourceImageMetadata metadata) =>
        $"The image is {metadata.PixelWidth}x{metadata.PixelHeight} and needs about {metadata.EstimatedBytes} bytes; " +
        $"the limits are {MaxImagePixels} pixels and {MaxImageBytes} decoded bytes.";

    private static ClipboardOcrResult Result(OcrReadStatus status, string error) =>
        new(status, string.Empty, Array.Empty<string>(), error, null);

    private static bool IsRecoverable(Exception exception) => exception is not (
        OutOfMemoryException or StackOverflowException or AccessViolationException);

    private sealed record ClipboardImageSnapshot(
        BitmapSource? Image,
        OcrSourceImageMetadata? Metadata,
        OcrReadStatus Status,
        string? Error);
}

public sealed record QuickCaptureNote(
    string Id,
    string Title,
    string Text,
    DateTimeOffset CreatedAt,
    bool IsPinned);

public enum NoteOperationStatus
{
    Success,
    NotFound,
    Invalid,
    CapacityReached,
    StorageError
}

public sealed record NoteOperationResult(
    NoteOperationStatus Status,
    QuickCaptureNote? Note = null,
    string? Error = null)
{
    public bool Succeeded => Status == NoteOperationStatus.Success;
}

/// <summary>
/// Persists explicitly-created plain-text quick-capture notes in a local JSON file.
/// It does not read OCR or clipboard content and does not create reminders.
/// </summary>
public sealed class QuickCaptureNoteService
{
    public const int MaxNotes = 100;
    public const int MaxNoteTextLength = 10_000;
    public const int MaxTitleLength = 200;
    public const long MaxNotesFileBytes = 2L * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private List<QuickCaptureNote> _notes = new();

    public QuickCaptureNoteService(string? notesPath = null)
    {
        NotesPath = NormalizeLocalPath(notesPath ?? DefaultNotesPath);
        Reload();
    }

    public static string DefaultNotesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VajraPulse",
        "notes.json");

    public string NotesPath { get; }
    public string? LastError { get; private set; }

    public IReadOnlyList<QuickCaptureNote> Notes
    {
        get
        {
            lock (_sync)
            {
                return _notes.ToArray();
            }
        }
    }

    public IReadOnlyList<QuickCaptureNote> Reload()
    {
        lock (_sync)
        {
            LastError = null;
            if (!File.Exists(NotesPath))
            {
                _notes = new List<QuickCaptureNote>();
                return _notes.ToArray();
            }

            try
            {
                var info = new FileInfo(NotesPath);
                if (info.Length > MaxNotesFileBytes)
                {
                    throw new InvalidDataException($"The notes file exceeds {MaxNotesFileBytes} bytes.");
                }

                byte[] bytes = File.ReadAllBytes(NotesPath);
                string json = StrictUtf8.GetString(bytes);
                List<QuickCaptureNote>? stored = JsonSerializer.Deserialize<List<QuickCaptureNote>>(json);
                _notes = NormalizeLoaded(stored);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                _notes = new List<QuickCaptureNote>();
                LastError = "The notes file could not be read safely: " + exception.Message;
            }

            return _notes.ToArray();
        }
    }

    public NoteOperationResult Create(string title, string text, bool isPinned = false)
    {
        string? validationError = Validate(title, text);
        if (validationError is not null)
        {
            return new NoteOperationResult(NoteOperationStatus.Invalid, Error: validationError);
        }

        lock (_sync)
        {
            if (_notes.Count >= MaxNotes)
            {
                return new NoteOperationResult(
                    NoteOperationStatus.CapacityReached,
                    Error: $"At most {MaxNotes} notes can be saved.");
            }

            var note = new QuickCaptureNote(
                Guid.NewGuid().ToString("N"),
                title.Trim(),
                text,
                DateTimeOffset.UtcNow,
                isPinned);
            var candidate = new List<QuickCaptureNote>(_notes.Count + 1) { note };
            candidate.AddRange(_notes);
            return Commit(candidate, note);
        }
    }

    public NoteOperationResult Update(string id, string title, string text, bool isPinned)
    {
        string? validationError = Validate(title, text);
        if (string.IsNullOrWhiteSpace(id))
        {
            validationError = "A note id is required.";
        }
        if (validationError is not null)
        {
            return new NoteOperationResult(NoteOperationStatus.Invalid, Error: validationError);
        }

        lock (_sync)
        {
            int index = _notes.FindIndex(note => string.Equals(note.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return new NoteOperationResult(NoteOperationStatus.NotFound, Error: "The note was not found.");
            }

            QuickCaptureNote updated = _notes[index] with
            {
                Title = title.Trim(),
                Text = text,
                IsPinned = isPinned
            };
            var candidate = new List<QuickCaptureNote>(_notes);
            candidate[index] = updated;
            return Commit(candidate, updated);
        }
    }

    public NoteOperationResult SetPinned(string id, bool isPinned)
    {
        lock (_sync)
        {
            int index = _notes.FindIndex(note => string.Equals(note.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return new NoteOperationResult(NoteOperationStatus.NotFound, Error: "The note was not found.");
            }

            QuickCaptureNote updated = _notes[index] with { IsPinned = isPinned };
            var candidate = new List<QuickCaptureNote>(_notes);
            candidate[index] = updated;
            return Commit(candidate, updated);
        }
    }

    public NoteOperationResult Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new NoteOperationResult(NoteOperationStatus.Invalid, Error: "A note id is required.");
        }

        lock (_sync)
        {
            int index = _notes.FindIndex(note => string.Equals(note.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return new NoteOperationResult(NoteOperationStatus.NotFound, Error: "The note was not found.");
            }

            QuickCaptureNote deleted = _notes[index];
            var candidate = new List<QuickCaptureNote>(_notes);
            candidate.RemoveAt(index);
            return Commit(candidate, deleted);
        }
    }

    private NoteOperationResult Commit(List<QuickCaptureNote> candidate, QuickCaptureNote note)
    {
        string temporaryPath = NotesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string? directory = Path.GetDirectoryName(NotesPath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("The notes path has no parent directory.");
            }

            Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(candidate, JsonOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, NotesPath, true);
            _notes = candidate;
            LastError = null;
            return new NoteOperationResult(NoteOperationStatus.Success, note);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            LastError = "The notes file could not be saved: " + exception.Message;
            return new NoteOperationResult(NoteOperationStatus.StorageError, Error: LastError);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static List<QuickCaptureNote> NormalizeLoaded(List<QuickCaptureNote>? stored)
    {
        if (stored is null)
        {
            return new List<QuickCaptureNote>();
        }

        var normalized = new List<QuickCaptureNote>(Math.Min(stored.Count, MaxNotes));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (QuickCaptureNote? note in stored)
        {
            if (normalized.Count == MaxNotes)
            {
                break;
            }
            if (note is null || string.IsNullOrWhiteSpace(note.Id) || !ids.Add(note.Id) ||
                string.IsNullOrWhiteSpace(note.Title) || note.Title.Length > MaxTitleLength ||
                note.Text is null || note.Text.Length > MaxNoteTextLength || note.CreatedAt == default)
            {
                continue;
            }

            normalized.Add(note with { Title = note.Title.Trim() });
        }
        return normalized;
    }

    private static string? Validate(string? title, string? text)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "A note title is required.";
        }
        if (title.Trim().Length > MaxTitleLength)
        {
            return $"A note title cannot exceed {MaxTitleLength} characters.";
        }
        if (text is null)
        {
            return "Note text is required.";
        }
        if (text.Length > MaxNoteTextLength)
        {
            return $"Note text cannot exceed {MaxNoteTextLength} characters.";
        }
        return null;
    }

    private static string NormalizeLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A notes path is required.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Notes must use a local path.", nameof(path));
        }
        return fullPath;
    }

    private static bool IsStorageFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or
        InvalidDataException or InvalidOperationException or NotSupportedException or
        System.Security.SecurityException;
}
