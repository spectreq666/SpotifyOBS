using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;

namespace WindowsAudioCatcher;

public class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly WebApplication _app;
    private readonly int _port;
    private readonly SynchronizationContext? _syncContext;
    private PlayerWindow? _playerWindow;
    private readonly HttpClient _httpClient = new HttpClient();
    private readonly Dictionary<string, ToolStripMenuItem> _themeMenuItems = new();
    private string _currentTheme = "neon-pink";

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public TrayApplication(WebApplication app, int port = 5000)
    {
        _app = app;
        _port = port;
        _syncContext = SynchronizationContext.Current;

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
            Renderer = new SpectreqMenuRenderer(),
            BackColor = Color.FromArgb(14, 12, 24),
            ShowImageMargin = true,
            ImageScalingSize = new Size(20, 20),
            AutoSize = true,
            Padding = new Padding(4, 6, 4, 6),
            DropShadowEnabled = true,
        };
        menu.HandleCreated += (s, _) => { if (s is Control c) TryRoundCorners(c.Handle); };

        // ── Open Player ──
        var openItem = MakeItem("Открыть окно плеера", true);
        openItem.Click += (_, _) => ShowPlayerWindow();
        menu.Items.Add(openItem);

        // ── Themes ──
        var themeItem = MakeItem("Тема оформления", false);
        themeItem.DropDown.Renderer = new SpectreqMenuRenderer();
        themeItem.DropDown.BackColor = Color.FromArgb(14, 12, 24);
        themeItem.DropDown.Padding = new Padding(4, 6, 4, 6);
        themeItem.DropDown.HandleCreated += (s, _) => { if (s is Control c) TryRoundCorners(c.Handle); };

        AddThemeItem(themeItem, "Neon Cyber Pink", "neon-pink");
        AddThemeItem(themeItem, "Deep Purple Glass", "deep-purple");
        AddThemeItem(themeItem, "Emerald Matrix", "emerald-mint");
        AddThemeItem(themeItem, "Aurora Sunset", "aurora-sunset");
        AddThemeItem(themeItem, "Obsidian Dark", "obsidian-dark");
        AddThemeItem(themeItem, "Custom GIF", "custom-gif");
        menu.Items.Add(themeItem);

        menu.Items.Add(new ToolStripSeparator());

        if (_port != 5000)
        {
            _trayIcon.ShowBalloonTip(3500, "Spectreq OBS Spotify", $"Порт 5000 занят. Оверлей запущен на http://localhost:{_port}", ToolTipIcon.Info);
        }

        // ── Open in browser ──
        string browserText = _port == 5000 ? "Открыть Overlay в браузере" : $"Открыть Overlay в браузере (:{_port})";
        var browserItem = MakeItem(browserText, false);
        browserItem.Click += (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"http://localhost:{_port}",
                UseShellExecute = true
            });
        };
        menu.Items.Add(browserItem);

        // ── Copy OBS Link ──
        var copyLinkItem = MakeItem("Скопировать ссылку для OBS", false);
        copyLinkItem.Click += (_, _) =>
        {
            string url = $"http://localhost:{_port}";
            bool ok = CopyToClipboard(url);
            try
            {
                _trayIcon.ShowBalloonTip(3000, "Spectreq OBS Spotify", $"Ссылка скопирована в буфер обмена:\n{url}", ToolTipIcon.Info);
            }
            catch { }

            copyLinkItem.Text = ok ? "✓ Ссылка скопирована!" : "Скопировать ссылку для OBS";
            var timer = new System.Windows.Forms.Timer { Interval = 2500 };
            timer.Tick += (_, _) =>
            {
                copyLinkItem.Text = "Скопировать ссылку для OBS";
                timer.Stop();
                timer.Dispose();
            };
            timer.Start();
        };
        menu.Items.Add(copyLinkItem);

        menu.Items.Add(new ToolStripSeparator());

        // ── Exit ──
        var exitItem = MakeItem("Выход", false, isExit: true);
        exitItem.Click += async (_, _) => await ShutdownAsync();
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;

        menu.Opening += (_, _) => SyncThemeFromConfig();
        themeItem.DropDownOpening += (_, _) => SyncThemeFromConfig();
        SyncThemeFromConfig();
    }

    private static ToolStripMenuItem MakeItem(string text, bool bold, bool isExit = false)
    {
        return new ToolStripMenuItem(text)
        {
            Font = new Font("Segoe UI", 9.75f, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = isExit ? Color.FromArgb(240, 100, 110) : Color.FromArgb(225, 220, 245),
            Padding = new Padding(8, 5, 24, 5),
            Margin = new Padding(4, 2, 4, 2),
        };
    }

    private void AddThemeItem(ToolStripMenuItem parent, string title, string key)
    {
        var item = new ToolStripMenuItem(title, null, async (_, _) => await ChangeThemeAsync(key))
        {
            Checked = key == _currentTheme,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(220, 215, 240),
            Padding = new Padding(8, 4, 20, 4),
            Margin = new Padding(4, 1, 4, 1),
        };
        _themeMenuItems[key] = item;
        parent.DropDownItems.Add(item);
    }

    private static void TryRoundCorners(IntPtr handle)
    {
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    private async void SyncThemeFromConfig()
    {
        try
        {
            try
            {
                var resp = await _httpClient.GetAsync($"http://localhost:{_port}/api/theme");
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("theme", out var themeProp))
                    {
                        string? activeTheme = themeProp.GetString();
                        if (!string.IsNullOrWhiteSpace(activeTheme))
                            _currentTheme = activeTheme;
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
                        string? saved = themeProp.GetString();
                        if (!string.IsNullOrWhiteSpace(saved))
                            _currentTheme = saved;
                    }
                }
            }

            foreach (var kvp in _themeMenuItems)
                kvp.Value.Checked = kvp.Key == _currentTheme;
        }
        catch { }
    }

    private async Task ChangeThemeAsync(string themeKey)
    {
        _currentTheme = themeKey;
        foreach (var kvp in _themeMenuItems)
            kvp.Value.Checked = kvp.Key == themeKey;

        try { await _httpClient.GetAsync($"http://localhost:{_port}/api/theme/set?name={themeKey}"); } catch { }

        if (_playerWindow != null && !_playerWindow.IsDisposed)
            await _playerWindow.NavigateToThemeAsync(themeKey);
    }

    public void ShowPlayerWindow()
    {
        try
        {
            if (_playerWindow == null || _playerWindow.IsDisposed)
                _playerWindow = new PlayerWindow(_port);

            if (_playerWindow.WindowState == FormWindowState.Minimized)
                _playerWindow.WindowState = FormWindowState.Normal;

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

    public void ShowPlayerWindowFromExternal()
    {
        if (_syncContext != null)
        {
            _syncContext.Post(_ => ShowPlayerWindow(), null);
        }
        else
        {
            ShowPlayerWindow();
        }
    }

    private async Task ShutdownAsync()
    {
        try
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            if (_playerWindow != null && !_playerWindow.IsDisposed)
                _playerWindow.ClosePermanently();
            await _app.StopAsync();
        }
        catch (Exception ex)
        {
            string logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpectreqOBS-Spotify", "Logs");
            if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);
            File.AppendAllText(Path.Combine(logFolder, "shutdown_error.log"), $"{DateTime.Now}\n{ex}\n");
        }
        finally { ExitThread(); }
    }

    private static bool CopyToClipboard(string text)
    {
        bool success = false;
        void DoCopy()
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    success = true;
                    return;
                }
                catch
                {
                    Thread.Sleep(50);
                }
            }
        }

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            DoCopy();
        }
        else
        {
            var staThread = new Thread(DoCopy);
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join(1000);
        }

        return success;
    }

    protected override void ExitThreadCore()
    {
        try { _trayIcon.Visible = false; _trayIcon.Dispose(); } catch { }
        base.ExitThreadCore();
    }
}

