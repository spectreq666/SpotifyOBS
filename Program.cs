using System;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using WindowsAudioCatcher.Media;

const string MutexName = "SpectreqOBS_Spotify_SingleInstance_Mutex";
const string WakeEventName = "SpectreqOBS_Spotify_WakeEvent";

bool isFirstInstance;
Mutex? singleInstanceMutex = null;

try
{
    singleInstanceMutex = new Mutex(true, @"Local\" + MutexName, out isFirstInstance);
}
catch (AbandonedMutexException)
{
    isFirstInstance = true;
}
catch
{
    try
    {
        singleInstanceMutex = new Mutex(true, MutexName, out isFirstInstance);
    }
    catch
    {
        isFirstInstance = true;
    }
}

if (!isFirstInstance)
{
    Console.WriteLine("Spectreq OBS Spotify уже работает. Активируем работающий экземпляр...");
    try
    {
        if (EventWaitHandle.TryOpenExisting(@"Local\" + WakeEventName, out var wakeEvent) ||
            EventWaitHandle.TryOpenExisting(WakeEventName, out wakeEvent))
        {
            wakeEvent.Set();
            wakeEvent.Dispose();
            Environment.Exit(0);
            return;
        }
    }
    catch { }

    MessageBox.Show(
        "Приложение Spectreq OBS Spotify уже работает!\nИконка приложения находится в системном трее (возле часов).",
        "Spectreq OBS Spotify",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);
    Environment.Exit(0);
    return;
}

static string GetAppDirectory()
{
    try
    {
        string? procPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(procPath))
        {
            string? dir = Path.GetDirectoryName(procPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                return dir;
            }
        }
    }
    catch { }
    return AppContext.BaseDirectory;
}

string appDir = GetAppDirectory();

try
{
    Directory.SetCurrentDirectory(appDir);
}
catch { }

static int FindAvailablePort(int startingPort = 5000, int maxAttempts = 50)
{
    for (int p = startingPort; p < startingPort + maxAttempts; p++)
    {
        if (IsPortAvailable(p))
            return p;
    }
    return startingPort;
}

static bool IsPortAvailable(int port)
{
    try
    {
        var ipProps = IPGlobalProperties.GetIPGlobalProperties();
        var tcpListeners = ipProps.GetActiveTcpListeners();
        foreach (var ep in tcpListeners)
        {
            if (ep.Port == port) return false;
        }

        using var tcpListener = new TcpListener(IPAddress.Loopback, port);
        tcpListener.Start();
        tcpListener.Stop();
        return true;
    }
    catch
    {
        return false;
    }
}

int port = FindAvailablePort(5000);

string wwwrootPath = Path.Combine(appDir, "wwwroot");
if (!Directory.Exists(wwwrootPath))
{
    string altPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    if (Directory.Exists(altPath))
    {
        wwwrootPath = altPath;
    }
    else
    {
        try { Directory.CreateDirectory(wwwrootPath); } catch { }
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = appDir,
    WebRootPath = wwwrootPath
});

builder.WebHost.UseUrls($"http://localhost:{port}");

builder.Services.AddSingleton<SpotifyWatcher>();
builder.Services.AddHostedService<SpotifyBackgroundService>();

var app = builder.Build();

var fileProvider = new PhysicalFileProvider(wwwrootPath);
app.Environment.WebRootPath = wwwrootPath;
app.Environment.WebRootFileProvider = fileProvider;

var defaultFileOptions = new DefaultFilesOptions
{
    FileProvider = fileProvider
};
defaultFileOptions.DefaultFileNames.Clear();
defaultFileOptions.DefaultFileNames.Add("index.html");
app.UseDefaultFiles(defaultFileOptions);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = fileProvider,
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Context.Response.Headers["Pragma"] = "no-cache";
        ctx.Context.Response.Headers["Expires"] = "0";
    }
});

var watcher = app.Services.GetRequiredService<SpotifyWatcher>();

string themeConfigFile = Path.Combine(appDir, "theme_config.json");
string customGifPath = Path.Combine(wwwrootPath, "custom_bg.gif");

void EnsureDefaultGifExists(string path)
{
    try
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        if (!File.Exists(path))
        {
            byte[] defaultBytes = Convert.FromBase64String("R0lGODlhQABAAIABAAD/AP///yH/C05FVFNDQVBFMj4wAwEAAAAh+QQJFAABACwAAAAAQABAAAACV4yPqcvtD6OctNqLs968+w+G4kiW5omm6sq27gvH8kzX9o3n+s73/g8MCofEovGITCqXzKbzCY1Kp9Sq9YrNarfcrvcLD8fi8bhdGpdL5/NZXT6v2+94BAAh+QQJFAABACwAAAAAQABAAAACV4yPqcvtD6OctNqLs968+w+G4kiW5omm6sq27gvH8kzX9o3n+s73/g8MCofEovGITCqXzKbzCY1Kp9Sq9YrNarfcrvcLD8fi8bhdGpdL5/NZXT6v2+94BAA7");
            File.WriteAllBytes(path, defaultBytes);
        }
    }
    catch { }
}

