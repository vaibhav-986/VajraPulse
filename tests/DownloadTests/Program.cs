using VajraPulse.Services;

var tests = new (string Name, Action Run)[]
{
    ("Reports observed bytes without a fabricated total", TestObservedBytesAndStall),
    ("Completes when a temporary file becomes its final file", TestCompletion),
    ("Fails a vanished download after the grace period", TestVanishedFailure),
    ("Tracks tmp files only when created after startup", TestTmpConfidence),
    ("Ignores unsupported suffixes and nested folders", TestScopeAndSuffixes),
    ("Bounds the snapshot to 25 activities", TestCapacity),
    ("Isolates event subscribers and emits only meaningful changes", TestChangedEvent),
    ("Rejects UNC roots", TestUncRoot)
};

int failures = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        Console.WriteLine("PASS " + name);
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void TestObservedBytesAndStall()
{
    InTemporaryDirectory(folder =>
    {
        string tempPath = Path.Combine(folder, "archive.zip.crdownload");
        File.WriteAllBytes(tempPath, new byte[] { 1, 2, 3 });
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var service = CreateService(folder, clock);

        DownloadActivity activity = Single(service.Snapshot, "initial activity");
        Equal("archive.zip", activity.FileName, "display name");
        Equal(3L, activity.BytesReceived, "initial bytes");
        Equal<long?>(null, activity.TotalBytes, "unknown total");
        Equal(DownloadState.Active, activity.State, "initial state");

        clock.Advance(TimeSpan.FromSeconds(2));
        using (var stream = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(new byte[] { 4, 5, 6, 7 });
        }
        service.Refresh();
        activity = Single(service.Snapshot, "grown activity");
        Equal(7L, activity.BytesReceived, "grown bytes");
        Equal(clock.Now, activity.LastProgressAt, "growth timestamp");

        clock.Advance(TimeSpan.FromSeconds(11));
        service.Refresh();
        Equal(DownloadState.Stalled, Single(service.Snapshot, "stalled activity").State, "stalled state");
    });
}

static void TestCompletion()
{
    InTemporaryDirectory(folder =>
    {
        string tempPath = Path.Combine(folder, "manual.pdf.part");
        string finalPath = Path.Combine(folder, "manual.pdf");
        File.WriteAllBytes(tempPath, new byte[] { 1, 2, 3, 4, 5 });
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var service = CreateService(folder, clock);
        string id = Single(service.Snapshot, "active download").Id;

        clock.Advance(TimeSpan.FromSeconds(1));
        File.Move(tempPath, finalPath);
        service.Refresh();

        DownloadActivity completed = Single(service.Snapshot, "completed download");
        Equal(id, completed.Id, "stable id");
        Equal(DownloadState.Completed, completed.State, "completed state");
        Equal(finalPath, completed.FinalPath, "final path");
        Equal(5L, completed.BytesReceived, "completed bytes");
        Equal(clock.Now, completed.CompletedAt, "completion timestamp");
        Equal<DateTimeOffset?>(null, completed.FailedAt, "no failure timestamp");

        DownloadRevealTarget target = service.RevealTarget(id)
            ?? throw new InvalidOperationException("Reveal target was absent.");
        Equal(folder, target.DirectoryPath, "reveal directory");
        Equal(finalPath, target.ItemPath, "reveal item");
    });
}

static void TestVanishedFailure()
{
    InTemporaryDirectory(folder =>
    {
        string tempPath = Path.Combine(folder, "missing.iso.partial");
        File.WriteAllBytes(tempPath, new byte[] { 1, 2, 3 });
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var service = CreateService(folder, clock);

        File.Delete(tempPath);
        service.Refresh();
        Equal(DownloadState.Active, Single(service.Snapshot, "grace activity").State, "state during grace");

        clock.Advance(TimeSpan.FromSeconds(6));
        service.Refresh();
        DownloadActivity failed = Single(service.Snapshot, "failed activity");
        Equal(DownloadState.Failed, failed.State, "failed state");
        Equal(clock.Now, failed.FailedAt, "failure timestamp");
        Equal<string?>(null, failed.FinalPath, "missing final path");

        DownloadRevealTarget target = service.RevealTarget(failed.Id)
            ?? throw new InvalidOperationException("Reveal target was absent.");
        Equal(folder, target.DirectoryPath, "failed reveal directory");
        Equal<string?>(null, target.ItemPath, "failed reveal has no missing item");
    });
}

