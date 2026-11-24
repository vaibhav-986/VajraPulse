using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace VajraPulse.Services;

public record MediaSnapshot(bool HasSession, string Title, string Artist, bool IsPlaying, bool CanToggle, bool CanPrevious, bool CanNext)
{
    public bool CanSeek { get; init; }
    public TimeSpan TimelineStart { get; init; }
    public BitmapSource? Artwork { get; init; }
    public TimeSpan Duration { get; init; }
    public TimeSpan Position { get; init; }
    public DateTimeOffset PositionUpdatedAt { get; init; }
    public double PlaybackRate { get; init; } = 1;
    public TimeSpan CurrentPosition
    {
        get
        {
            double elapsed = IsPlaying && PositionUpdatedAt != default
                ? Math.Max(0, (DateTimeOffset.UtcNow - PositionUpdatedAt).TotalSeconds) * PlaybackRate : 0;
            double seconds = Math.Max(0, Position.TotalSeconds + elapsed);
            if (Duration > TimeSpan.Zero) seconds = Math.Min(seconds, Duration.TotalSeconds);
            return TimeSpan.FromSeconds(seconds);
        }
    }
    public static MediaSnapshot Empty { get; } = new(false, "Nothing playing", "Start music in your app or browser", false, false, false, false);
}

public sealed class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _artworkKey;
    private BitmapSource? _artwork;
    private bool _disposed;
    public MediaSnapshot Snapshot { get; private set; } = MediaSnapshot.Empty;

    public async Task RefreshAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0)) return;
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;
            _session = _manager.GetCurrentSession();
            if (_session == null)
            {
                Snapshot = MediaSnapshot.Empty;
                _artworkKey = null;
                _artwork = null;
                return;
            }
            var media = await _session.TryGetMediaPropertiesAsync();
            if (_disposed) return;
            string key = _session.SourceAppUserModelId + "\n" + media.Title + "\n" + media.Artist;
            if (key != _artworkKey)
            {
                _artworkKey = key;
                _artwork = await ReadArtwork(media.Thumbnail);
            }
            if (_disposed) return;
            var playback = _session.GetPlaybackInfo();
            var controls = playback.Controls;
            var timeline = _session.GetTimelineProperties();
            Snapshot = new(true, string.IsNullOrWhiteSpace(media.Title) ? "Media session" : media.Title,
                string.IsNullOrWhiteSpace(media.Artist) ? "Windows media" : media.Artist,
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                controls.IsPlayPauseToggleEnabled, controls.IsPreviousEnabled, controls.IsNextEnabled)
            {
                Artwork = _artwork,
                CanSeek = controls.IsPlaybackPositionEnabled && timeline.EndTime > timeline.StartTime,
                TimelineStart = timeline.StartTime,
                Duration = timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero,
                Position = timeline.Position >= timeline.StartTime ? timeline.Position - timeline.StartTime : TimeSpan.Zero,
                PositionUpdatedAt = timeline.LastUpdatedTime,
                PlaybackRate = playback.PlaybackRate ?? 1
            };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            _session = null;
            _manager = null;
            _artworkKey = null;
            _artwork = null;
            Snapshot = MediaSnapshot.Empty;
        }
        finally { _gate.Release(); }
    }

    private static async Task<BitmapSource?> ReadArtwork(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail == null) return null;
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 4 * 1024 * 1024) return null;
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            uint size = (uint)stream.Size;
            if (await reader.LoadAsync(size) != size) return null;
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            using var memory = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.DecodePixelHeight = 128;
            image.StreamSource = memory;
            image.EndInit();
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0 || (long)image.PixelWidth * image.PixelHeight > 16_384) return null;
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FormatException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        { return null; }
    }

    public async Task<bool> ControlAsync(string command)
    {
        var session = _session;
        if (_disposed || session == null) return false;
        try
        {
            return command switch
            {
                "toggle" => await session.TryTogglePlayPauseAsync(),
                "previous" => await session.TrySkipPreviousAsync(),
                "next" => await session.TrySkipNextAsync(),
                _ => false
            };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        { return false; }
    }

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        var session = _session;
        var snapshot = Snapshot;
        if (_disposed || session == null || !snapshot.CanSeek || snapshot.Duration <= TimeSpan.Zero) return false;
        TimeSpan target = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, snapshot.Duration.TotalSeconds));
        try
        {
            bool changed = await session.TryChangePlaybackPositionAsync(checked(snapshot.TimelineStart.Ticks + target.Ticks));
            if (changed && ReferenceEquals(session, _session))
                Snapshot = snapshot with { Position = target, PositionUpdatedAt = DateTimeOffset.UtcNow };
            return changed;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        { return false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _session = null;
        _manager = null;
        _artwork = null;
    }
}
