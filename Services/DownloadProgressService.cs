using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace VajraPulse.Services;

public enum DownloadState
{
    Active,
    Stalled,
    Completed,
    Failed
}

public sealed record DownloadActivity(
    string Id,
    string FileName,
    string TempPath,
    string? FinalPath,
    long BytesReceived,
    long? TotalBytes,
    DownloadState State,
    DateTimeOffset StartedAt,
    DateTimeOffset LastProgressAt,
    DateTimeOffset StateChangedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? FailedAt);

public sealed record DownloadRevealTarget(string DirectoryPath, string? ItemPath);

/// <summary>
/// Observes temporary download files in one local Downloads directory. The service
/// never opens or changes a download and does not invent a total byte count.
/// </summary>
public sealed class DownloadProgressService : IDisposable
{
    public const int Capacity = 25;
    public const int MaximumPathLength = 4096;

    private const int MaximumFilesPerReconciliation = 2048;
    private const int MaximumWatcherEventsPerSecond = 256;
    private static readonly TimeSpan DefaultReconciliationInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultStalledAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultVanishGrace = TimeSpan.FromSeconds(5);
    private static readonly string[] TemporarySuffixes =
    {
        ".crdownload", ".partial", ".download", ".part", ".tmp"
    };

    private readonly object _gate = new();
    private readonly string _downloadsDirectory;
    private readonly Func<DateTimeOffset> _getNow;
    private readonly TimeSpan _stalledAfter;
    private readonly TimeSpan _vanishGrace;
    private readonly DateTimeOffset _serviceStartedAt;
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, Entry> _activeByTempPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _reconciliationTimer;
    private readonly FileSystemWatcher? _watcher;
    private long _watcherEventWindowStarted = Environment.TickCount64;
    private int _watcherEventsInWindow;
    private bool _disposed;

    public DownloadProgressService()
        : this(GetDefaultDownloadsDirectory())
    {
    }

    public DownloadProgressService(string downloadsDirectory)
        : this(
            downloadsDirectory,
            static () => DateTimeOffset.UtcNow,
            DefaultReconciliationInterval,
            DefaultStalledAfter,
            DefaultVanishGrace,
            enableWatcher: true)
    {
    }

