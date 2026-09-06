using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace WindowsAudioCatcher;

public class PlayerWindow : Form
{
    private WebView2? _webView;
    private readonly Panel _titleBar;
    private readonly Label _titleLabel;
    private readonly Panel _nativeContainer;
    private readonly HttpClient _httpClient = new HttpClient();
    private readonly int _port;
    private bool _isClosingForReal = false;
    private bool _webViewInitialized = false;

    private const int TITLE_H = 40;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 0x2;

    public PlayerWindow(int port = 5000)
    {
        _port = port;
        Text = "Spectreq OBS Spotify";
        Size = new Size(760, 710);
        MinimumSize = new Size(640, 500);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(10, 10, 18);
        ShowIcon = true;
        ShowInTaskbar = true;
        DoubleBuffered = true;

        LoadIcon();
        ApplyRoundedCorners();
        Resize += (_, _) => ApplyRoundedCorners();

        // ── Title bar ──
        _titleBar = new BufferedPanel
        {
            Dock = DockStyle.Top,
            Height = TITLE_H,
        };
        _titleBar.Paint += PaintTitleBar;
        _titleBar.MouseDown += DragWindow;

        _titleLabel = new Label
        {
            Text = "Spectreq OBS Spotify",
            ForeColor = Color.FromArgb(210, 210, 235),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 10),
            BackColor = Color.Transparent,
            Cursor = Cursors.Default
        };
        _titleLabel.MouseDown += DragWindow;
        _titleBar.Controls.Add(_titleLabel);

        // Title bar buttons — simple, clean, no emoji
        var closeBtn = new TitleBtn("×", Color.FromArgb(220, 50, 60), Color.White);
        closeBtn.Click += (_, _) => Hide();
        _titleBar.Controls.Add(closeBtn);

        var minBtn = new TitleBtn("−", Color.FromArgb(55, 50, 85), Color.FromArgb(200, 195, 220));
        minBtn.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _titleBar.Controls.Add(minBtn);

        LayoutButtons(closeBtn, minBtn);
        _titleBar.Resize += (_, _) => LayoutButtons(closeBtn, minBtn);

        // ── Accent line ──
        var accent = new BufferedPanel { Dock = DockStyle.Top, Height = 1 };
        accent.Paint += (_, e) =>
        {
            if (accent.Width <= 0) return;
            using var b = new LinearGradientBrush(
                new Point(0, 0), new Point(accent.Width, 0),
                Color.FromArgb(130, 80, 255), Color.Transparent);
            var blend = new ColorBlend(3)
            {
                Colors = new[] { Color.FromArgb(140, 130, 80, 255), Color.FromArgb(100, 60, 130, 235), Color.Transparent },
                Positions = new[] { 0f, 0.45f, 1f }
            };
            b.InterpolationColors = blend;
            e.Graphics.FillRectangle(b, 0, 0, accent.Width, 1);
        };

