using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;

namespace WindowsAudioCatcher;

public class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly WebApplication _app;
    private PlayerWindow? _playerWindow;
    private readonly HttpClient _httpClient = new HttpClient();
    private readonly Dictionary<string, ToolStripMenuItem> _themeMenuItems = new();
    private string _currentTheme = "neon-pink";

    public TrayApplication(WebApplication app)
    {
        _app = app;

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Logo.ico");
        Icon appIcon = SystemIcons.Application;
        if (File.Exists(iconPath))
        {
            try { appIcon = new Icon(iconPath); } catch { }
        }
        else
        {
            try
            {
                var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null) appIcon = extracted;
            }
            catch { }
        }

        _trayIcon = new NotifyIcon
        {
            Icon = appIcon,
            Text = "Spectreq OBS Spotify",
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => ShowPlayerWindow();

        var menu = new ContextMenuStrip
        {
            Renderer = new PurpleMenuRenderer(),
            BackColor = Color.FromArgb(18, 14, 34),
            ShowImageMargin = false
        };

        var openWindowItem = new ToolStripMenuItem("📱 Открыть окно плеера", null, (_, _) => ShowPlayerWindow())
        {
            Font = new Font("Segoe UI Emoji", 9.5f, FontStyle.Bold),
            ForeColor = Color.White
        };
        menu.Items.Add(openWindowItem);

        var themeSubMenu = new ToolStripMenuItem("🎨 Тема оформления")
        {
            Font = new Font("Segoe UI Emoji", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(235, 230, 255)
        };
        themeSubMenu.DropDown.Renderer = new PurpleMenuRenderer();
        themeSubMenu.DropDown.BackColor = Color.FromArgb(18, 14, 34);

        AddThemeMenuItem(themeSubMenu, "💗 Неоновый Розовый (Neon Cyber)", "neon-pink");
        AddThemeMenuItem(themeSubMenu, "💜 Фиолетовый Глянцевый (Deep Purple)", "deep-purple");
        AddThemeMenuItem(themeSubMenu, "💚 Изумрудный Мятный (Emerald Mint)", "emerald-mint");
        AddThemeMenuItem(themeSubMenu, "🌅 Закат Аврора (Aurora Sunset)", "aurora-sunset");
        AddThemeMenuItem(themeSubMenu, "🖤 Обсидиан (Obsidian Dark)", "obsidian-dark");
        AddThemeMenuItem(themeSubMenu, "✨ Кастомная GIF тема (Pink Particles)", "custom-gif");
        menu.Items.Add(themeSubMenu);

        menu.Opening += (_, _) => SyncThemeFromConfig();
        themeSubMenu.DropDownOpening += (_, _) => SyncThemeFromConfig();

        menu.Items.Add(new ToolStripSeparator());

        var openBrowserItem = new ToolStripMenuItem("🌐 Открыть Overlay в браузере", null, (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "http://localhost:5000",
                UseShellExecute = true
            });
        })
        {
            Font = new Font("Segoe UI Emoji", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(235, 230, 255)
        };
        menu.Items.Add(openBrowserItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("❌ Выход", null, async (_, _) => await ShutdownAsync())
        {
            Font = new Font("Segoe UI Emoji", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 120, 140)
        };
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;

        SyncThemeFromConfig();
    }

    private async void SyncThemeFromConfig()
    {
        try
        {
            try
            {
                var resp = await _httpClient.GetAsync("http://localhost:5000/api/theme");
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("theme", out var themeProp))
                    {
                        string? activeTheme = themeProp.GetString();
                        if (!string.IsNullOrWhiteSpace(activeTheme))
                        {
                            _currentTheme = activeTheme;
                        }
                    }
                }
            }
            catch
            {
                string appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                string path1 = Path.Combine(appDir, "theme_config.json");
                string path2 = Path.Combine(AppContext.BaseDirectory, "theme_config.json");
                string? configFile = File.Exists(path1) ? path1 : (File.Exists(path2) ? path2 : null);

                if (configFile != null)
                {
                    string json = File.ReadAllText(configFile);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("theme", out var themeProp))
                    {
                        string? savedTheme = themeProp.GetString();
                        if (!string.IsNullOrWhiteSpace(savedTheme))
                        {
                            _currentTheme = savedTheme;
                        }
                    }
                }
            }

            foreach (var kvp in _themeMenuItems)
            {
                kvp.Value.Checked = (kvp.Key == _currentTheme);
            }
        }
        catch { }
    }

    private void AddThemeMenuItem(ToolStripMenuItem parent, string title, string themeKey)
    {
        var item = new ToolStripMenuItem(title, null, async (_, _) => await ChangeThemeAsync(themeKey))
        {
            Checked = (themeKey == _currentTheme),
            Font = new Font("Segoe UI Emoji", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(235, 230, 255)
        };
        _themeMenuItems[themeKey] = item;
        parent.DropDownItems.Add(item);
    }

    private async Task ChangeThemeAsync(string themeKey)
    {
        _currentTheme = themeKey;

        foreach (var kvp in _themeMenuItems)
        {
            kvp.Value.Checked = (kvp.Key == themeKey);
        }

        try
        {
            await _httpClient.GetAsync($"http://localhost:5000/api/theme/set?name={themeKey}");
        }
        catch { }

        if (_playerWindow != null && !_playerWindow.IsDisposed)
        {
            await _playerWindow.NavigateToThemeAsync(themeKey);
        }
    }

    private void ShowPlayerWindow()
    {
        try
        {
            if (_playerWindow == null || _playerWindow.IsDisposed)
            {
                _playerWindow = new PlayerWindow();
            }

            if (_playerWindow.WindowState == FormWindowState.Minimized)
            {
                _playerWindow.WindowState = FormWindowState.Normal;
            }

            _playerWindow.Show();
            _playerWindow.BringToFront();
            _playerWindow.Activate();
        }
        catch (Exception ex)
        {
            string logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpectreqOBS-Spotify", "Logs");
            if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);
            File.AppendAllText(Path.Combine(logFolder, "window_error.log"), $"{DateTime.Now}\n{ex}\n");
            MessageBox.Show($"Ошибка открытия окна: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ShutdownAsync()
    {
        try
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();

            if (_playerWindow != null && !_playerWindow.IsDisposed)
            {
                _playerWindow.ClosePermanently();
            }

            await _app.StopAsync();
        }
        catch (Exception ex)
        {
            string logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpectreqOBS-Spotify", "Logs");
            if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);
            File.AppendAllText(Path.Combine(logFolder, "shutdown_error.log"), $"{DateTime.Now}\n{ex}\n");
        }
        finally
        {
            ExitThread();
        }
    }

    protected override void ExitThreadCore()
    {
        try
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        catch { }

        base.ExitThreadCore();
    }
}

