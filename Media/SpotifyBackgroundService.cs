using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace WindowsAudioCatcher.Media;

public class SpotifyBackgroundService : BackgroundService
{
    private readonly SpotifyWatcher _watcher;

    public SpotifyBackgroundService(SpotifyWatcher watcher)
    {
        _watcher = watcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _watcher.UpdateAsync();
            }
            catch (Exception ex)
            {
                File.AppendAllText("spotify_error.log", $"{DateTime.Now}\n{ex}\n\n");
            }

            await Task.Delay(1000, stoppingToken);
        }
    }
}