EnsureDefaultGifExists(customGifPath);

string LoadSavedTheme()
{
    try
    {
        if (File.Exists(themeConfigFile))
        {
            string json = File.ReadAllText(themeConfigFile);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("theme", out var themeProp))
            {
                string? saved = themeProp.GetString();
                if (!string.IsNullOrWhiteSpace(saved)) return saved;
            }
        }
    }
    catch { }
    return "neon-pink";
}

void SaveSavedTheme(string themeName)
{
    try
    {
        string json = JsonSerializer.Serialize(new { theme = themeName });
        File.WriteAllText(themeConfigFile, json);
    }
    catch { }
}

string currentTheme = LoadSavedTheme();

long gifVersion = DateTime.UtcNow.Ticks;

app.MapGet("/api/theme", () =>
{
    bool isGifMissing = (currentTheme == "custom-gif" && !File.Exists(customGifPath));
    return Results.Ok(new
    {
        theme = currentTheme,
        hasGif = File.Exists(customGifPath),
        gifMissing = isGifMissing,
        gifVersion = gifVersion
    });
});

app.MapPost("/api/theme", (ThemeDto dto) =>
{
    if (!string.IsNullOrWhiteSpace(dto.Theme))
    {
        currentTheme = dto.Theme;
        SaveSavedTheme(currentTheme);
    }
    return Results.Ok(new { theme = currentTheme, hasGif = File.Exists(customGifPath), gifVersion = gifVersion });
});

app.MapGet("/api/theme/set", (string name) =>
{
    if (!string.IsNullOrWhiteSpace(name))
    {
        currentTheme = name;
        if (name == "custom-gif")
        {
            gifVersion = DateTime.UtcNow.Ticks;
        }
        SaveSavedTheme(currentTheme);
    }
    return Results.Ok(new
    {
        theme = currentTheme,
        hasGif = File.Exists(customGifPath),
        gifMissing = (currentTheme == "custom-gif" && !File.Exists(customGifPath)),
        gifVersion = gifVersion
    });
});

app.MapGet("/api/gif", () =>
{
    bool exists = File.Exists(customGifPath);
    return Results.Ok(new
    {
        exists = exists,
        url = exists ? $"/custom_bg.gif?v={gifVersion}" : "",
        gifVersion = gifVersion
    });
});

app.MapPost("/api/gif/upload", async (HttpRequest request) =>
{
    try
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        byte[] bytes = ms.ToArray();

        if (bytes.Length > 0)
        {
            await File.WriteAllBytesAsync(customGifPath, bytes);
            currentTheme = "custom-gif";
            gifVersion = DateTime.UtcNow.Ticks;
            SaveSavedTheme(currentTheme);
            return Results.Ok(new { success = true, url = $"/custom_bg.gif?v={gifVersion}", gifVersion = gifVersion });
        }
        return Results.BadRequest(new { error = "Empty file" });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/api/state", () =>
{
    if (!watcher.State.Playing)
    {
        return Results.Ok(new
        {
            playing = false,
            theme = currentTheme,
            gifVersion = gifVersion
        });
    }

    return Results.Ok(new
    {
        playing = true,
        title = watcher.State.Title,
        artist = watcher.State.Artist,
        album = watcher.State.Album,
        position = watcher.CurrentPosition,
        duration = watcher.State.Duration,
        theme = currentTheme,
        gifVersion = gifVersion
    });
});

app.MapGet("/api/cover", () =>
{
    if (watcher.CoverBytes == null)
    {
        return Results.NotFound();
    }
    return Results.File(watcher.CoverBytes, "image/jpeg");
});

app.MapGet("/", () =>
{
    string indexPath = Path.Combine(wwwrootPath, "index.html");
    if (File.Exists(indexPath))
    {
        return Results.File(indexPath, "text/html");
    }
    return Results.NotFound($"index.html not found at {indexPath}");
});

app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fileProvider });

await app.StartAsync();

EventWaitHandle? wakeEventHandle = null;
try
{
    wakeEventHandle = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + WakeEventName);
}
catch
{
    try
    {
        wakeEventHandle = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
    }
    catch { }
}

using var wakeCts = new CancellationTokenSource();
var trayApp = new WindowsAudioCatcher.TrayApplication(app, port);

if (wakeEventHandle != null)
{
    var wakeThread = new Thread(() =>
    {
        var waitHandles = new WaitHandle[] { wakeEventHandle, wakeCts.Token.WaitHandle };
        while (!wakeCts.Token.IsCancellationRequested)
        {
            try
            {
                int idx = WaitHandle.WaitAny(waitHandles);
                if (idx == 0)
                {
                    trayApp.ShowPlayerWindowFromExternal();
                }
                else
                {
                    break;
                }
            }
            catch
            {
                break;
            }
        }
    })
    {
        IsBackground = true
    };
    wakeThread.Start();
}

try
{
    Application.Run(trayApp);
}
finally
{
    wakeCts.Cancel();
    wakeEventHandle?.Dispose();
    try { singleInstanceMutex?.ReleaseMutex(); } catch { }
    singleInstanceMutex?.Dispose();
}

record ThemeDto(string Theme);