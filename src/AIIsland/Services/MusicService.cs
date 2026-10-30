using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace AIIsland.Services;

public sealed record MusicState(
    string Title,
    string Artist,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    ImageSource? Cover);

/// <summary>Uses the Windows system media transport controls (no player APIs).</summary>
public sealed class MusicService
{
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? session;
    private string lastKey = "";
    private ImageSource? lastCover;

    public bool Available => manager is not null;

    public async Task StartAsync()
    {
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch { manager = null; }
    }

    public async Task<MusicState?> SnapshotAsync()
    {
        if (manager is null) return null;
        try
        {
            var sessions = manager.GetSessions();
            var current = sessions.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                          ?? sessions.FirstOrDefault();
            if (current is null) { session = null; return null; }
            session = current;

            var playback = current.GetPlaybackInfo();
            var timeline = current.GetTimelineProperties();
            var media = await current.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            var key = media.Title + "|" + media.Artist;
            if (key != lastKey)
            {
                lastKey = key;
                lastCover = await LoadCoverAsync(media);
            }

            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;
            if (duration <= TimeSpan.Zero) duration = TimeSpan.Zero;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > duration) position = duration;

            return new MusicState(
                string.IsNullOrWhiteSpace(media.Title) ? "未知歌曲" : media.Title,
                media.Artist ?? "",
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                position,
                duration,
                lastCover);
        }
        catch { return null; }
    }

    private static async Task<ImageSource?> LoadCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties media)
    {
        if (media.Thumbnail is null) return null;
        try
        {
            using var thumbnail = await media.Thumbnail.OpenReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            if (thumbnail.Size > 8 * 1024 * 1024) return null;
            using var stream = thumbnail.AsStreamForRead();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            if (buffer.Length > 8 * 1024 * 1024) return null;
            buffer.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.StreamSource = buffer;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    public async Task ToggleAsync() { if (session is not null) await session.TryTogglePlayPauseAsync().AsTask(); }
    public async Task NextAsync() { if (session is not null) await session.TrySkipNextAsync().AsTask(); }
    public async Task PreviousAsync() { if (session is not null) await session.TrySkipPreviousAsync().AsTask(); }
    public async Task SeekAsync(double fraction)
    {
        if (session is null) return;
        try
        {
            var timeline = session.GetTimelineProperties();
            var start = timeline.StartTime;
            var duration = timeline.EndTime - start;
            if (duration <= TimeSpan.Zero) return;
            var target = start + duration * Math.Clamp(fraction, 0, 1);
            await session.TryChangePlaybackPositionAsync(target.Ticks).AsTask();
        }
        catch { }
    }
}
