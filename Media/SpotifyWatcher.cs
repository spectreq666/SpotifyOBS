using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Foundation;

namespace WindowsAudioCatcher.Media;

public class SpotifyWatcher
{
    private GlobalSystemMediaTransportControlsSession? _session;
    private byte[]? _coverBytes;
    private string _lastTrackId = "";
    private double _basePosition;
    private DateTime _lastPositionUpdate;
    private DateTime _lastSessionCheck = DateTime.MinValue;

    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _mediaHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _playbackHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>? _timelineHandler;

    public byte[]? CoverBytes => _coverBytes;
    public SpotifyState State { get; } = new();

    public async Task UpdateAsync()
    {
        if (_session == null || DateTime.UtcNow - _lastSessionCheck > TimeSpan.FromSeconds(5))
        {
            _lastSessionCheck = DateTime.UtcNow;
            await FindSpotifySession();
        }

        if (_session == null)
        {
            State.Connected = false;
            State.Playing = false;
            return;
        }

        var elapsed = (DateTime.UtcNow - _lastPositionUpdate).TotalSeconds;

        if (State.Playing)
        {
            State.Position = _basePosition + elapsed;
            if (State.Position > State.Duration)
            {
                State.Position = State.Duration;
            }
        }
    }

    private async Task FindSpotifySession()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetSessions().FirstOrDefault(x => x.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));

            if (session == null)
            {
                ClearSession();
                return;
            }

            if (_session == session) return;

            ClearSession();
            _session = session;
            SubscribeSession(session);

            await UpdateMedia();
            UpdatePlayback();
            UpdateTimeline();
        }
        catch (Exception ex)
        {
            string logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpectreqOBS-Spotify", "Logs");
            if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);
            File.AppendAllText(Path.Combine(logFolder, "spotify_error.log"), $"{DateTime.Now}\n{ex}\n\n");
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

        var media = await _session.TryGetMediaPropertiesAsync();

        State.Connected = true;
        State.Title = media.Title;
        State.Artist = media.Artist;
        State.Album = media.AlbumTitle;

        var trackId = $"{media.Title}|{media.Artist}";

        if (trackId != _lastTrackId)
        {
            _lastTrackId = trackId;
            UpdateTimeline();
            await UpdateCover(media);
        }
    }

    private void UpdatePlayback()
    {
        if (_session == null) return;

        var playback = _session.GetPlaybackInfo();
        bool old = State.Playing;
        State.Playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        if (!old && State.Playing)
        {
            _lastPositionUpdate = DateTime.UtcNow;
        }
    }

    private void UpdateTimeline()
    {
        if (_session == null) return;

        var timeline = _session.GetTimelineProperties();
        _basePosition = timeline.Position.TotalSeconds;
        _lastPositionUpdate = DateTime.UtcNow;
        State.Position = _basePosition;
        State.Duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
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
}