    internal DownloadProgressService(
        string downloadsDirectory,
        Func<DateTimeOffset> getNow,
        TimeSpan reconciliationInterval,
        TimeSpan stalledAfter,
        TimeSpan vanishGrace,
        bool enableWatcher)
    {
        _downloadsDirectory = NormalizeLocalDirectory(downloadsDirectory);
        _getNow = getNow ?? throw new ArgumentNullException(nameof(getNow));
        ValidateDurations(reconciliationInterval, stalledAfter, vanishGrace);
        _stalledAfter = stalledAfter;
        _vanishGrace = vanishGrace;
        _serviceStartedAt = _getNow();

        if (enableWatcher && Directory.Exists(_downloadsDirectory))
        {
            try
            {
                _watcher = new FileSystemWatcher(_downloadsDirectory)
                {
                    IncludeSubdirectories = false,
                    Filter = "*",
                    InternalBufferSize = 16 * 1024,
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.Size |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.CreationTime
                };
                _watcher.Created += OnCreatedOrChanged;
                _watcher.Changed += OnCreatedOrChanged;
                _watcher.Deleted += OnDeleted;
                _watcher.Renamed += OnRenamed;
                _watcher.Error += OnWatcherError;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception exception) when (IsExpectedFileSystemException(exception))
            {
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        Refresh();
        _reconciliationTimer = new Timer(
            static state => ((DownloadProgressService)state!).RefreshFromTimer(),
            this,
            reconciliationInterval,
            reconciliationInterval);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<DownloadActivity> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _entries
                    .OrderByDescending(entry => entry.StartedAt)
                    .Select(entry => entry.ToActivity())
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Performs an immediate read-only reconciliation. The periodic timer and file
    /// watcher call the same state machine.
    /// </summary>
    public void Refresh()
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            changed = ReconcileCore(_getNow());
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>
    /// Returns only local path data for a UI reveal action. It never launches a shell.
    /// </summary>
    public DownloadRevealTarget? RevealTarget(string activityId)
    {
        if (string.IsNullOrWhiteSpace(activityId))
        {
            return null;
        }

        lock (_gate)
        {
            Entry? entry = _entries.FirstOrDefault(
                candidate => string.Equals(candidate.Id, activityId, StringComparison.Ordinal));
            if (entry == null)
            {
                return null;
            }

            string? itemPath = null;
            if (entry.FinalPath != null && IsSafeDirectChild(entry.FinalPath) && File.Exists(entry.FinalPath))
            {
                itemPath = entry.FinalPath;
            }
            else if (IsSafeDirectChild(entry.TempPath) && File.Exists(entry.TempPath))
            {
                itemPath = entry.TempPath;
            }

            return new DownloadRevealTarget(_downloadsDirectory, itemPath);
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watcher = _watcher;
            if (watcher != null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnCreatedOrChanged;
                watcher.Changed -= OnCreatedOrChanged;
                watcher.Deleted -= OnDeleted;
                watcher.Renamed -= OnRenamed;
                watcher.Error -= OnWatcherError;
            }
        }

        _reconciliationTimer.Dispose();
        watcher?.Dispose();
    }

    private bool ReconcileCore(DateTimeOffset now)
    {
        bool changed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool completeScan = Directory.Exists(_downloadsDirectory);

        if (completeScan)
        {
            int scanned = 0;
            try
            {
                foreach (string candidate in Directory.EnumerateFiles(
                             _downloadsDirectory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (++scanned > MaximumFilesPerReconciliation)
                    {
                        completeScan = false;
                        break;
                    }

                    string? path = NormalizeDirectChild(candidate);
                    if (path == null || !TryGetTemporarySuffix(path, out _))
                    {
                        continue;
                    }

                    seen.Add(path);
                    changed |= ObserveTemporaryFile(path, now, permitNewTmp: false);
                }
            }
            catch (Exception exception) when (IsExpectedFileSystemException(exception))
            {
                completeScan = false;
            }
        }

        if (completeScan)
        {
            foreach (Entry entry in _activeByTempPath.Values.ToArray())
            {
                if (!seen.Contains(entry.TempPath))
                {
                    changed |= ObserveMissing(entry, now);
                }
            }
        }

        return changed;
    }

    private bool ObserveTemporaryFile(string path, DateTimeOffset now, bool permitNewTmp)
    {
        if (!_activeByTempPath.TryGetValue(path, out Entry? entry))
        {
            if (!TryReadFile(path, out FileObservation observation) ||
                !TryGetTemporarySuffix(path, out string suffix))
            {
                return false;
            }

            if (suffix.Equals(".tmp", StringComparison.OrdinalIgnoreCase) &&
                !permitNewTmp &&
                observation.CreatedAt < _serviceStartedAt)
            {
                return false;
            }

            bool pruned = MakeRoomForEntry();
            if (_entries.Count >= Capacity)
            {
                return pruned;
            }

            string expectedFinalPath = path[..^suffix.Length];
            FinalFingerprint initialFinal = ReadFinalFingerprint(expectedFinalPath);
            entry = new Entry(
                Guid.NewGuid().ToString("N"),
                GetDisplayFileName(path, suffix),
                path,
                observation.Length,
                now,
                expectedFinalPath,
                initialFinal);
            _entries.Add(entry);
            _activeByTempPath.Add(path, entry);
            return true;
        }

        if (!TryReadFile(path, out FileObservation current))
        {
            return ObserveMissing(entry, now);
        }

        bool changed = false;
        if (entry.MissingSince != null)
        {
            entry.MissingSince = null;
        }

        if (entry.BytesReceived != current.Length)
        {
            entry.BytesReceived = current.Length;
            entry.LastProgressAt = now;
            changed = true;
            changed |= SetState(entry, DownloadState.Active, now);
        }
        else
        {
            DownloadState state = now - entry.LastProgressAt >= _stalledAfter
                ? DownloadState.Stalled
                : DownloadState.Active;
            changed |= SetState(entry, state, now);
        }

        return changed;
    }

    private bool ObserveMissing(Entry entry, DateTimeOffset now)
    {
        if (!_activeByTempPath.ContainsKey(entry.TempPath))
        {
            return false;
        }

        if (TryResolveExpectedFinal(entry, out string? finalPath, out long finalLength))
        {
            Complete(entry, finalPath, finalLength, now);
            return true;
        }

        entry.MissingSince ??= now;
        if (now - entry.MissingSince.Value < _vanishGrace)
        {
            return false;
        }

        _activeByTempPath.Remove(entry.TempPath);
        entry.FailedAt = now;
        SetState(entry, DownloadState.Failed, now);
        return true;
    }

    private bool TryResolveExpectedFinal(Entry entry, out string finalPath, out long finalLength)
    {
        finalPath = entry.ExpectedFinalPath;
        finalLength = 0;
        if (!IsSafeDirectChild(finalPath) ||
            TryGetTemporarySuffix(finalPath, out _) ||
            !TryReadFile(finalPath, out FileObservation observation) ||
            observation.Length < entry.BytesReceived)
        {
            return false;
        }

        bool appearedOrChanged = !entry.InitialFinal.Exists ||
                                 observation.Length != entry.InitialFinal.Length ||
                                 observation.LastWriteAt > entry.InitialFinal.LastWriteAt;
        if (!appearedOrChanged)
        {
            return false;
        }

        finalLength = observation.Length;
        return true;
    }

    private void Complete(Entry entry, string finalPath, long finalLength, DateTimeOffset now)
    {
        _activeByTempPath.Remove(entry.TempPath);
        entry.FinalPath = finalPath;
        entry.FileName = Path.GetFileName(finalPath);
        entry.BytesReceived = finalLength;
        entry.LastProgressAt = now;
        entry.CompletedAt = now;
        entry.MissingSince = null;
        SetState(entry, DownloadState.Completed, now);
    }

    private bool MoveActiveEntry(Entry entry, string newTempPath, DateTimeOffset now)
    {
        if (!TryGetTemporarySuffix(newTempPath, out string suffix))
        {
            return false;
        }

        _activeByTempPath.Remove(entry.TempPath);
        entry.TempPath = newTempPath;
        entry.FileName = GetDisplayFileName(newTempPath, suffix);
        entry.ExpectedFinalPath = newTempPath[..^suffix.Length];
        entry.InitialFinal = ReadFinalFingerprint(entry.ExpectedFinalPath);
        entry.MissingSince = null;
        _activeByTempPath[newTempPath] = entry;
        ObserveTemporaryFile(newTempPath, now, permitNewTmp: true);
        return true;
    }

    private bool CompleteFromRename(Entry entry, string newPath, DateTimeOffset now)
    {
        if (!IsSafeDirectChild(newPath) ||
            TryGetTemporarySuffix(newPath, out _) ||
            !TryReadFile(newPath, out FileObservation final))
        {
            return false;
        }

        Complete(entry, newPath, final.Length, now);
        return true;
    }

    private bool MakeRoomForEntry()
    {
        if (_entries.Count < Capacity)
        {
            return false;
        }

        Entry? oldestTerminal = _entries
            .Where(entry => entry.State is DownloadState.Completed or DownloadState.Failed)
            .OrderBy(entry => entry.CompletedAt ?? entry.FailedAt ?? entry.StateChangedAt)
            .FirstOrDefault();
        if (oldestTerminal == null)
        {
            return false;
        }

        _entries.Remove(oldestTerminal);
        return true;
    }

    private static bool SetState(Entry entry, DownloadState state, DateTimeOffset now)
    {
        if (entry.State == state)
        {
            return false;
        }

        entry.State = state;
        entry.StateChangedAt = now;
        return true;
    }

    private bool AcceptWatcherEvent()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            long now = Environment.TickCount64;
            if (now - _watcherEventWindowStarted >= 1000)
            {
                _watcherEventWindowStarted = now;
                _watcherEventsInWindow = 0;
            }

            if (_watcherEventsInWindow >= MaximumWatcherEventsPerSecond)
            {
                return false;
            }

            _watcherEventsInWindow++;
            return true;
        }
    }

    private void OnCreatedOrChanged(object sender, FileSystemEventArgs eventArgs)
    {
        if (!AcceptWatcherEvent())
        {
            return;
        }

        string? path = NormalizeDirectChild(eventArgs.FullPath);
        if (path == null || !TryGetTemporarySuffix(path, out _))
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            changed = ObserveTemporaryFile(
                path,
                _getNow(),
                permitNewTmp: eventArgs.ChangeType == WatcherChangeTypes.Created);
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs eventArgs)
    {
        if (!AcceptWatcherEvent())
        {
            return;
        }

        string? path = NormalizeDirectChild(eventArgs.FullPath);
        if (path == null)
        {
            return;
        }

        bool changed = false;
        lock (_gate)
        {
            if (!_disposed && _activeByTempPath.TryGetValue(path, out Entry? entry))
            {
                changed = ObserveMissing(entry, _getNow());
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs eventArgs)
    {
        if (!AcceptWatcherEvent())
        {
            return;
        }

        string? oldPath = NormalizeDirectChild(eventArgs.OldFullPath);
        string? newPath = NormalizeDirectChild(eventArgs.FullPath);
        if (oldPath == null && newPath == null)
        {
            return;
        }

        bool changed = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            DateTimeOffset now = _getNow();
            if (oldPath != null && _activeByTempPath.TryGetValue(oldPath, out Entry? entry))
            {
                if (newPath != null && TryGetTemporarySuffix(newPath, out _))
                {
                    changed = MoveActiveEntry(entry, newPath, now);
                }
                else if (newPath != null && CompleteFromRename(entry, newPath, now))
                {
                    changed = true;
                }
                else
                {
                    changed = ObserveMissing(entry, now);
                }
            }
            else if (newPath != null && TryGetTemporarySuffix(newPath, out _))
            {
                changed = ObserveTemporaryFile(newPath, now, permitNewTmp: true);
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs)
    {
        if (!AcceptWatcherEvent())
        {
            return;
        }

        // FileSystemWatcher can lose events when its finite native buffer overflows.
        // A bounded top-level reconciliation restores state without trusting the event stream.
        Refresh();
    }

    private void RefreshFromTimer()
    {
        try
        {
            Refresh();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RaiseChanged()
    {
        EventHandler? handlers = Changed;
        if (handlers == null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // A subscriber must not break the watcher or reconciliation timer.
            }
        }
    }

    private string? NormalizeDirectChild(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumPathLength)
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(value);
            return IsSafeDirectChild(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private bool IsSafeDirectChild(string path)
    {
        if (path.Length > MaximumPathLength || IsUnc(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(path);
        return string.Equals(parent, _downloadsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLocalDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A Downloads directory is required.", nameof(value));
        }

        if (value.Length > MaximumPathLength)
        {
            throw new PathTooLongException("The Downloads directory path is too long.");
        }

        string fullPath = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(fullPath) || IsUnc(fullPath))
        {
            throw new ArgumentException("Only a fully qualified local directory is supported.", nameof(value));
        }

        return fullPath;
    }

    private static string GetDefaultDownloadsDirectory()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads");
    }

    private static bool TryGetTemporarySuffix(string path, out string suffix)
    {
        foreach (string candidate in TemporarySuffixes)
        {
            if (path.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                suffix = candidate;
                return true;
            }
        }

        suffix = string.Empty;
        return false;
    }

    private static string GetDisplayFileName(string tempPath, string suffix)
    {
        string name = Path.GetFileName(tempPath);
        return name.Length > suffix.Length ? name[..^suffix.Length] : name;
    }

    private static bool TryReadFile(string path, out FileObservation observation)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                observation = default;
                return false;
            }

            observation = new FileObservation(
                info.Length,
                new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            return true;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            observation = default;
            return false;
        }
    }

    private static FinalFingerprint ReadFinalFingerprint(string path)
    {
        return TryReadFile(path, out FileObservation observation)
            ? new FinalFingerprint(true, observation.Length, observation.LastWriteAt)
            : new FinalFingerprint(false, 0, DateTimeOffset.MinValue);
    }

    private static bool IsUnc(string path)
    {
        return path.StartsWith("\\\\", StringComparison.Ordinal) ||
               path.StartsWith("//", StringComparison.Ordinal);
    }

    private static bool IsExpectedFileSystemException(Exception exception)
    {
        return exception is IOException or
               UnauthorizedAccessException or
               ArgumentException or
               NotSupportedException;
    }

    private static void ValidateDurations(
        TimeSpan reconciliationInterval,
        TimeSpan stalledAfter,
        TimeSpan vanishGrace)
    {
        if (reconciliationInterval != Timeout.InfiniteTimeSpan && reconciliationInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reconciliationInterval));
        }

        if (stalledAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stalledAfter));
        }

        if (vanishGrace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(vanishGrace));
        }
    }

