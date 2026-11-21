using System;
using System.Collections.Generic;

namespace VajraPulse.Core;

public enum PulseActivityKind
{
    Privacy,
    Clipboard,
    Download,
    Bluetooth,
    Capture,
    Note
}

public sealed class PulseActivity
{
    public const int MaximumIdLength = 128;
    public const int MaximumTitleLength = 80;
    public const int MaximumDetailLength = 240;

    public const int PrivacyPriority = 400;
    public const int UserInvokedPriority = 300;
    public const int TransientStatusPriority = 200;
    public const int ClipboardPriority = 100;

    public PulseActivity(
        string id,
        PulseActivityKind kind,
        string title,
        string detail,
        int priority,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt = null,
        bool isPersistent = false,
        double? progress = null,
        bool isIndeterminate = false)
    {
        Id = ValidateRequiredText(id, MaximumIdLength, nameof(id));

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown activity kind.");
        }

        Kind = kind;
        Title = ValidateRequiredText(title, MaximumTitleLength, nameof(title));
        Detail = ValidateText(detail, MaximumDetailLength, nameof(detail));

        if (progress is < 0 or > 1 || (progress.HasValue && !double.IsFinite(progress.Value)))
        {
            throw new ArgumentOutOfRangeException(nameof(progress), progress, "Progress must be a finite value from 0 through 1.");
        }

        if (isIndeterminate && progress.HasValue)
        {
            throw new ArgumentException("Indeterminate activities cannot also specify progress.", nameof(isIndeterminate));
        }

        Priority = priority;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        IsPersistent = isPersistent;
        Progress = progress;
        IsIndeterminate = isIndeterminate;
    }

    public PulseActivity(
        string id,
        PulseActivityKind kind,
        string title,
        string detail,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt = null,
        bool isPersistent = false,
        double? progress = null,
        bool isIndeterminate = false)
        : this(
            id,
            kind,
            title,
            detail,
            GetDefaultPriority(kind),
            createdAt,
            expiresAt,
            isPersistent,
            progress,
            isIndeterminate)
    {
    }

    public string Id { get; }

    public PulseActivityKind Kind { get; }

    public string Title { get; }

    public string Detail { get; }

    public int Priority { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public bool IsPersistent { get; }

    public double? Progress { get; }

    public bool IsIndeterminate { get; }

    public static int GetDefaultPriority(PulseActivityKind kind) => kind switch
    {
        PulseActivityKind.Privacy => PrivacyPriority,
        PulseActivityKind.Capture or PulseActivityKind.Note => UserInvokedPriority,
        PulseActivityKind.Download or PulseActivityKind.Bluetooth => TransientStatusPriority,
        PulseActivityKind.Clipboard => ClipboardPriority,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown activity kind.")
    };

    private static string ValidateRequiredText(string value, int maximumLength, string parameterName)
    {
        ValidateText(value, maximumLength, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        return value;
    }

    private static string ValidateText(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"Value cannot exceed {maximumLength} characters.", parameterName);
        }

        return value;
    }
}

public sealed class ActivityCoordinator
{
    public const int MaximumActivities = 30;

    private readonly object _gate = new();
    private readonly Dictionary<string, PulseActivity> _activities = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public ActivityCoordinator(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _activities.Count;
            }
        }
    }

    public PulseActivity? Current
    {
        get
        {
            lock (_gate)
            {
                PulseActivity? current = null;
                foreach (var activity in _activities.Values)
                {
                    if (current is null || CompareRank(activity, current) < 0)
                    {
                        current = activity;
                    }
                }

                return current;
            }
        }
    }

    public void Upsert(PulseActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        bool changed;
        lock (_gate)
        {
            if (IsExpired(activity, _clock()))
            {
                changed = _activities.Remove(activity.Id);
            }
            else if (_activities.ContainsKey(activity.Id))
            {
                _activities[activity.Id] = activity;
                changed = true;
            }
            else if (_activities.Count < MaximumActivities)
            {
                _activities.Add(activity.Id, activity);
                changed = true;
            }
            else
            {
                var lowestRanked = FindLowestRanked();
                if (CompareRank(activity, lowestRanked) < 0)
                {
                    _activities.Remove(lowestRanked.Id);
                    _activities.Add(activity.Id, activity);
                    changed = true;
                }
                else
                {
                    changed = false;
                }
            }
        }

        RaiseChanged(changed);
    }

    public bool Remove(string id)
    {
        ValidateId(id);

        bool changed;
        lock (_gate)
        {
            changed = _activities.Remove(id);
        }

        RaiseChanged(changed);
        return changed;
    }

    public int ClearKind(PulseActivityKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown activity kind.");
        }

        int removed;
        lock (_gate)
        {
            var ids = new List<string>();
            foreach (var pair in _activities)
            {
                if (pair.Value.Kind == kind)
                {
                    ids.Add(pair.Key);
                }
            }

            foreach (var id in ids)
            {
                _activities.Remove(id);
            }

            removed = ids.Count;
        }

        RaiseChanged(removed > 0);
        return removed;
    }

    public int Tick()
    {
        var now = _clock();
        int removed;

        lock (_gate)
        {
            var expiredIds = new List<string>();
            foreach (var pair in _activities)
            {
                if (IsExpired(pair.Value, now))
                {
                    expiredIds.Add(pair.Key);
                }
            }

            foreach (var id in expiredIds)
            {
                _activities.Remove(id);
            }

            removed = expiredIds.Count;
        }

        RaiseChanged(removed > 0);
        return removed;
    }

    public IReadOnlyList<PulseActivity> Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new List<PulseActivity>(_activities.Values);
            snapshot.Sort(CompareRank);
            return snapshot.ToArray();
        }
    }

    private static bool IsExpired(PulseActivity activity, DateTimeOffset now) =>
        !activity.IsPersistent && activity.ExpiresAt is { } expiresAt && expiresAt <= now;

    private PulseActivity FindLowestRanked()
    {
        PulseActivity? lowest = null;
        foreach (var activity in _activities.Values)
        {
            if (lowest is null || CompareRank(activity, lowest) > 0)
            {
                lowest = activity;
            }
        }

        return lowest!;
    }

    private static int CompareRank(PulseActivity left, PulseActivity right)
    {
        var priority = right.Priority.CompareTo(left.Priority);
        if (priority != 0)
        {
            return priority;
        }

        var recency = right.CreatedAt.CompareTo(left.CreatedAt);
        if (recency != 0)
        {
            return recency;
        }

        return StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    private static void ValidateId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (string.IsNullOrWhiteSpace(id) || id.Length > PulseActivity.MaximumIdLength)
        {
            throw new ArgumentException($"Id must contain text and cannot exceed {PulseActivity.MaximumIdLength} characters.", nameof(id));
        }
    }

    private void RaiseChanged(bool changed)
    {
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