        // ── Native fallback ──
        _nativeContainer = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(10, 10, 18),
            Padding = new Padding(16, 24, 16, 16),
            AutoScroll = true
        };
        Controls.Add(_nativeContainer);
        BuildNativeUI();

        // ── WebView2 ──
        try
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(10, 10, 18)
            };
            Controls.Add(_webView);
        }
        catch (Exception ex)
        {
            LogError("webview_error.log", $"WebView2 ctor: {ex.Message}");
        }

        Controls.Add(accent);
        Controls.Add(_titleBar);
        _titleBar.BringToFront();
    }

    // ═════════════════════════════════════════════
    //  Title bar paint
    // ═════════════════════════════════════════════

    private void PaintTitleBar(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = _titleBar.ClientRectangle;

        // Gradient: dark to slightly lighter
        using var bg = new LinearGradientBrush(r,
            Color.FromArgb(14, 13, 26), Color.FromArgb(20, 18, 36),
            LinearGradientMode.Horizontal);
        g.FillRectangle(bg, r);
    }

    private static void LayoutButtons(Control close, Control min)
    {
        var parent = close.Parent;
        if (parent == null) return;
        int y = (parent.Height - 28) / 2;
        close.Location = new Point(parent.Width - 38, y);
        min.Location = new Point(parent.Width - 72, y);
    }

    // ═════════════════════════════════════════════
    //  Rounded corners
    // ═════════════════════════════════════════════

    private void ApplyRoundedCorners()
    {
        if (Width <= 0 || Height <= 0) return;
        try
        {
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
            Region = null;
            return;
        }
        catch { }

        using var p = RoundedRect(new Rectangle(0, 0, Width, Height), 12);
        Region = new Region(p);
    }

    // ═════════════════════════════════════════════
    //  WebView2
    // ═════════════════════════════════════════════

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_webViewInitialized && _webView != null)
            await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        if (_webView == null) return;
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userData = Path.Combine(appData, "SpectreqOBS-Spotify", "WebView2");
            string logDir = Path.Combine(appData, "SpectreqOBS-Spotify", "Logs");
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);

            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userData);
            await _webView.EnsureCoreWebView2Async(env);

            if (_webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.IsScriptEnabled = true;

                try { await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(); } catch { }

                string url = $"http://localhost:{_port}/?mode=app&v={DateTime.Now.Ticks}";

                _webView.CoreWebView2.NavigationCompleted += async (_, args) =>
                {
                    if (args.IsSuccess)
                    {
                        _webViewInitialized = true;
                        _webView.BringToFront();
                        _titleBar.BringToFront();
                    }
                    else
                    {
                        LogError(Path.Combine(logDir, "webview_error.log"), $"Nav failed: {args.WebErrorStatus}");
                        await Task.Delay(800);
                        try { _webView.CoreWebView2.Navigate(url); } catch { }
                    }
                };

                _webView.CoreWebView2.Navigate(url);
                _webView.BringToFront();
                _titleBar.BringToFront();
            }
        }
        catch (Exception ex)
        {
            LogError("webview_error.log", $"Init: {ex.Message}");
            _nativeContainer.BringToFront();
            _titleBar.BringToFront();
        }
    }

    // ═════════════════════════════════════════════
    //  Native fallback UI (shown until WebView2 loads)
    // ═════════════════════════════════════════════

    private void BuildNativeUI()
    {
        _nativeContainer.Controls.Clear();
        _nativeContainer.AutoScroll = true;

        var grid = new FlowLayoutPanel
        {
            Width = 715,
            Height = 1115,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0),
            Padding = new Padding(6, 10, 6, 20)
        };

        _nativeContainer.Resize += (_, _) =>
        {
            grid.Location = new Point(Math.Max(0, (_nativeContainer.ClientSize.Width - grid.Width) / 2), 0);
        };
        grid.Location = new Point(Math.Max(0, (_nativeContainer.ClientSize.Width - grid.Width) / 2), 0);

        AddCard(grid, "Neon Cyber Pink", "neon-pink", "Киберпанк неон с розовым свечением, бирюзовой рамкой и скайнлайнами.", DrawNeonPreview);
        AddCard(grid, "Deep Purple Glass", "deep-purple", "Фиолетовый гласморфизм с эффектом матового размытия и свечением.", DrawPurplePreview);
        AddCard(grid, "Emerald Matrix", "emerald-mint", "Матричный терминал: мятная подсветка, цифровая сетка и моно-шрифт.", DrawEmeraldPreview);
        AddCard(grid, "Aurora Sunset", "aurora-sunset", "Закатные тона, золотистая аура и вращающийся виниловый диск.", DrawAuroraPreview);
        AddCard(grid, "Obsidian Dark", "obsidian-dark", "Тёмный обсидиан с неоново-голубыми лучами и индиго свечением.", DrawObsidianPreview);
        AddCard(grid, "Custom GIF", "custom-gif", "GIF-анимация с затемнением фона и летящими частицами.", DrawGifPreview, true);

        _nativeContainer.Controls.Add(grid);
    }

    private void AddCard(FlowLayoutPanel parent, string title, string key, string desc, PaintEventHandler preview, bool gif = false)
    {
        var card = new BufferedPanel
        {
            Size = new Size(335, 345),
            Margin = new Padding(8),
            BackColor = Color.FromArgb(18, 17, 32),
        };

        // Rounded border paint
        card.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 12);
            using var pen = new Pen(Color.FromArgb(25, 255, 255, 255), 1f);
            e.Graphics.DrawPath(pen, path);
        };
        // Clip to rounded shape
        card.Resize += (_, _) =>
        {
            using var p = RoundedRect(new Rectangle(0, 0, card.Width, card.Height), 12);
            card.Region = new Region(p);
        };
        using (var initP = RoundedRect(new Rectangle(0, 0, card.Width, card.Height), 12))
            card.Region = new Region(initP);

        // Preview box
        var prevBox = new BufferedPanel
        {
            Size = new Size(319, 180),
            Location = new Point(8, 8),
            BackColor = Color.FromArgb(10, 10, 18),
            Tag = key
        };
        prevBox.Paint += (s, e) =>
        {
            string? img = FindPreviewImage(key);
            if (img != null)
            {
                try
                {
                    using var image = Image.FromFile(img);
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    var (w, h) = FitSize(image.Width, image.Height, prevBox.Width, prevBox.Height, 0.94f);
                    int x = (prevBox.Width - w) / 2, y = (prevBox.Height - h) / 2;
                    e.Graphics.DrawImage(image, new Rectangle(x, y, w, h));
                    return;
                }
                catch { }
            }
            preview(s, e);
        };
        card.Controls.Add(prevBox);

        // Title — no emoji, clean
        card.Controls.Add(new Label
        {
            Text = title,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            Location = new Point(10, 194),
            AutoSize = true,
            BackColor = Color.Transparent
        });

        // Description
        card.Controls.Add(new Label
        {
            Text = desc,
            ForeColor = Color.FromArgb(145, 145, 170),
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(10, 218),
            Size = new Size(315, 70),
            BackColor = Color.Transparent
        });

        // Apply button
        var applyBtn = new GradBtn("Применить тему",
            gif ? new Size(218, 34) : new Size(315, 34),
            new Point(10, 300),
            Color.FromArgb(100, 60, 220), Color.FromArgb(45, 100, 230));
        applyBtn.Click += async (_, _) => await ApplyThemeAsync(key);
        card.Controls.Add(applyBtn);

        if (gif)
        {
            var gifBtn = new GradBtn("GIF",
                new Size(89, 34), new Point(236, 300),
                Color.FromArgb(220, 30, 110), Color.FromArgb(150, 40, 220));
            gifBtn.Click += async (_, _) => await ChooseCustomGifAsync();
            card.Controls.Add(gifBtn);
        }

        parent.Controls.Add(card);
    }

    // ═════════════════════════════════════════════
    //  Preview drawing
    // ═════════════════════════════════════════════

    private void DrawCardBase(Graphics g, Rectangle r, Color bg, Color border, Color coverBg, Color titleC, Color artistC, Color progC, bool vinyl = false, bool mono = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = RoundedRect(r, 14))
        using (var fill = new SolidBrush(bg))
        using (var pen = new Pen(border, 1.5f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        var cover = new Rectangle(r.X + 10, r.Y + 12, 52, 52);
        if (vinyl)
        {
            using var vb = new SolidBrush(Color.FromArgb(20, 20, 25));
            using var vc = new SolidBrush(Color.FromArgb(245, 158, 11));
            using var vp = new Pen(Color.FromArgb(100, 255, 255, 255), 1.5f);
            g.FillEllipse(vb, cover);
            g.DrawEllipse(vp, cover);
            g.FillEllipse(vc, cover.X + 20, cover.Y + 20, 12, 12);
        }
        else
        {
            int rad = mono ? 4 : 10;
            using var cp = RoundedRect(cover, rad);
            using var cb = new SolidBrush(coverBg);
            using var cn = new Pen(border, 1.5f);
            g.FillPath(cb, cp);
            g.DrawPath(cn, cp);
        }

        int tx = r.X + 70;
        using var tf = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var af = new Font("Segoe UI", 7.5f);
        using var tb = new SolidBrush(titleC);
        using var ab = new SolidBrush(artistC);
        g.DrawString("Track Title", tf, tb, tx, r.Y + 12);
        g.DrawString("Artist Name", af, ab, tx, r.Y + 30);

        int py = r.Y + 48, pw = r.Width - 82;
        using var bgP = new Pen(Color.FromArgb(50, 255, 255, 255), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fP = new Pen(progC, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(bgP, tx, py, tx + pw, py);
        g.DrawLine(fP, tx, py, tx + (int)(pw * 0.42f), py);

        using var tmf = new Font("Segoe UI", 7f);
        using var tmb = new SolidBrush(Color.FromArgb(140, 140, 165));
        g.DrawString("0:53", tmf, tmb, tx, r.Y + 56);
        g.DrawString("2:21", tmf, tmb, tx + pw - 22, r.Y + 56);
    }

    private void DrawNeonPreview(object? s, PaintEventArgs e) =>
        DrawCardBase(e.Graphics, new Rectangle(6, 6, 196, 98), Color.FromArgb(240, 15, 2, 35), Color.FromArgb(255, 0, 127), Color.FromArgb(0, 240, 255), Color.FromArgb(255, 0, 127), Color.FromArgb(0, 240, 255), Color.FromArgb(0, 240, 255));
    private void DrawPurplePreview(object? s, PaintEventArgs e) =>
        DrawCardBase(e.Graphics, new Rectangle(6, 6, 196, 98), Color.FromArgb(240, 25, 8, 60), Color.FromArgb(192, 132, 252), Color.FromArgb(168, 85, 247), Color.FromArgb(216, 180, 254), Color.FromArgb(192, 132, 252), Color.FromArgb(168, 85, 247));
    private void DrawEmeraldPreview(object? s, PaintEventArgs e) =>
        DrawCardBase(e.Graphics, new Rectangle(6, 6, 196, 98), Color.FromArgb(240, 2, 20, 10), Color.FromArgb(52, 211, 153), Color.FromArgb(16, 185, 129), Color.FromArgb(52, 211, 153), Color.FromArgb(167, 243, 208), Color.FromArgb(52, 211, 153), mono: true);
    private void DrawAuroraPreview(object? s, PaintEventArgs e) =>
        DrawCardBase(e.Graphics, new Rectangle(6, 6, 196, 98), Color.FromArgb(240, 45, 10, 28), Color.FromArgb(245, 158, 11), Color.FromArgb(239, 68, 68), Color.FromArgb(253, 230, 138), Color.FromArgb(253, 230, 138), Color.FromArgb(245, 158, 11), vinyl: true);
    private void DrawObsidianPreview(object? s, PaintEventArgs e) =>
        DrawCardBase(e.Graphics, new Rectangle(6, 6, 196, 98), Color.FromArgb(240, 6, 8, 20), Color.FromArgb(99, 102, 241), Color.FromArgb(34, 211, 238), Color.FromArgb(34, 211, 238), Color.FromArgb(129, 140, 248), Color.FromArgb(34, 211, 238));
    private void DrawGifPreview(object? s, PaintEventArgs e)
    {
        DrawCardBase(e.Graphics, new Rectangle(6, 6, 196, 98), Color.FromArgb(240, 28, 5, 25), Color.FromArgb(255, 0, 127), Color.FromArgb(255, 102, 204), Color.FromArgb(255, 153, 221), Color.FromArgb(255, 180, 230), Color.FromArgb(255, 0, 127));
        using var p1 = new SolidBrush(Color.FromArgb(220, 255, 0, 127));
        using var p2 = new SolidBrush(Color.FromArgb(200, 0, 240, 255));
        e.Graphics.FillEllipse(p1, 25, 18, 4, 4);
        e.Graphics.FillEllipse(p2, 140, 22, 5, 5);
        e.Graphics.FillEllipse(p1, 175, 75, 4, 4);
        e.Graphics.FillEllipse(p2, 45, 80, 5, 5);
    }

    // ═════════════════════════════════════════════
    //  Theme + GIF actions
    // ═════════════════════════════════════════════

    private async Task ApplyThemeAsync(string key)
    {
        try
        {
            await _httpClient.GetAsync($"http://localhost:{_port}/api/theme/set?name={key}");
            if (_webView?.CoreWebView2 != null)
                await _webView.CoreWebView2.ExecuteScriptAsync($"if(window.setTheme)window.setTheme('{key}')");
            MessageBox.Show($"Тема «{key}» установлена!", "Тема", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public async Task ChooseCustomGifAsync()
    {
        try
        {
            string? file = await ShowFileDialogAsync();
            if (string.IsNullOrEmpty(file)) return;
            byte[] bytes = await File.ReadAllBytesAsync(file);
            using var content = new ByteArrayContent(bytes);
            try
            {
                await _httpClient.PostAsync($"http://localhost:{_port}/api/gif/upload", content);
            }
            catch
            {
                string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                Directory.CreateDirectory(wwwroot);
                await File.WriteAllBytesAsync(Path.Combine(wwwroot, "custom_bg.gif"), bytes);
                await _httpClient.GetAsync($"http://localhost:{_port}/api/theme/set?name=custom-gif");
            }
            await NavigateToThemeAsync("custom-gif");
            MessageBox.Show("GIF фон обновлён!", "Кастомная тема", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogError("gif_error.log", ex.ToString());
            MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка GIF", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public async Task NavigateToThemeAsync(string theme)
    {
        try
        {
            if (_webView?.CoreWebView2 != null)
                await _webView.CoreWebView2.ExecuteScriptAsync($"if(window.setTheme)window.setTheme('{theme}')");
        }
        catch (Exception ex)
        {
            LogError("webview_error.log", $"Theme switch: {ex.Message}");
        }
    }

    // ═════════════════════════════════════════════
    //  Helpers
    // ═════════════════════════════════════════════

    private void LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Logo.ico");
        if (File.Exists(path)) { try { Icon = new Icon(path); } catch { } }
        else { try { var x = Icon.ExtractAssociatedIcon(Application.ExecutablePath); if (x != null) Icon = x; } catch { } }
    }

    private void DragWindow(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0); }
    }

    public void ClosePermanently() { _isClosingForReal = true; Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_isClosingForReal && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        else base.OnFormClosing(e);
    }

    private static string? FindPreviewImage(string key)
    {
        string appDir = GetAppDir();
        string[] paths = {
            Path.Combine(appDir, "wwwroot", "previews", $"{key}.png"),
            Path.Combine(appDir, "previews", $"{key}.png"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "previews", $"{key}.png"),
            Path.Combine(AppContext.BaseDirectory, "previews", $"{key}.png"),
        };
        foreach (var p in paths) if (File.Exists(p)) return p;
        return null;
    }

    private static (int w, int h) FitSize(int imgW, int imgH, int boxW, int boxH, float scale)
    {
        float ia = (float)imgW / imgH, ba = (float)boxW / boxH;
        int w, h;
        if (ia > ba) { w = (int)(boxW * scale); h = (int)(w / ia); }
        else { h = (int)(boxH * scale); w = (int)(h * ia); }
        return (w, h);
    }

    private static string GetAppDir()
    {
        try { var p = Environment.ProcessPath; if (p != null) { var d = Path.GetDirectoryName(p); if (d != null && Directory.Exists(d)) return d; } } catch { }
        return AppContext.BaseDirectory;
    }

    private static Task<string?> ShowFileDialogAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var t = new Thread(() =>
        {
            try
            {
                using var d = new OpenFileDialog
                {
                    Title = "Выберите GIF или изображение",
                    Filter = "GIF (*.gif)|*.gif|Все (*.gif;*.png;*.jpg;*.webp)|*.gif;*.png;*.jpg;*.webp",
                    CheckFileExists = true
                };
                tcs.SetResult(d.ShowDialog() == DialogResult.OK ? d.FileName : null);
            }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    private static void LogError(string file, string msg)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpectreqOBS-Spotify", "Logs");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string fullPath = Path.IsPathRooted(file) ? file : Path.Combine(dir, file);
            File.AppendAllText(fullPath, $"{DateTime.Now} {msg}\n");
        }
        catch { }
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

// ═══════════════════════════════════════════════════
//  BufferedPanel — flicker-free panel
// ═══════════════════════════════════════════════════

internal class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
}

// ═══════════════════════════════════════════════════
//  TitleBtn — minimal round title bar button
// ═══════════════════════════════════════════════════

internal class TitleBtn : Control
{
    private readonly string _sym;
    private readonly Color _hoverBg;
    private readonly Color _hoverFg;
    private bool _hover;

    public TitleBtn(string symbol, Color hoverBg, Color hoverFg)
    {
        _sym = symbol;
        _hoverBg = hoverBg;
        _hoverFg = hoverFg;
        Size = new Size(30, 28);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        if (_hover)
        {
            using var bg = new SolidBrush(_hoverBg);
            g.FillEllipse(bg, 1, 1, Width - 3, Height - 3);
        }

        using var font = new Font("Segoe UI", 11f, FontStyle.Regular);
        using var brush = new SolidBrush(_hover ? _hoverFg : Color.FromArgb(160, 155, 180));
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(_sym, font, brush, new RectangleF(0, -1, Width, Height), sf);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
}

// ═══════════════════════════════════════════════════
//  GradBtn — gradient rounded button
// ═══════════════════════════════════════════════════

internal class GradBtn : Control
{
    private readonly Color _c1, _c2;
    private bool _hover, _press;

    public GradBtn(string text, Size size, Point loc, Color c1, Color c2)
    {
        Text = text;
        Size = size;
        Location = loc;
        _c1 = c1;
        _c2 = c2;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(r, 8);

        var sc = _hover ? Brighten(_c1, 25) : _c1;
        var ec = _hover ? Brighten(_c2, 25) : _c2;

        using var grad = new LinearGradientBrush(r, sc, ec, LinearGradientMode.Horizontal);
        g.FillPath(grad, path);

        if (_press)
        {
            using var dim = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
            g.FillPath(dim, path);
        }

        using var brush = new SolidBrush(ForeColor);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Text, Font, brush, new RectangleF(0, 0, Width, Height), sf);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { _press = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); }

    private static Color Brighten(Color c, int amount) =>
        Color.FromArgb(c.A, Math.Min(255, c.R + amount), Math.Min(255, c.G + amount), Math.Min(255, c.B + amount));

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
