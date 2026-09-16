using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace AIIsland.Modules;
public abstract class MusicModule : ModuleBase
{
    private readonly string processName;
    public string DisplayName { get; }
    private readonly string id;
    private readonly MusicPlaybackOrder playbackOrder = new();
    public long LastStarted => playbackOrder.LastStarted;
    private ImageSource? cover;
    public ImageSource? Cover { get => cover; private set => Set(ref cover, value); }
    private double positionSeconds, durationSeconds;
    public double PositionSeconds { get => positionSeconds; private set { if (Set(ref positionSeconds, value)) Changed(nameof(PositionLabel)); } }
    public double DurationSeconds { get => durationSeconds; private set { if (Set(ref durationSeconds, value)) { Changed(nameof(DurationLabel)); Changed(nameof(PositionLabel)); Changed(nameof(HasTimeline)); } } }
    public bool HasTimeline => DurationSeconds > 0;
    public string PositionLabel => HasTimeline ? MusicProgress.Format(PositionSeconds) : "--:--";
    public string DurationLabel => HasTimeline ? MusicProgress.Format(DurationSeconds) : "--:--";
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? session;
    private AppProcess? app;
    private int dirty = 1;
    private bool enabled = true, playing, canPrevious, canNext, canToggle;
    private string title = "暂无可控制的播放会话", artist = "";
    public override string Id => id;
    public string Title { get => title; private set => Set(ref title, value); }
    public string Artist { get => artist; private set => Set(ref artist, value); }
    public bool IsPlaying { get => playing; private set { if (Set(ref playing, value)) Changed(nameof(PlayLabel)); } }
    public string PlayLabel => IsPlaying ? "暂停" : "播放";
    public ICommand Previous { get; }
    public ICommand Toggle { get; }
    public ICommand Next { get; }
    public ICommand Open { get; }
    protected MusicModule(string id, string processName, string displayName)
    {
        this.id = id; this.processName = processName; DisplayName = displayName;
        Previous = Action(() => Control(s => s.TrySkipPreviousAsync().AsTask()), () => canPrevious);
        Next = Action(() => Control(s => s.TrySkipNextAsync().AsTask()), () => canNext);
        Toggle = Action(async () => {
            var starting = !IsPlaying;
            await Control(s => s.TryTogglePlayPauseAsync().AsTask());
            if (starting) { playbackOrder.Played(); Changed(nameof(LastStarted)); }
        }, () => canToggle);
        Open = Action(() => ProcessDiscovery.OpenAsync(app), () => app?.Path != null);
    }
    public void Configure(bool value) { enabled = value; if (!value) { IsVisible = false; SelectSession(null); ResetMedia(); } }
    public override async Task StartAsync(CancellationToken token)
    {
        try {
            var created = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), token);
            if (Disposed) return;
            manager = created; manager.SessionsChanged += SessionsChanged;
        }
        catch (Exception e) when (e is not OperationCanceledException) { Note = "媒体控制不可用：" + e.Message; }
    }
    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Interlocked.Exchange(ref dirty, 1);
    private void MediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Interlocked.Exchange(ref dirty, 1);
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, session)) return;
        try { playbackOrder.Observe(sender.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing); }
        catch (Exception) { }
    }
    private void SelectSession(GlobalSystemMediaTransportControlsSession? value)
    {
        if (ReferenceEquals(value, session)) return;
        if (session != null) { session.MediaPropertiesChanged -= MediaChanged; session.PlaybackInfoChanged -= PlaybackChanged; }
        session = value;
        if (session != null) { session.MediaPropertiesChanged += MediaChanged; session.PlaybackInfoChanged += PlaybackChanged; }
        Interlocked.Exchange(ref dirty, 1);
    }
    public override async Task RefreshAsync(ProcessSnapshot processes, CancellationToken token)
    {
        app = processes.Find(processName); IsVisible = enabled && app != null;
        if (!IsVisible) { SelectSession(null); ResetMedia(); return; }
        try
        {
            var sessions = manager?.GetSessions().Where(s => s.SourceAppUserModelId.Contains(processName, StringComparison.OrdinalIgnoreCase)).ToArray();
            var selected = sessions?.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) ?? sessions?.FirstOrDefault();
            SelectSession(selected);
            canPrevious = canNext = canToggle = false;
            if (session == null) { ResetMedia(); }
            else
            {
                var current = session;
                var playback = current.GetPlaybackInfo();
                canPrevious = playback.Controls.IsPreviousEnabled; canNext = playback.Controls.IsNextEnabled; canToggle = playback.Controls.IsPlayPauseToggleEnabled;
                IsPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                playbackOrder.Observe(IsPlaying);
                var timeline = current.GetTimelineProperties();
                var progress = MusicProgress.Calculate(timeline.StartTime, timeline.EndTime, timeline.Position, timeline.LastUpdatedTime, DateTimeOffset.UtcNow, IsPlaying, playback.PlaybackRate ?? 1);
                DurationSeconds = progress.Duration; PositionSeconds = progress.Position;
                if (Interlocked.Exchange(ref dirty, 0) == 1)
                {
                    Cover = null;
                    var media = await current.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), token);
                    if (Disposed || !ReferenceEquals(current, session)) return;
                    Title = string.IsNullOrWhiteSpace(media.Title) ? "未知歌曲" : media.Title; Artist = media.Artist;
                    if (media.Thumbnail != null)
                    {
                        try
                        {
                            using var thumbnail = await media.Thumbnail.OpenReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), token);
                            if (thumbnail.Size <= 8 * 1024 * 1024)
                            {
                                using var stream = thumbnail.AsStreamForRead();
                                using var buffer = new MemoryStream();
                                await stream.CopyToAsync(buffer, token).WaitAsync(TimeSpan.FromSeconds(2), token);
                                buffer.Position = 0;
                                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 144; image.StreamSource = buffer; image.EndInit(); image.Freeze();
                                if (!Disposed && ReferenceEquals(current, session) && Volatile.Read(ref dirty) == 0) Cover = image;
                            }
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { Cover = null; }
                    }
                }
                Note = "";
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { ResetMedia(false); Interlocked.Exchange(ref dirty, 1); Note = "读取播放状态失败：" + e.Message; }
        Changed(nameof(LastStarted));
        CommandManager.InvalidateRequerySuggested();
    }
    private async Task Control(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> operation)
    {
        var target = session ?? throw new InvalidOperationException(DisplayName + "没有可控制的播放会话。");
        if (!await operation(target).WaitAsync(TimeSpan.FromSeconds(3))) throw new InvalidOperationException(DisplayName + "未接受此操作。");
        Interlocked.Exchange(ref dirty, 1);
    }
    private void ResetMedia(bool observeStop = true)
    {
        canPrevious = canNext = canToggle = false; IsPlaying = false; Cover = null;
        DurationSeconds = PositionSeconds = 0; Changed(nameof(PositionLabel));
        Title = "暂无可控制的播放会话"; Artist = "请在" + DisplayName + "中开始播放";
        if (observeStop) playbackOrder.Observe(false);
    }
    public override void Dispose() { base.Dispose(); if (manager != null) manager.SessionsChanged -= SessionsChanged; SelectSession(null); manager = null; }
}