    private readonly record struct FileObservation(
        long Length,
        DateTimeOffset CreatedAt,
        DateTimeOffset LastWriteAt);

    private readonly record struct FinalFingerprint(
        bool Exists,
        long Length,
        DateTimeOffset LastWriteAt);

    private sealed class Entry
    {
        public Entry(
            string id,
            string fileName,
            string tempPath,
            long bytesReceived,
            DateTimeOffset now,
            string expectedFinalPath,
            FinalFingerprint initialFinal)
        {
            Id = id;
            FileName = fileName;
            TempPath = tempPath;
            BytesReceived = bytesReceived;
            StartedAt = now;
            LastProgressAt = now;
            StateChangedAt = now;
            ExpectedFinalPath = expectedFinalPath;
            InitialFinal = initialFinal;
        }

        public string Id { get; }
        public string FileName { get; set; }
        public string TempPath { get; set; }
        public string? FinalPath { get; set; }
        public long BytesReceived { get; set; }
        public DownloadState State { get; set; } = DownloadState.Active;
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset LastProgressAt { get; set; }
        public DateTimeOffset StateChangedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public DateTimeOffset? FailedAt { get; set; }
        public DateTimeOffset? MissingSince { get; set; }
        public string ExpectedFinalPath { get; set; }
        public FinalFingerprint InitialFinal { get; set; }

        public DownloadActivity ToActivity()
        {
            return new DownloadActivity(
                Id,
                FileName,
                TempPath,
                FinalPath,
                BytesReceived,
                TotalBytes: null,
                State,
                StartedAt,
                LastProgressAt,
                StateChangedAt,
                CompletedAt,
                FailedAt);
        }
    }
}
