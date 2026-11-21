using VajraPulse.Core;

var tests = new (string Name, Action Run)[]
{
    ("Default priorities and ordering", TestOrdering),
    ("Upsert replaces the same id", TestReplacement),
    ("Tick expires transient activities at the deadline", TestExpiration),
    ("Persistent activities do not expire", TestPersistentExpiration),
    ("Capacity retains the best thirty activities", TestCapacity),
    ("Changed is raised only for state changes", TestChangedEvent),
    ("Changed subscribers can re-enter the coordinator", TestReentrantSubscriber),
    ("Activity values are validated", TestValidation),
    ("ClearKind removes only the selected kind", TestClearKind)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void TestOrdering()
{
    var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var coordinator = new ActivityCoordinator(() => now);

    coordinator.Upsert(Activity("clipboard", PulseActivityKind.Clipboard, now.AddSeconds(5)));
    coordinator.Upsert(Activity("download", PulseActivityKind.Download, now.AddSeconds(4)));
    coordinator.Upsert(Activity("capture", PulseActivityKind.Capture, now.AddSeconds(3)));
    coordinator.Upsert(Activity("privacy-old", PulseActivityKind.Privacy, now.AddSeconds(1)));
    coordinator.Upsert(Activity("privacy-new", PulseActivityKind.Privacy, now.AddSeconds(2)));

    var snapshot = coordinator.Snapshot();
    SequenceEqual(
        new[] { "privacy-new", "privacy-old", "capture", "download", "clipboard" },
        snapshot.Select(activity => activity.Id),
        "Priority then recency ordering");
    Equal("privacy-new", coordinator.Current?.Id, "Current activity");

    Equal(PulseActivity.PrivacyPriority, PulseActivity.GetDefaultPriority(PulseActivityKind.Privacy), "Privacy priority");
    Equal(PulseActivity.UserInvokedPriority, PulseActivity.GetDefaultPriority(PulseActivityKind.Note), "Note priority");
    Equal(PulseActivity.TransientStatusPriority, PulseActivity.GetDefaultPriority(PulseActivityKind.Bluetooth), "Bluetooth priority");
    Equal(PulseActivity.ClipboardPriority, PulseActivity.GetDefaultPriority(PulseActivityKind.Clipboard), "Clipboard priority");
}

static void TestReplacement()
{
    var now = DateTimeOffset.UtcNow;
    var coordinator = new ActivityCoordinator(() => now);
    coordinator.Upsert(Activity("same", PulseActivityKind.Clipboard, now));
    coordinator.Upsert(new PulseActivity(
        "same",
        PulseActivityKind.Note,
        "Replacement",
        "Updated",
        now.AddSeconds(1),
        isPersistent: true));

    Equal(1, coordinator.Count, "Replacement count");
    Equal(PulseActivityKind.Note, coordinator.Current?.Kind, "Replacement kind");
    Equal("Replacement", coordinator.Current?.Title, "Replacement title");
}

static void TestExpiration()
{
    var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    var coordinator = new ActivityCoordinator(clock.GetNow);
    var expiresAt = clock.GetNow().AddSeconds(5);
    coordinator.Upsert(Activity("temporary", PulseActivityKind.Download, clock.GetNow(), expiresAt));

    clock.Advance(TimeSpan.FromTicks(TimeSpan.FromSeconds(5).Ticks - 1));
    Equal(0, coordinator.Tick(), "Nothing expires before the deadline");
    Equal(1, coordinator.Count, "Activity remains before deadline");

    clock.Advance(TimeSpan.FromTicks(1));
    Equal(1, coordinator.Tick(), "Activity expires at the exact deadline");
    Equal(0, coordinator.Count, "Expired activity is removed");

    coordinator.Upsert(Activity("already-expired", PulseActivityKind.Download, clock.GetNow(), clock.GetNow()));
    Equal(0, coordinator.Count, "Already-expired activity is not inserted");
}

static void TestPersistentExpiration()
{
    var clock = new TestClock(DateTimeOffset.UtcNow);
    var coordinator = new ActivityCoordinator(clock.GetNow);
    coordinator.Upsert(new PulseActivity(
        "persistent",
        PulseActivityKind.Privacy,
        "Camera active",
        "Camera is in use",
        clock.GetNow(),
        expiresAt: clock.GetNow().AddSeconds(1),
        isPersistent: true));

    clock.Advance(TimeSpan.FromDays(1));
    Equal(0, coordinator.Tick(), "Persistent activity does not expire");
    Equal("persistent", coordinator.Current?.Id, "Persistent activity remains current");
}

static void TestCapacity()
{
    var now = DateTimeOffset.UtcNow;
    var coordinator = new ActivityCoordinator(() => now);
    for (var index = 0; index < ActivityCoordinator.MaximumActivities; index++)
    {
        coordinator.Upsert(new PulseActivity(
            $"item-{index:D2}",
            PulseActivityKind.Download,
            $"Item {index}",
            string.Empty,
            priority: 10,
            createdAt: now.AddSeconds(index)));
    }

    coordinator.Upsert(new PulseActivity(
        "important",
        PulseActivityKind.Privacy,
        "Privacy",
        string.Empty,
        priority: 100,
        createdAt: now));

    Equal(ActivityCoordinator.MaximumActivities, coordinator.Count, "Capacity");
    True(coordinator.Snapshot().Any(activity => activity.Id == "important"), "Higher-ranked activity is retained");
    False(coordinator.Snapshot().Any(activity => activity.Id == "item-00"), "Lowest-ranked activity is evicted");

    coordinator.Upsert(new PulseActivity(
        "too-low",
        PulseActivityKind.Clipboard,
        "Low",
        string.Empty,
        priority: -1,
        createdAt: now.AddDays(1)));
    Equal(ActivityCoordinator.MaximumActivities, coordinator.Count, "Capacity after low-ranked insert");
    False(coordinator.Snapshot().Any(activity => activity.Id == "too-low"), "Lower-ranked overflow is discarded");
}

static void TestChangedEvent()
{
    var now = DateTimeOffset.UtcNow;
    var coordinator = new ActivityCoordinator(() => now);
    var changes = 0;
    coordinator.Changed += (_, _) => changes++;

    coordinator.Upsert(Activity("one", PulseActivityKind.Note, now));
    coordinator.Upsert(Activity("one", PulseActivityKind.Note, now.AddSeconds(1)));
    False(coordinator.Remove("missing"), "Removing a missing id reports false");
    Equal(0, coordinator.ClearKind(PulseActivityKind.Download), "Clearing an absent kind");
    Equal(0, coordinator.Tick(), "Tick without expirations");
    True(coordinator.Remove("one"), "Removing an existing id reports true");

    Equal(3, changes, "Changed event count");
}

static void TestReentrantSubscriber()
{
    var now = DateTimeOffset.UtcNow;
    var coordinator = new ActivityCoordinator(() => now);
    var entered = false;
    coordinator.Changed += (_, _) =>
    {
        _ = coordinator.Snapshot();
        _ = coordinator.Current;
        if (!entered)
        {
            entered = true;
            coordinator.Upsert(Activity("nested", PulseActivityKind.Clipboard, now));
        }
    };

    coordinator.Upsert(Activity("outer", PulseActivityKind.Privacy, now));

    Equal(2, coordinator.Count, "Reentrant upsert completes");
    Equal("outer", coordinator.Current?.Id, "Reentrant activity ordering");
}

static void TestValidation()
{
    var now = DateTimeOffset.UtcNow;
    Throws<ArgumentException>(() => Activity(" ", PulseActivityKind.Note, now), "Blank id");
    Throws<ArgumentException>(() => new PulseActivity("id", PulseActivityKind.Note, " ", string.Empty, now), "Blank title");
    Throws<ArgumentException>(() => new PulseActivity("id", PulseActivityKind.Note, new string('t', PulseActivity.MaximumTitleLength + 1), string.Empty, now), "Long title");
    Throws<ArgumentException>(() => new PulseActivity("id", PulseActivityKind.Note, "Title", new string('d', PulseActivity.MaximumDetailLength + 1), now), "Long detail");
    Throws<ArgumentOutOfRangeException>(() => new PulseActivity("id", PulseActivityKind.Download, "Title", string.Empty, now, progress: -0.01), "Negative progress");
    Throws<ArgumentOutOfRangeException>(() => new PulseActivity("id", PulseActivityKind.Download, "Title", string.Empty, now, progress: double.NaN), "Non-finite progress");
    Throws<ArgumentException>(() => new PulseActivity("id", PulseActivityKind.Download, "Title", string.Empty, now, progress: 0.5, isIndeterminate: true), "Conflicting progress modes");

    var complete = new PulseActivity("complete", PulseActivityKind.Download, "Done", string.Empty, now, progress: 1);
    Equal(1d, complete.Progress, "Progress upper bound");
}

static void TestClearKind()
{
    var now = DateTimeOffset.UtcNow;
    var coordinator = new ActivityCoordinator(() => now);
    coordinator.Upsert(Activity("note-1", PulseActivityKind.Note, now));
    coordinator.Upsert(Activity("note-2", PulseActivityKind.Note, now.AddSeconds(1)));
    coordinator.Upsert(Activity("capture", PulseActivityKind.Capture, now));

    Equal(2, coordinator.ClearKind(PulseActivityKind.Note), "Removed note count");
    SequenceEqual(new[] { "capture" }, coordinator.Snapshot().Select(activity => activity.Id), "Remaining kind");
}

static PulseActivity Activity(
    string id,
    PulseActivityKind kind,
    DateTimeOffset createdAt,
    DateTimeOffset? expiresAt = null) =>
    new(id, kind, id, string.Empty, createdAt, expiresAt);

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{message}: expected true.");
    }
}

static void False(bool condition, string message) => True(!condition, message);

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}.");
    }
}

static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException($"{message}: sequences differ.");
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
    public DateTimeOffset GetNow() => now;

    public void Advance(TimeSpan amount) => now = now.Add(amount);
}
