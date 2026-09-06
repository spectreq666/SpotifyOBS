using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Foundation;

namespace WindowsAudioCatcher.Media;

public class SpotifyWatcher
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private byte[]? _coverBytes;
    private string _lastTrackId = "";
    private double _basePosition;
    private DateTime _lastPositionUpdate;
    private DateTime _lastSessionCheck = DateTime.MinValue;
    private readonly object _lock = new();

    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _mediaHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _playbackHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>? _timelineHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs>? _sessionsChangedHandler;

    public byte[]? CoverBytes => _coverBytes;
    public SpotifyState State { get; } = new();

    public double CurrentPosition
    {
        get
        {
            lock (_lock)
            {
                if (!State.Playing || _lastPositionUpdate == default)
                {
                    return State.Position;
                }

                var elapsed = (DateTime.UtcNow - _lastPositionUpdate).TotalSeconds;
                var pos = _basePosition + elapsed;
                if (State.Duration > 0 && pos > State.Duration)
                {
                    pos = State.Duration;
                }
                return Math.Max(0, pos);
            }
        }
    }

    public async Task UpdateAsync()
    {
        try
        {
            if (_session == null || DateTime.UtcNow - _lastSessionCheck > TimeSpan.FromSeconds(5))
            {
                _lastSessionCheck = DateTime.UtcNow;
                await EnsureSessionAsync();
            }

            if (_session == null)
            {
                lock (_lock)
                {
                    State.Connected = false;
                    State.Playing = false;
                }
                return;
            }

            // Verify session is still alive
            try
            {
                var playback = _session.GetPlaybackInfo();
                bool isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                lock (_lock)
                {
                    State.Playing = isPlaying;
                    State.Position = CurrentPosition;
                }
            }
            catch
            {
                ClearSession();
                lock (_lock)
                {
                    State.Connected = false;
                    State.Playing = false;
                }
            }
        }
        catch (Exception ex)
        {
            LogError("UpdateAsync", ex);
        }
    }

    private async Task EnsureSessionAsync()
    {
        try
        {
            if (_sessionManager == null)
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_sessionManager != null)
                {
                    _sessionsChangedHandler = async (_, _) => await EnsureSessionAsync();
                    _sessionManager.SessionsChanged += _sessionsChangedHandler;
                }
            }

            if (_sessionManager == null) return;

            // If existing session is still alive and responsive, do not recreate/reset it
            if (_session != null)
            {
                try
                {
                    var info = _session.GetPlaybackInfo();
                    if (info != null)
                    {
                        return;
                    }
                }
                catch
                {
                    ClearSession();
                }
            }

            var sessions = _sessionManager.GetSessions();
            var spotifySession = sessions.FirstOrDefault(x => x.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));

            if (spotifySession == null)
            {
                ClearSession();
                return;
            }

            ClearSession();
            _session = spotifySession;
            SubscribeSession(_session);

            await UpdateMedia();
            UpdatePlayback();
            UpdateTimeline();
        }
        catch (Exception ex)
        {
            LogError("EnsureSessionAsync", ex);
        }
    }

    private void SubscribeSession(GlobalSystemMediaTransportControlsSession session)
    {
        _mediaHandler = async (_, _) => await UpdateMedia();
        _playbackHandler = (_, _) => UpdatePlayback();
        _timelineHandler = (_, _) => UpdateTimeline();

        session.MediaPropertiesChanged += _mediaHandler;
        session.PlaybackInfoChanged += _playbackHandler;
        session.TimelinePropertiesChanged += _timelineHandler;
    }

    private void ClearSession()
    {
        if (_session == null) return;

        try
        {
            if (_mediaHandler != null) _session.MediaPropertiesChanged -= _mediaHandler;
            if (_playbackHandler != null) _session.PlaybackInfoChanged -= _playbackHandler;
            if (_timelineHandler != null) _session.TimelinePropertiesChanged -= _timelineHandler;
        }
        catch { }

        _session = null;
        _mediaHandler = null;
        _playbackHandler = null;
        _timelineHandler = null;
    }

    private async Task UpdateMedia()
    {
        if (_session == null) return;

        try
        {
            var media = await _session.TryGetMediaPropertiesAsync();
            if (media == null) return;

            string title = media.Title ?? "";
            string artist = media.Artist ?? "";
            string album = media.AlbumTitle ?? "";
            var trackId = $"{title}|{artist}";

            bool trackChanged = false;
            lock (_lock)
            {
                State.Connected = true;
                State.Title = title;
                State.Artist = artist;
                State.Album = album;

                if (trackId != _lastTrackId)
                {
                    _lastTrackId = trackId;
                    trackChanged = true;
                    _lastPositionUpdate = default;
                    _basePosition = 0;
                    State.Position = 0;
                }
            }

            if (trackChanged)
            {
                UpdateTimeline();
                await UpdateCover(media);
            }
        }
        catch (Exception ex)
        {
            LogError("UpdateMedia", ex);
        }
    }

    private void UpdatePlayback()
    {
        if (_session == null) return;

        try
        {
            var playback = _session.GetPlaybackInfo();
            if (playback == null) return;

            bool wasPlaying;
            bool isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            lock (_lock)
            {
                wasPlaying = State.Playing;
                State.Playing = isPlaying;

                if (wasPlaying && !isPlaying)
                {
                    // Paused - freeze position at current estimate
                    State.Position = CurrentPosition;
                    _basePosition = State.Position;
                    _lastPositionUpdate = DateTime.UtcNow;
                }
            }

            if (!wasPlaying && isPlaying)
            {
                // Resumed playing - refresh timeline from GSMTC
                UpdateTimeline();
            }
        }
        catch (Exception ex)
        {
            LogError("UpdatePlayback", ex);
        }
    }

    private void UpdateTimeline()
    {
        if (_session == null) return;

        try
        {
            var timeline = _session.GetTimelineProperties();
            if (timeline == null) return;

            lock (_lock)
            {
                State.Duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);

                double rawPos = timeline.Position.TotalSeconds;
                double adjustedPos = rawPos;

                // GSMTC timeline position is timestamped at LastUpdatedTime.
                // When playing, add the elapsed time since LastUpdatedTime.
                if (State.Playing && timeline.LastUpdatedTime > DateTimeOffset.MinValue && timeline.LastUpdatedTime.Year > 2000)
                {
                    var timeSinceReport = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds;
                    if (timeSinceReport > 0)
                    {
                        adjustedPos += timeSinceReport;
                    }
                }

                if (State.Duration > 0 && adjustedPos > State.Duration)
                {
                    adjustedPos = State.Duration;
                }

                double newPos = Math.Max(0, adjustedPos);

                // Anti-jitter: If track is actively playing, ignore tiny backward jumps (< 1.5s)
                // caused by GSMTC timer reporting lag. Real seeks backwards (> 1.5s) are applied immediately.
                if (State.Playing && _lastPositionUpdate != default)
                {
                    var currentEstimate = _basePosition + (DateTime.UtcNow - _lastPositionUpdate).TotalSeconds;
                    var diff = currentEstimate - newPos;
                    if (diff > 0 && diff < 1.5)
                    {
                        newPos = currentEstimate;
                    }
                }

                _basePosition = newPos;
                _lastPositionUpdate = DateTime.UtcNow;
                State.Position = _basePosition;
            }
        }
        catch (Exception ex)
        {
            LogError("UpdateTimeline", ex);
        }
    }

    private async Task UpdateCover(GlobalSystemMediaTransportControlsSessionMediaProperties media)
    {
        if (media.Thumbnail == null) return;

        try
        {
            using var stream = await media.Thumbnail.OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(memory);

            var bytes = memory.ToArray();
            if (_coverBytes == null || !_coverBytes.SequenceEqual(bytes))
            {
                _coverBytes = bytes;
            }
        }
        catch { }
    }

    private void LogError(string context, Exception ex)
    {
        try
        {
            string logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpectreqOBS-Spotify", "Logs");
            if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);
            File.AppendAllText(Path.Combine(logFolder, "spotify_error.log"), $"{DateTime.Now} [{context}]\n{ex}\n\n");
        }
        catch { }
    }
}