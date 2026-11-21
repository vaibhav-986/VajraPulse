using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VajraPulse.Services;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        Check(!ClipboardHistoryPolicy.IsSensitiveText("Agenda for the 10:30 design review"), "Ordinary text is accepted");
        Check(ClipboardHistoryPolicy.IsSensitiveText("password = correct-horse-battery-staple"), "Named passwords are sensitive");
        Check(ClipboardHistoryPolicy.IsSensitiveText("-----BEGIN PRIVATE KEY-----\nfixture\n-----END PRIVATE KEY-----"), "Private keys are sensitive");
        Check(ClipboardHistoryPolicy.IsSensitiveText("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.fixtureSignature"), "JWT-shaped tokens are sensitive");
        Check(ClipboardHistoryPolicy.IsSensitiveText("4111 1111 1111 1111"), "Luhn-valid card-like text is sensitive");
        Check(!ClipboardHistoryPolicy.IsSensitiveText("Order 4111-1111-1111-1112"), "Invalid card-like text is not classified as a card");

        DateTimeOffset focus = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Check(ClipboardHistoryPolicy.IsWithinPasswordFocusWindow(focus.AddSeconds(2), focus), "Password focus window includes two-second boundary");
        Check(!ClipboardHistoryPolicy.IsWithinPasswordFocusWindow(focus.AddMilliseconds(2001), focus), "Password focus window expires deterministically");
        Check(!ClipboardHistoryPolicy.IsWithinPasswordFocusWindow(focus.AddMilliseconds(-1), focus), "Events before password focus are not attributed to it");

        string oversized = new('a', ClipboardHistoryPolicy.MaxTextCharacters + 73);
        string bounded = ClipboardHistoryPolicy.BoundText(oversized);
        Check(bounded.Length == ClipboardHistoryPolicy.MaxTextCharacters, "Text is bounded to 10,000 characters");

        DateTimeOffset t0 = focus;
        var entries = new ObservableCollection<ClipboardHistoryEntry>
        {
            ClipboardHistoryEntry.CreateText("same content", t0)
        };
        entries[0].IsPinned = true;
        ClipboardHistoryEntry replacement = ClipboardHistoryEntry.CreateText("same content", t0.AddSeconds(1));
        Check(ClipboardHistoryPolicy.InsertNewestDeduplicated(entries, replacement, 20), "Dedupe helper accepts a repeated newest item");
        Check(entries.Count == 1 && ReferenceEquals(entries[0], replacement), "Dedupe helper replaces rather than duplicates content");
        Check(entries[0].Timestamp == t0.AddSeconds(1) && entries[0].IsPinned, "Dedupe refreshes time and preserves pin state");

        using (var history = new ClipboardHistoryService(2))
        {
            Check(history.AddCapturedText(oversized, t0), "Bounded text can be added without clipboard access");
            Check(history.Entries[0].Text?.Length == ClipboardHistoryPolicy.MaxTextCharacters, "Stored text never exceeds its bound");
            int skipped = 0;
            SensitiveClipboardReason? skippedReason = null;
            history.SensitiveItemSkipped += (_, args) => { skipped++; skippedReason = args.Reason; };
            Check(!history.AddCapturedText("api_key: fixtureVerySecret123", t0), "Sensitive text is not stored");
            Check(skipped == 1 && skippedReason == SensitiveClipboardReason.SecretPattern, "Sensitive skip event exposes reason but no content");

            history.ClearAll();
            history.AddCapturedText("alpha words", t0);
            history.AddCapturedText("beta words", t0.AddSeconds(1));
            ClipboardHistoryEntry pinned = history.Entries[1];
            Check(history.Pin(pinned), "Entry can be pinned");
            history.AddCapturedText("gamma words", t0.AddSeconds(2));
            Check(history.Entries.Count == 2 && history.Entries.Contains(pinned), "Capacity eviction preserves pinned entries");
            Check(history.ClearUnpinned() == 1 && history.Entries.Count == 1 && history.Entries[0].IsPinned, "Clear unpinned preserves pins");
            Check(history.Unpin(pinned) && history.ClearAll() == 1 && history.Entries.Count == 0, "Unpin and clear all remove retained entries");
        }

        string root = Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\";
        string[] paths = Enumerable.Range(0, 40).Select(i => Path.Combine(root, "clipboard-fixture", $"item-{i}.txt")).ToArray();
        IReadOnlyList<string> localFiles = ClipboardHistoryPolicy.NormalizeLocalFiles(paths);
        Check(localFiles.Count == ClipboardHistoryPolicy.MaxFileReferences, "File references are bounded to 30");
        Check(ClipboardHistoryPolicy.NormalizeLocalPath("\\\\server\\share\\secret.txt") == null, "UNC network references are rejected without access");
        Check(localFiles.All(Path.IsPathFullyQualified), "Captured file references are fully qualified local paths");

        byte[] pixels = new byte[3000 * 4];
        BitmapSource source = BitmapSource.Create(3000, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 3000 * 4);
        BitmapSource? image = ClipboardHistoryPolicy.CreateBoundedImage(source);
        Check(image != null && image.PixelWidth <= ClipboardHistoryPolicy.MaxImageDimension && image.PixelHeight <= ClipboardHistoryPolicy.MaxImageDimension, "Image dimensions are bounded");
        Check(image != null && (long)image.PixelWidth * image.PixelHeight * 4 <= ClipboardHistoryPolicy.MaxImageBytes, "Stored image memory is bounded");
        Check(image?.IsFrozen == true && !ReferenceEquals(image, source), "Stored image is a detached frozen clone");

        Console.WriteLine($"{_passed} clipboard checks passed. The system clipboard was not read or changed.");
        return 0;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
        _passed++;
    }
}
