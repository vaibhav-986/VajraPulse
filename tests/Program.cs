using VajraPulse.Core;

var tests = new (string Name, Action Run)[]
{
    ("Island state transitions", TestIslandStateTransitions),
    ("Countdown uses an absolute deadline", TestCountdown),
    ("Pause and resume retain remaining time", TestPauseAndResume),
    ("Completion is raised once", TestCompletionRaisedOnce),
    ("Invalid durations are rejected", TestInvalidDurations),
    ("Pausing at the deadline completes instead of getting stuck", TestPauseAtDeadline),
    ("Reset restores a completed timer", TestResetAfterCompletion),
    ("Running and paused sessions restore safely", TestRestore),
    ("Invalid restored sessions are rejected", TestInvalidRestore),
    ("Inactive pause and resume are harmless", TestInactiveActions)
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

static void TestIslandStateTransitions()
{
    var state = new IslandState();
    Equal(IslandMode.Compact, state.Mode, "Initial mode");

    state.Expand();
    Equal(IslandMode.Expanded, state.Mode, "Expand");

    state.Collapse();
    Equal(IslandMode.Compact, state.Mode, "Collapse");

    state.Toggle();
    Equal(IslandMode.Expanded, state.Mode, "Toggle from compact");
    state.Toggle();
    Equal(IslandMode.Compact, state.Mode, "Toggle from expanded");

    state.OpenSettings();
    Equal(IslandMode.Settings, state.Mode, "Open settings");
    state.CloseSettings();
    Equal(IslandMode.Expanded, state.Mode, "Close settings");
}

static void TestCountdown()
{
    var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    var timer = new FocusTimer(clock.GetNow);

    Equal(TimeSpan.FromMinutes(25), timer.Duration, "Default duration");
    Equal(TimeSpan.FromMinutes(25), timer.Remaining, "Default remaining");
    False(timer.IsRunning, "Timer initially stopped");

    timer.Start(TimeSpan.FromSeconds(10));
    True(timer.IsRunning, "Timer running after start");
    Equal(TimeSpan.FromSeconds(10), timer.Remaining, "Remaining after start");

    clock.Advance(TimeSpan.FromMilliseconds(3250));
    Equal(TimeSpan.FromMilliseconds(6750), timer.Remaining, "Remaining follows deadline");
    timer.Tick();
    True(timer.IsRunning, "Tick before deadline keeps timer running");
}

static void TestPauseAndResume()
{
    var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    var timer = new FocusTimer(clock.GetNow);

    timer.Start(TimeSpan.FromSeconds(10));
    clock.Advance(TimeSpan.FromSeconds(3));
    timer.Pause();

    False(timer.IsRunning, "Paused timer is not running");
    True(timer.IsPaused, "Paused flag");
    Equal(TimeSpan.FromSeconds(7), timer.Remaining, "Remaining at pause");

    clock.Advance(TimeSpan.FromHours(1));
    Equal(TimeSpan.FromSeconds(7), timer.Remaining, "Paused time does not elapse");

    timer.Resume();
    True(timer.IsRunning, "Resumed timer is running");
    False(timer.IsPaused, "Resumed timer is not paused");
    clock.Advance(TimeSpan.FromSeconds(2));
    Equal(TimeSpan.FromSeconds(5), timer.Remaining, "Resumed countdown");

    timer.Reset();
    False(timer.IsRunning, "Reset stops timer");
    False(timer.IsPaused, "Reset clears pause");
    Equal(TimeSpan.FromSeconds(10), timer.Remaining, "Reset restores selected duration");
}

static void TestCompletionRaisedOnce()
{
    var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    var timer = new FocusTimer(clock.GetNow);
    var completions = 0;
    timer.Completed += (_, _) => completions++;

    timer.Start(TimeSpan.FromSeconds(2));
    clock.Advance(TimeSpan.FromSeconds(2));
    Equal(TimeSpan.Zero, timer.Remaining, "Remaining clamps to zero");

    timer.Tick();
    timer.Tick();
    Equal(1, completions, "Completion event count");
    False(timer.IsRunning, "Completed timer is stopped");
    False(timer.IsPaused, "Completed timer is not paused");

    timer.Start(TimeSpan.FromSeconds(1));
    clock.Advance(TimeSpan.FromSeconds(1));
    timer.Tick();
    Equal(2, completions, "A new start permits a new completion");
}

static void TestInvalidDurations()
{
    var timer = new FocusTimer();

    Throws<ArgumentOutOfRangeException>(() => timer.Start(TimeSpan.Zero), "Zero duration");
    Throws<ArgumentOutOfRangeException>(() => timer.Start(TimeSpan.FromTicks(-1)), "Negative duration");
    Throws<ArgumentOutOfRangeException>(() => timer.Start(TimeSpan.FromHours(24) + TimeSpan.FromTicks(1)), "Duration over 24 hours");

    timer.Start(TimeSpan.FromHours(24));
    Equal(TimeSpan.FromHours(24), timer.Duration, "24-hour duration is valid");
}

static void TestPauseAtDeadline()
{
    var clock = new TestClock(DateTimeOffset.UtcNow);
    var timer = new FocusTimer(clock.GetNow);
    int completed = 0;
    timer.Completed += (_, _) => completed++;
    timer.Start(TimeSpan.FromSeconds(1));
    clock.Advance(TimeSpan.FromSeconds(1));
    timer.Pause();
    False(timer.IsPaused, "Expired timer must not pause at zero");
    False(timer.IsRunning, "Expired timer stops");
    Equal(1, completed, "Pause at deadline delivers completion");
    timer.Resume();
    timer.Tick();
    Equal(1, completed, "Completion remains once only");
}

static void TestResetAfterCompletion()
{
    var clock = new TestClock(DateTimeOffset.UtcNow);
    var timer = new FocusTimer(clock.GetNow);
    timer.Start(TimeSpan.FromSeconds(2));
    clock.Advance(TimeSpan.FromSeconds(3));
    timer.Tick();
    timer.Reset();
    Equal(TimeSpan.FromSeconds(2), timer.Remaining, "Reset restores duration after completion");
    False(timer.IsRunning, "Reset does not start timer");
    False(timer.IsPaused, "Reset does not pause timer");
}

static void TestRestore()
{
    var clock = new TestClock(DateTimeOffset.UtcNow);
    var timer = new FocusTimer(clock.GetNow);

    timer.Restore(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(12), isRunning: true, isPaused: false);
    True(timer.IsRunning, "Restored running state");
    False(timer.IsPaused, "Restored running timer is not paused");
    Equal(TimeSpan.FromMinutes(25), timer.Duration, "Restored duration");
    clock.Advance(TimeSpan.FromMinutes(2));
    Equal(TimeSpan.FromMinutes(10), timer.Remaining, "Restored running timer follows a new deadline");

    timer.Restore(TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(7), isRunning: false, isPaused: true);
    False(timer.IsRunning, "Restored paused timer is not running");
    True(timer.IsPaused, "Restored paused state");
    clock.Advance(TimeSpan.FromHours(1));
    Equal(TimeSpan.FromMinutes(7), timer.Remaining, "Restored paused time stays fixed");
    timer.Reset();
    Equal(TimeSpan.FromMinutes(40), timer.Remaining, "Reset uses the restored session duration");
}

static void TestInvalidRestore()
{
    var timer = new FocusTimer();
    Throws<ArgumentOutOfRangeException>(() => timer.Restore(TimeSpan.Zero, TimeSpan.Zero, false, false), "Zero restored duration");
    Throws<ArgumentOutOfRangeException>(() => timer.Restore(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6), false, false), "Remaining beyond duration");
    Throws<ArgumentException>(() => timer.Restore(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2), true, true), "Running and paused together");
    Throws<ArgumentException>(() => timer.Restore(TimeSpan.FromMinutes(5), TimeSpan.Zero, true, false), "Running at zero");
}

static void TestInactiveActions()
{
    var timer = new FocusTimer();
    timer.Pause();
    timer.Resume();
    timer.Tick();
    False(timer.IsRunning, "Inactive actions do not start timer");
    False(timer.IsPaused, "Inactive actions do not pause timer");
    Equal(TimeSpan.FromMinutes(25), timer.Remaining, "Inactive actions preserve duration");
}

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
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
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
    public DateTimeOffset GetNow() => now;

    public void Advance(TimeSpan amount) => now = now.Add(amount);
}