public class PurpleColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(20, 15, 36);
    public override Color ImageMarginGradientBegin => Color.FromArgb(20, 15, 36);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(20, 15, 36);
    public override Color ImageMarginGradientEnd => Color.FromArgb(20, 15, 36);

    public override Color MenuItemSelected => Color.FromArgb(109, 40, 217);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(109, 40, 217);
    public override Color MenuItemSelectedGradientEnd => Color.FromArgb(124, 58, 237);

    public override Color MenuItemPressedGradientBegin => Color.FromArgb(91, 33, 182);
    public override Color MenuItemPressedGradientEnd => Color.FromArgb(109, 40, 217);
    public override Color MenuItemBorder => Color.FromArgb(168, 85, 247);
    public override Color MenuBorder => Color.FromArgb(88, 28, 135);

    public override Color SeparatorDark => Color.FromArgb(60, 45, 95);
    public override Color SeparatorLight => Color.Transparent;

    public override Color CheckBackground => Color.FromArgb(147, 51, 234);
    public override Color CheckSelectedBackground => Color.FromArgb(168, 85, 247);
    public override Color CheckPressedBackground => Color.FromArgb(192, 132, 252);
}

public class PurpleMenuRenderer : ToolStripProfessionalRenderer
{
    public PurpleMenuRenderer() : base(new PurpleColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected ? Color.White : Color.FromArgb(235, 230, 255);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Selected)
        {
            Rectangle rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using var brush = new SolidBrush(Color.FromArgb(109, 40, 217));
            e.Graphics.FillRectangle(brush, rect);
            using var pen = new Pen(Color.FromArgb(168, 85, 247), 1);
            e.Graphics.DrawRectangle(pen, rect);
        }
        else
        {
            base.OnRenderMenuItemBackground(e);
        }
    }
}