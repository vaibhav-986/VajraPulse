using System;

namespace VajraPulse.Core;

public sealed class FocusTimer
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(24);

    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _deadline;
    private TimeSpan _remaining;
    private bool _completionRaised;

    public FocusTimer(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Duration = DefaultDuration;
        _remaining = Duration;
    }

    public TimeSpan Duration { get; private set; }

    public TimeSpan Remaining
    {
        get
        {
            if (!IsRunning)
            {
                return _remaining;
            }

            var remaining = _deadline - _clock();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public bool IsRunning { get; private set; }

    public bool IsPaused { get; private set; }

    public event EventHandler? Completed;

    public void Start(TimeSpan duration)
    {
        ValidateDuration(duration);

        Duration = duration;
        _remaining = duration;
        _deadline = _clock().Add(duration);
        IsRunning = true;
        IsPaused = false;
        _completionRaised = false;
    }

    public void Pause()
    {
        if (!IsRunning)
        {
            return;
        }

        var remaining = Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            Tick();
            return;
        }

        _remaining = remaining;
        IsRunning = false;
        IsPaused = true;
    }

    public void Resume()
    {
        if (!IsPaused || _remaining <= TimeSpan.Zero)
        {
            return;
        }

        _deadline = _clock().Add(_remaining);
        IsRunning = true;
        IsPaused = false;
    }

    public void Reset()
    {
        _remaining = Duration;
        IsRunning = false;
        IsPaused = false;
        _completionRaised = false;
    }

    public void Restore(TimeSpan duration, TimeSpan remaining, bool isRunning, bool isPaused)
    {
        ValidateDuration(duration);
        if (remaining < TimeSpan.Zero || remaining > duration)
        {
            throw new ArgumentOutOfRangeException(nameof(remaining), remaining, "Remaining time must be from zero through the session duration.");
        }
        if (isRunning && isPaused)
        {
            throw new ArgumentException("A focus timer cannot be running and paused at the same time.");
        }
        if ((isRunning || isPaused) && remaining <= TimeSpan.Zero)
        {
            throw new ArgumentException("An active focus timer must have time remaining.");
        }

        Duration = duration;
        _remaining = remaining;
        IsRunning = isRunning;
        IsPaused = isPaused;
        _deadline = isRunning ? _clock().Add(remaining) : default;
        _completionRaised = remaining <= TimeSpan.Zero;
    }

    public void Tick()
    {
        if (!IsRunning || Remaining > TimeSpan.Zero)
        {
            return;
        }

        _remaining = TimeSpan.Zero;
        IsRunning = false;
        IsPaused = false;

        if (_completionRaised)
        {
            return;
        }

        _completionRaised = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private static void ValidateDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "Duration must be greater than zero and no more than 24 hours.");
        }
    }
}
