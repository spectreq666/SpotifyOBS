namespace WindowsAudioCatcher.Media;

public class SpotifyState
{
    public bool Connected { get; set; }
    public bool Playing { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double Position { get; set; }
    public double Duration { get; set; }
}