// ═══════════════════════════════════════════════════════════
//  Spectreq Menu Renderer — clean, no emoji, premium feel
// ═══════════════════════════════════════════════════════════

public class SpectreqMenuRenderer : ToolStripProfessionalRenderer
{
    // Palette
    private static readonly Color Bg = Color.FromArgb(14, 12, 24);
    private static readonly Color HoverFill = Color.FromArgb(28, 24, 50);
    private static readonly Color HoverBorderColor = Color.FromArgb(60, 120, 80, 255);
    private static readonly Color AccentPurple = Color.FromArgb(130, 80, 255);
    private static readonly Color AccentCyan = Color.FromArgb(0, 210, 240);
    private static readonly Color TextNormal = Color.FromArgb(200, 195, 225);
    private static readonly Color TextHover = Color.FromArgb(245, 242, 255);
    private static readonly Color SepColor = Color.FromArgb(35, 130, 80, 255);

    public SpectreqMenuRenderer() : base(new SpectreqColors()) { }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.AffectedBounds;

        // Solid dark fill
        using var bg = new SolidBrush(Bg);
        g.FillRectangle(bg, r);

        // Very subtle gradient sheen at top
        if (r.Height > 0)
        {
            using var sheen = new LinearGradientBrush(
                new Rectangle(r.X, r.Y, r.Width, Math.Min(50, r.Height)),
                Color.FromArgb(8, 255, 255, 255), Color.Transparent, LinearGradientMode.Vertical);
            g.FillRectangle(sheen, r.X, r.Y, r.Width, Math.Min(50, r.Height));
        }

