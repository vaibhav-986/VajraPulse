using System;
using System.IO;
using System.Linq;
using VajraPulse.Services;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
string folder = Path.Combine(Path.GetTempPath(), "pulse-feature-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    string file = Path.Combine(folder, "fixture.txt");
    File.WriteAllText(file, "This is a local test fixture, not a user document.");
    var shelf = new FileShelfService();
    Check(shelf.Add(new[] { file, file.ToUpperInvariant() }) == 1, "Shelf deduplicates paths case-insensitively");
    Check(shelf.Files[0].Exists && shelf.Files[0].Name == "fixture.txt", "Shelf stores a valid local file reference");
    shelf.Remove(shelf.Files[0]);
    Check(File.Exists(file) && File.ReadAllText(file).Contains("local test fixture"), "Removing a shelf shortcut never deletes or modifies its file");
    Check(shelf.Add(new[] { Path.Combine(folder, "missing.txt") }) == 0, "Missing files are not added");
    Check(FileShelfService.Normalize("\\\\server\\share\\file.txt") == null, "UNC network paths are not accessed by the local shelf");
    Check(shelf.Add(new[] { folder }) == 1 && shelf.Files[0].IsDirectory, "Local folders can be shelved");
    var paths = Enumerable.Range(0, 40).Select(i => Path.Combine(folder, $"capacity-{i}.txt")).ToArray();
    foreach (string path in paths) File.WriteAllText(path, "fixture");
    shelf.Add(paths);
    Check(shelf.Files.Count == FileShelfService.Capacity, "Shelf enforces its capacity");
    Check(shelf.SavedPaths().Length == FileShelfService.Capacity, "Persisted shelf paths match visible references");

    using (var audio = new AudioService())
    {
        audio.Refresh();
        Check(!audio.IsAvailable || (audio.Volume >= 0 && audio.Volume <= 1 && !string.IsNullOrWhiteSpace(audio.DeviceName)), "Default audio device is sensed without changing audio");
    }
    var brightness = BrightnessService.Read();
    Check(!brightness.IsAvailable || brightness.Percent is >= 0 and <= 100, "Brightness sensing returns a supported level or a truthful unavailable state");
    Console.WriteLine("Brightness hardware available: " + brightness.IsAvailable);
    using (var media = new MediaService())
    {
        await media.RefreshAsync();
        var snapshot = media.Snapshot;
        Check(snapshot.CurrentPosition >= TimeSpan.Zero && (snapshot.Duration <= TimeSpan.Zero || snapshot.CurrentPosition <= snapshot.Duration), "Live media timeline stays within its duration");
        Check(snapshot.Artwork == null || snapshot.Artwork.PixelWidth <= 128, "Real media artwork is bounded in memory");
        Console.WriteLine("Current player exposes seeking: " + snapshot.CanSeek);
        if (snapshot.CanSeek && !snapshot.IsPlaying)
        {
            bool unchangedSeek = await media.SeekAsync(snapshot.CurrentPosition);
            Console.WriteLine(unchangedSeek ? "PASS Same-position seek accepted without moving paused playback" : "SKIP Current player rejected a same-position seek request");
            if (unchangedSeek) passed++;
        }
        else Console.WriteLine("SKIP Seek mutation test; playback was left untouched");
    }
    using (var notifications = new NotificationListenerService())
    {
        Check(!notifications.Enabled, "Notification reading is off by default");
        Check((await notifications.SyncAsync(Array.Empty<string>(), false, true)).Count == 0, "Disabled notification sync reads no messages");
        if (!NotificationListenerService.HasPackageIdentity)
            Check(!(await notifications.EnableAsync(false)).Allowed, "Unpackaged notification access is blocked with setup guidance");
    }
    Console.WriteLine($"{passed} feature checks passed. No volume or brightness settings were changed.");
    return 0;
}
finally { Directory.Delete(folder, true); }
