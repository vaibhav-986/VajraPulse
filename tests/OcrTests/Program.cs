using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VajraPulse.Services;

internal static class Program
{
    private static int _passed;

    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Image bounds reject unsafe decoded sizes", ImageBounds),
            ("Generated bitmap returns bounded metadata", GeneratedBitmapMetadata),
            ("Cancellation is reported without OCR", GeneratedBitmapCancellation),
            ("Note CRUD persists only to an isolated path", NoteCrud),
            ("Note limits are enforced", NoteLimits),
            ("Corrupt notes are handled safely", CorruptNotes)
        };

        foreach ((string name, Func<Task> run) in tests)
        {
            try
            {
                await run();
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
                return 1;
            }
        }

        Console.WriteLine($"All {_passed} OCR and note tests passed. No clipboard, screenshots, or user notes were accessed.");
        return 0;
    }

    private static Task ImageBounds()
    {
        True(ScreenCaptureOcrService.IsImageWithinLimits(1920, 1080, 32, out long normalBytes),
            "a normal bitmap should be accepted");
        Equal(1920L * 1080 * 4, normalBytes, "decoded BGRA byte estimate");

        False(ScreenCaptureOcrService.IsImageWithinLimits(4096, 4096, 32, out long largeBytes),
            "a 16 MP BGRA bitmap exceeds the decoded byte limit");
        True(largeBytes > ScreenCaptureOcrService.MaxImageBytes, "large image byte estimate");
        False(ScreenCaptureOcrService.IsImageWithinLimits(20_000, 20_000, 1, out _),
            "pixel limit applies even to one-bit sources");
        False(ScreenCaptureOcrService.IsImageWithinLimits(0, 100, 32, out _),
            "zero dimensions are invalid");
        False(ScreenCaptureOcrService.IsImageWithinLimits(int.MaxValue, int.MaxValue, 32, out _),
            "overflow-sized dimensions are rejected");
        return Task.CompletedTask;
    }

    private static async Task GeneratedBitmapMetadata()
    {
        BitmapSource bitmap = GeneratedBitmap();
        var service = new ScreenCaptureOcrService();
        ClipboardOcrResult result = await service.RecognizeImageAsync(bitmap, timeout: TimeSpan.Zero);

        Equal(OcrReadStatus.Failed, result.Status, "an invalid timeout should fail before OCR");
        NotNull(result.SourceImage, "source metadata");
        Equal(2, result.SourceImage!.PixelWidth, "source width");
        Equal(2, result.SourceImage.PixelHeight, "source height");
        Equal(16L, result.SourceImage.EstimatedBytes, "source decoded bytes");
        Equal(string.Empty, result.Text, "failed OCR returns no invented text");
        Equal(0, result.Lines.Count, "failed OCR returns no invented lines");
    }

    private static async Task GeneratedBitmapCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new ScreenCaptureOcrService();
        ClipboardOcrResult result = await service.RecognizeImageAsync(GeneratedBitmap(), cancellation.Token);
        Equal(OcrReadStatus.Cancelled, result.Status, "pre-cancelled OCR status");
        NotNull(result.SourceImage, "cancelled result metadata");
    }

    private static Task NoteCrud()
    {
        WithIsolatedNotes((folder, path) =>
        {
            var service = new QuickCaptureNoteService(path);
            Equal(0, service.Notes.Count, "new store starts empty");

            NoteOperationResult created = service.Create(" Fixture note ", "local plain-text fixture");
            True(created.Succeeded, "create succeeds");
            NotNull(created.Note, "created note");
            Equal("Fixture note", created.Note!.Title, "title is normalized");
            True(File.Exists(path), "notes file is created in isolated folder");
            True(File.ReadAllText(path).Contains("local plain-text fixture", StringComparison.Ordinal),
                "JSON stores note text locally as plain text");

            DateTimeOffset createdAt = created.Note.CreatedAt;
            NoteOperationResult updated = service.Update(
                created.Note.Id,
                "Updated fixture",
                "updated body",
                true);
            True(updated.Succeeded, "update succeeds");
            Equal(createdAt, updated.Note!.CreatedAt, "update preserves creation time");
            True(updated.Note.IsPinned, "update persists pin state");

            var reloaded = new QuickCaptureNoteService(path);
            Equal(1, reloaded.Notes.Count, "saved note reloads");
            Equal("updated body", reloaded.Notes[0].Text, "updated body reloads");
            True(reloaded.Notes[0].IsPinned, "pin reloads");

            True(reloaded.SetPinned(created.Note.Id, false).Succeeded, "pin can be cleared");
            True(reloaded.Delete(created.Note.Id).Succeeded, "delete succeeds");
            Equal(0, new QuickCaptureNoteService(path).Notes.Count, "delete persists");
            Equal(0, Directory.GetFiles(folder, "*.tmp").Length, "atomic-save temporary files are cleaned");
            Equal(NoteOperationStatus.NotFound, reloaded.Delete(created.Note.Id).Status,
                "deleting a missing note is truthful");
        });
        return Task.CompletedTask;
    }

    private static Task NoteLimits()
    {
        WithIsolatedNotes((_, path) =>
        {
            var service = new QuickCaptureNoteService(path);
            Equal(NoteOperationStatus.Invalid, service.Create("", "body").Status, "empty title rejected");
            Equal(NoteOperationStatus.Invalid,
                service.Create("title", new string('x', QuickCaptureNoteService.MaxNoteTextLength + 1)).Status,
                "oversized body rejected");
            Equal(NoteOperationStatus.Invalid,
                service.Create(new string('t', QuickCaptureNoteService.MaxTitleLength + 1), "body").Status,
                "oversized title rejected");

            for (int index = 0; index < QuickCaptureNoteService.MaxNotes; index++)
            {
                True(service.Create("Note " + index, "fixture").Succeeded, "capacity fixture " + index);
            }
            Equal(NoteOperationStatus.CapacityReached, service.Create("One too many", "fixture").Status,
                "note capacity is enforced");
            Equal(QuickCaptureNoteService.MaxNotes, new QuickCaptureNoteService(path).Notes.Count,
                "capacity-sized store reloads");
        });
        return Task.CompletedTask;
    }

    private static Task CorruptNotes()
    {
        WithIsolatedNotes((_, path) =>
        {
            File.WriteAllText(path, "{ definitely not valid JSON");
            var service = new QuickCaptureNoteService(path);
            Equal(0, service.Notes.Count, "corrupt file yields an empty safe view");
            NotNull(service.LastError, "corruption is reported");
            Equal("{ definitely not valid JSON", File.ReadAllText(path),
                "loading does not modify the corrupt file");

            True(service.Create("Recovery", "explicit replacement").Succeeded,
                "an explicit create can replace a corrupt store");
            Equal(1, new QuickCaptureNoteService(path).Notes.Count, "recovered store reloads");
        });
        return Task.CompletedTask;
    }

    private static BitmapSource GeneratedBitmap()
    {
        byte[] pixels =
        {
            0, 0, 0, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 0, 0, 0, 255
        };
        BitmapSource bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        bitmap.Freeze();
        return bitmap;
    }

    private static void WithIsolatedNotes(Action<string, string> action)
    {
        string folder = Path.Combine(Path.GetTempPath(), "VajraPulse-OcrTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "notes.json");
        try
        {
            action(folder, path);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}. Expected: {expected}; actual: {actual}");
        }
    }

    private static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new InvalidOperationException(message);
        }
    }
}