        // Left accent stripe (2px, vertical gradient purple → cyan)
        if (r.Height > 10)
        {
            using var stripe = new LinearGradientBrush(
                new Point(r.X, r.Y + 6), new Point(r.X, r.Bottom - 6),
                Color.FromArgb(80, AccentPurple), Color.FromArgb(40, AccentCyan));
            using var pen = new Pen(stripe, 2f);
            g.DrawLine(pen, r.X + 2, r.Y + 8, r.X + 2, r.Bottom - 8);
        }

        // Outer border
        using var borderPen = new Pen(Color.FromArgb(30, 160, 120, 255), 1f);
        g.DrawRectangle(borderPen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(3, 0, e.Item.Width - 6, e.Item.Height);
        using var path = RoundedRect(rect, 6);

        // Fill — solid dark hover, not gradient (cleaner)
        using var fill = new SolidBrush(HoverFill);
        g.FillPath(fill, path);

        // Border
        using var border = new Pen(HoverBorderColor, 1f);
        g.DrawPath(border, path);

        // Left accent bar on hover (3px tall line, purple)
        int barH = Math.Min(16, rect.Height - 8);
        int barY = rect.Y + (rect.Height - barH) / 2;
        using var barBrush = new SolidBrush(AccentPurple);
        g.FillRectangle(barBrush, rect.X + 2, barY, 3, barH);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = (e.Item.Selected || e.Item.Pressed) ? TextHover : TextNormal;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        int y = e.Item.Height / 2;
        int left = 16;
        int right = e.Item.Width - 16;
        if (right <= left) return;

        using var brush = new LinearGradientBrush(
            new Point(left, y), new Point(right, y),
            Color.Transparent, Color.Transparent);
        var blend = new ColorBlend(3)
        {
            Colors = new[] { Color.Transparent, SepColor, Color.Transparent },
            Positions = new[] { 0f, 0.5f, 1f }
        };
        brush.InterpolationColors = blend;
        using var pen = new Pen(brush, 1f);
        g.DrawLine(pen, left, y, right, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Selected == true
            ? Color.FromArgb(200, 180, 255)
            : Color.FromArgb(130, 125, 160);
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int size = 8;
        int cx = e.ImageRectangle.X + e.ImageRectangle.Width / 2;
        int cy = e.ImageRectangle.Y + e.ImageRectangle.Height / 2;

        // Outer glow
        using var glow = new SolidBrush(Color.FromArgb(30, AccentCyan));
        g.FillEllipse(glow, cx - size, cy - size, size * 2, size * 2);

        // Dot
        using var dot = new SolidBrush(AccentCyan);
        g.FillEllipse(dot, cx - size / 2, cy - size / 2, size, size);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        if (r.Width <= 0 || r.Height <= 0) return p;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

public class SpectreqColors : ProfessionalColorTable
{
    private static readonly Color D = Color.FromArgb(14, 12, 24);
    public override Color ToolStripDropDownBackground => D;
    public override Color ImageMarginGradientBegin => D;
    public override Color ImageMarginGradientMiddle => D;
    public override Color ImageMarginGradientEnd => D;
    public override Color MenuItemSelected => Color.FromArgb(28, 24, 50);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(28, 24, 50);
    public override Color MenuItemSelectedGradientEnd => Color.FromArgb(28, 24, 50);
    public override Color MenuItemPressedGradientBegin => Color.FromArgb(35, 30, 60);
    public override Color MenuItemPressedGradientEnd => Color.FromArgb(35, 30, 60);
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuBorder => Color.FromArgb(30, 160, 120, 255);
    public override Color SeparatorDark => Color.Transparent;
    public override Color SeparatorLight => Color.Transparent;
    public override Color CheckBackground => Color.FromArgb(14, 12, 24);
    public override Color CheckSelectedBackground => Color.FromArgb(28, 24, 50);
    public override Color CheckPressedBackground => Color.FromArgb(28, 24, 50);
}