static void TestTmpConfidence()
{
    InTemporaryDirectory(folder =>
    {
        string oldTmp = Path.Combine(folder, "unrelated.tmp");
        File.WriteAllText(oldTmp, "pre-existing temporary file");
        DateTimeOffset oldCreated = new(File.GetCreationTimeUtc(oldTmp), TimeSpan.Zero);
        var clock = new TestClock(oldCreated.AddSeconds(2));
        using var service = CreateService(folder, clock);
        Equal(0, service.Snapshot.Count, "pre-existing tmp ignored");

        string newTmp = Path.Combine(folder, "fresh.tmp");
        File.WriteAllText(newTmp, "new temporary download");
        File.SetCreationTimeUtc(newTmp, clock.Now.UtcDateTime.AddSeconds(1));
        service.Refresh();
        DownloadActivity activity = Single(service.Snapshot, "new tmp activity");
        Equal(newTmp, activity.TempPath, "new tmp path");
    });
}

static void TestScopeAndSuffixes()
{
    InTemporaryDirectory(folder =>
    {
        File.WriteAllText(Path.Combine(folder, "ordinary.txt"), "not a download temp file");
        File.WriteAllText(Path.Combine(folder, "browser.opdownload"), "unsupported suffix");
        string nested = Path.Combine(folder, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "nested.zip.crdownload"), "nested");

        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var service = CreateService(folder, clock);
        Equal(0, service.Snapshot.Count, "unsupported and nested files ignored");
    });
}

static void TestCapacity()
{
    InTemporaryDirectory(folder =>
    {
        for (int index = 0; index < 30; index++)
        {
            File.WriteAllText(Path.Combine(folder, $"file-{index:D2}.bin.download"), index.ToString());
        }

        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var service = CreateService(folder, clock);
        Equal(DownloadProgressService.Capacity, service.Snapshot.Count, "snapshot capacity");
        True(service.Snapshot.All(activity => activity.TotalBytes == null), "all totals remain unknown");
    });
}

static void TestChangedEvent()
{
    InTemporaryDirectory(folder =>
    {
        string tempPath = Path.Combine(folder, "events.dat.crdownload");
        File.WriteAllText(tempPath, "one");
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var service = CreateService(folder, clock);
        int delivered = 0;
        service.Changed += (_, _) => throw new InvalidOperationException("subscriber failure");
        service.Changed += (_, _) => delivered++;

        clock.Advance(TimeSpan.FromSeconds(1));
        File.AppendAllText(tempPath, "two");
        service.Refresh();
        Equal(1, delivered, "later subscriber still receives change");

        service.Refresh();
        Equal(1, delivered, "unchanged refresh does not emit");
    });
}

static void TestUncRoot()
{
    Throws<ArgumentException>(() => new DownloadProgressService("\\\\server\\share\\Downloads"), "UNC root");
}

static DownloadProgressService CreateService(string folder, TestClock clock)
{
    return new DownloadProgressService(
        folder,
        clock.GetNow,
        Timeout.InfiniteTimeSpan,
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(5),
        enableWatcher: false);
}

static void InTemporaryDirectory(Action<string> action)
{
    string folder = Path.Combine(Path.GetTempPath(), "vajra-download-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        action(folder);
    }
    finally
    {
        Directory.Delete(folder, recursive: true);
    }
}

static DownloadActivity Single(IReadOnlyList<DownloadActivity> values, string message)
{
    if (values.Count != 1)
    {
        throw new InvalidOperationException($"{message}: expected one item, got {values.Count}.");
    }

    return values[0];
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message + ": expected true.");
    }
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}.");
    }
}

static void Throws<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"{message}: expected {typeof(TException).Name}.");
}

file sealed class TestClock(DateTimeOffset now)
{
    public DateTimeOffset Now => now;
    public DateTimeOffset GetNow() => now;
    public void Advance(TimeSpan amount) => now = now.Add(amount);
}
