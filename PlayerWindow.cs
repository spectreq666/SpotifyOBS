using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    private readonly Button _closeButton;
    private readonly Button _minimizeButton;
    private readonly Panel _nativeContainer;
    private readonly HttpClient _httpClient = new HttpClient();
    private bool _isClosingForReal = false;
    private bool _webViewInitialized = false;

    private static readonly Font EmojiTitleFont = GetEmojiFont(9.5f, FontStyle.Bold);
    private static readonly Font EmojiButtonFont = GetEmojiFont(8.5f, FontStyle.Bold);
    private static readonly Font StandardTextFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
    private static readonly Font StandardBoldFont = new Font("Segoe UI", 9f, FontStyle.Bold);
    private static readonly Font CardTitleFont = GetEmojiFont(10f, FontStyle.Bold);

    private static Font GetEmojiFont(float size, FontStyle style = FontStyle.Regular)
    {
        try
        {
            return new Font("Segoe UI Emoji", size, style);
        }
        catch
        {
            return new Font("Segoe UI", size, style);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 0x2;

    public PlayerWindow()
    {
        Text = "Spectreq OBS Spotify";
        Size = new Size(760, 710);
        MinimumSize = new Size(640, 500);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(10, 10, 18);
        ShowIcon = true;
        ShowInTaskbar = true;

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Logo.ico");
        if (File.Exists(iconPath))
        {
            try { Icon = new Icon(iconPath); } catch { }
        }
        else
        {
            try
            {
                var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null) Icon = extracted;
            }
            catch { }
        }

        _titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 38,
            BackColor = Color.FromArgb(16, 16, 26)
        };
        _titleBar.MouseDown += TitleBar_MouseDown;

        _titleLabel = new Label
        {
            Text = "🎵 Spectreq OBS Spotify",
            ForeColor = Color.FromArgb(235, 235, 255),
            Font = EmojiTitleFont,
            AutoSize = true,
            Location = new Point(12, 9)
        };
        _titleLabel.MouseDown += TitleBar_MouseDown;
        _titleBar.Controls.Add(_titleLabel);

        _closeButton = CreateHeaderButton("✕", Color.FromArgb(220, 50, 50));
        _closeButton.Click += (_, _) => Hide();
        _titleBar.Controls.Add(_closeButton);

        _minimizeButton = CreateHeaderButton("—", Color.FromArgb(60, 60, 90));
        _minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _titleBar.Controls.Add(_minimizeButton);

        LayoutHeaderControls();
        _titleBar.Resize += (_, _) => LayoutHeaderControls();

        _nativeContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(10, 10, 18),
            Padding = new Padding(16, 24, 16, 16),
            AutoScroll = true
        };

        Controls.Add(_nativeContainer);
        BuildNativeStudioUI();

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
            File.AppendAllText("webview_error.log", $"{DateTime.Now} WebView2 ctor error: {ex.Message}\n");
        }

        Controls.Add(_titleBar);
        _titleBar.BringToFront();
    }

    private Button CreateHeaderButton(string text, Color hoverColor)
    {
        var btn = new Button
        {
            Text = text,
            Width = 36,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(210, 210, 230),
            Font = EmojiButtonFont,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = hoverColor;
        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(hoverColor.R, hoverColor.G, hoverColor.B);
        return btn;
    }

    private void LayoutHeaderControls()
    {
        int right = _titleBar.Width;
        _closeButton.Location = new Point(right - 36, 0);
        _minimizeButton.Location = new Point(right - 72, 0);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (!_webViewInitialized && _webView != null)
        {
            await InitializeWebViewAsync();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webView == null) return;
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userDataFolder = Path.Combine(appData, "SpectreqOBS-Spotify", "WebView2");
            string logFolder = Path.Combine(appData, "SpectreqOBS-Spotify", "Logs");
            if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);

            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            if (_webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.IsScriptEnabled = true;

                try { await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(); } catch { }

                string url = $"http://localhost:5000/?mode=app&v={DateTime.Now.Ticks}";

                _webView.CoreWebView2.NavigationCompleted += async (s, e) =>
                {
                    if (e.IsSuccess)
                    {
                        _webViewInitialized = true;
                        _webView.BringToFront();
                        _titleBar.BringToFront();
                    }
                    else
                    {
                        File.AppendAllText(Path.Combine(logFolder, "webview_error.log"), $"{DateTime.Now} Navigation failed: {e.WebErrorStatus}, retrying...\n");
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
            string logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpectreqOBS-Spotify", "Logs");
            if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);
            File.AppendAllText(Path.Combine(logFolder, "webview_error.log"), $"{DateTime.Now} WebView2 OnShown Init error: {ex.Message}\n");
            _nativeContainer.BringToFront();
            _titleBar.BringToFront();
        }
    }

    private void BuildNativeStudioUI()
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

        AddThemeCardCustomDraw(grid, "💗 Neon Cyber Pink", "neon-pink", "Киберпанк неон с розовым свечением, бирюзовой рамкой и скайнлайнами.", DrawNeonPreview);
        AddThemeCardCustomDraw(grid, "💜 Deep Purple Glass", "deep-purple", "Фиолетовый гласморфизм с эффектом матового размытия и свечением.", DrawPurplePreview);
        AddThemeCardCustomDraw(grid, "💚 Emerald Matrix", "emerald-mint", "Матричный терминал: мятная подсветка, цифровая сетка и моно-шрифт.", DrawEmeraldPreview);
        AddThemeCardCustomDraw(grid, "🌅 Aurora Sunset", "aurora-sunset", "Закатные тона, золотистая аура и вращающийся виниловый диск.", DrawAuroraPreview);
        AddThemeCardCustomDraw(grid, "🖤 Obsidian Dark", "obsidian-dark", "Тёмный обсидиан с неоново-голубыми лучами и индиго свечением.", DrawObsidianPreview);
        AddThemeCardCustomDraw(grid, "✨ Кастомная GIF", "custom-gif", "GIF-анимация с затемнением фона и летящими частицами.", DrawGifPreview, isGifCard: true);

        _nativeContainer.Controls.Add(grid);
    }

    private void AddThemeCardCustomDraw(FlowLayoutPanel parent, string title, string themeKey, string desc, PaintEventHandler paintHandler, bool isGifCard = false)
    {
        var card = new Panel
        {
            Size = new Size(335, 345),
            Margin = new Padding(8),
            BackColor = Color.FromArgb(18, 18, 30)
        };

        var previewBox = new Panel
        {
            Size = new Size(319, 180),
            Location = new Point(8, 8),
            BackColor = Color.FromArgb(10, 10, 18),
            Tag = themeKey
        };
        previewBox.Paint += (s, e) =>
        {
            string appDir = GetAppDir();
            string pngPath1 = Path.Combine(appDir, "wwwroot", "previews", $"{themeKey}.png");
            string pngPath2 = Path.Combine(appDir, "previews", $"{themeKey}.png");
            string pngPath3 = Path.Combine(AppContext.BaseDirectory, "wwwroot", "previews", $"{themeKey}.png");
            string pngPath4 = Path.Combine(AppContext.BaseDirectory, "previews", $"{themeKey}.png");
            string? imgPath = File.Exists(pngPath1) ? pngPath1 : (File.Exists(pngPath2) ? pngPath2 : (File.Exists(pngPath3) ? pngPath3 : (File.Exists(pngPath4) ? pngPath4 : null)));

            if (imgPath != null)
            {
                try
                {
                    using var img = Image.FromFile(imgPath);
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

                    float imgAspect = (float)img.Width / img.Height;
                    float boxAspect = (float)previewBox.Width / previewBox.Height;
                    int drawW, drawH;

                    if (imgAspect > boxAspect)
                    {
                        drawW = (int)(previewBox.Width * 0.94f);
                        drawH = (int)(drawW / imgAspect);
                    }
                    else
                    {
                        drawH = (int)(previewBox.Height * 0.94f);
                        drawW = (int)(drawH * imgAspect);
                    }

                    int drawX = (previewBox.Width - drawW) / 2;
                    int drawY = (previewBox.Height - drawH) / 2;

                    e.Graphics.DrawImage(img, new Rectangle(drawX, drawY, drawW, drawH));
                    return;
                }
                catch { }
            }

            paintHandler(s, e);
        };

        card.Controls.Add(previewBox);

        var cardTitleLabel = new Label
        {
            Text = title,
            ForeColor = Color.White,
            Font = CardTitleFont,
            Location = new Point(8, 194),
            AutoSize = true
        };
        card.Controls.Add(cardTitleLabel);

        var descLabel = new Label
        {
            Text = desc,
            ForeColor = Color.FromArgb(165, 165, 185),
            Font = StandardTextFont,
            Location = new Point(8, 216),
            Size = new Size(319, 75)
        };
        card.Controls.Add(descLabel);

        var applyBtn = new Button
        {
            Text = "Применить тему",
            Size = isGifCard ? new Size(220, 36) : new Size(319, 36),
            Location = new Point(8, 300),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(109, 40, 217),
            ForeColor = Color.White,
            Font = StandardBoldFont,
            Cursor = Cursors.Hand
        };
        applyBtn.FlatAppearance.BorderSize = 0;
        applyBtn.Click += async (_, _) => await SelectThemeNativeAsync(themeKey);
        card.Controls.Add(applyBtn);

        if (isGifCard)
        {
            var gifUploadBtn = new Button
            {
                Text = "📁 GIF",
                Size = new Size(91, 36),
                Location = new Point(236, 300),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(255, 0, 127),
                ForeColor = Color.White,
                Font = EmojiButtonFont,
                Cursor = Cursors.Hand
            };
            gifUploadBtn.FlatAppearance.BorderSize = 0;
            gifUploadBtn.Click += async (_, _) => await ChooseCustomGifAsync();
            card.Controls.Add(gifUploadBtn);
        }

        parent.Controls.Add(card);
    }

    private void DrawPlayerCardBase(Graphics g, Rectangle r, Color cardBg, Color coverBorder, Color coverBg, Color titleColor, Color artistColor, Color progColor, bool isVinyl = false, bool isEmerald = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = GetRoundedPath(r, 14))
        using (var fillBrush = new SolidBrush(cardBg))
        using (var borderPen = new Pen(coverBorder, 1.5f))
        {
            g.FillPath(fillBrush, path);
            g.DrawPath(borderPen, path);
        }

        Rectangle coverRect = new Rectangle(r.X + 10, r.Y + 12, 52, 52);
        if (isVinyl)
        {
            using var vinylBrush = new SolidBrush(Color.FromArgb(20, 20, 25));
            using var vinylCenter = new SolidBrush(Color.FromArgb(245, 158, 11));
            using var vinylPen = new Pen(Color.FromArgb(100, 255, 255, 255), 1.5f);
            g.FillEllipse(vinylBrush, coverRect);
            g.DrawEllipse(vinylPen, coverRect);
            Rectangle centerRect = new Rectangle(coverRect.X + 20, coverRect.Y + 20, 12, 12);
            g.FillEllipse(vinylCenter, centerRect);
        }
        else
        {
            int rad = isEmerald ? 4 : 10;
            using var cPath = GetRoundedPath(coverRect, rad);
            using var cBrush = new SolidBrush(coverBg);
            using var cPen = new Pen(coverBorder, 1.5f);
            g.FillPath(cBrush, cPath);
            g.DrawPath(cPen, cPath);
        }

        int textX = r.X + 70;
        using (var tFont = new Font("Segoe UI", 9f, FontStyle.Bold))
        using (var aFont = new Font("Segoe UI", 7.5f, FontStyle.Regular))
        using (var tBrush = new SolidBrush(titleColor))
        using (var aBrush = new SolidBrush(artistColor))
        {
            g.DrawString("Название трека", tFont, tBrush, textX, r.Y + 12);
            g.DrawString("Исполнитель", aFont, aBrush, textX, r.Y + 30);
        }

        int progY = r.Y + 48;
        int progW = r.Width - 82;
        using (var bgPen = new Pen(Color.FromArgb(50, 255, 255, 255), 4))
        using (var fillPen = new Pen(progColor, 4))
        {
            bgPen.StartCap = LineCap.Round; bgPen.EndCap = LineCap.Round;
            fillPen.StartCap = LineCap.Round; fillPen.EndCap = LineCap.Round;

            g.DrawLine(bgPen, textX, progY, textX + progW, progY);
            g.DrawLine(fillPen, textX, progY, textX + (int)(progW * 0.42f), progY);
        }

        using (var timeFont = new Font("Segoe UI", 7f, FontStyle.Regular))
        using (var timeBrush = new SolidBrush(Color.FromArgb(160, 160, 185)))
        {
            g.DrawString("0:53", timeFont, timeBrush, textX, r.Y + 56);
            g.DrawString("2:21", timeFont, timeBrush, textX + progW - 22, r.Y + 56);
        }
    }

    private void DrawNeonPreview(object? sender, PaintEventArgs e)
    {
        Rectangle r = new Rectangle(6, 6, 196, 98);
        DrawPlayerCardBase(e.Graphics, r, Color.FromArgb(240, 15, 2, 35), Color.FromArgb(255, 0, 127), Color.FromArgb(0, 240, 255), Color.FromArgb(255, 0, 127), Color.FromArgb(0, 240, 255), Color.FromArgb(0, 240, 255));
    }

    private void DrawPurplePreview(object? sender, PaintEventArgs e)
    {
        Rectangle r = new Rectangle(6, 6, 196, 98);
        DrawPlayerCardBase(e.Graphics, r, Color.FromArgb(240, 25, 8, 60), Color.FromArgb(192, 132, 252), Color.FromArgb(168, 85, 247), Color.FromArgb(216, 180, 254), Color.FromArgb(192, 132, 252), Color.FromArgb(168, 85, 247));
    }

    private void DrawEmeraldPreview(object? sender, PaintEventArgs e)
    {
        Rectangle r = new Rectangle(6, 6, 196, 98);
        DrawPlayerCardBase(e.Graphics, r, Color.FromArgb(240, 2, 20, 10), Color.FromArgb(52, 211, 153), Color.FromArgb(16, 185, 129), Color.FromArgb(52, 211, 153), Color.FromArgb(167, 243, 208), Color.FromArgb(52, 211, 153), isEmerald: true);
    }

    private void DrawAuroraPreview(object? sender, PaintEventArgs e)
    {
        Rectangle r = new Rectangle(6, 6, 196, 98);
        DrawPlayerCardBase(e.Graphics, r, Color.FromArgb(240, 45, 10, 28), Color.FromArgb(245, 158, 11), Color.FromArgb(239, 68, 68), Color.FromArgb(253, 230, 138), Color.FromArgb(253, 230, 138), Color.FromArgb(245, 158, 11), isVinyl: true);
    }

    private void DrawObsidianPreview(object? sender, PaintEventArgs e)
    {
        Rectangle r = new Rectangle(6, 6, 196, 98);
        DrawPlayerCardBase(e.Graphics, r, Color.FromArgb(240, 6, 8, 20), Color.FromArgb(99, 102, 241), Color.FromArgb(34, 211, 238), Color.FromArgb(34, 211, 238), Color.FromArgb(129, 140, 248), Color.FromArgb(34, 211, 238));
    }

    private void DrawGifPreview(object? sender, PaintEventArgs e)
    {
        Rectangle r = new Rectangle(6, 6, 196, 98);
        DrawPlayerCardBase(e.Graphics, r, Color.FromArgb(240, 28, 5, 25), Color.FromArgb(255, 0, 127), Color.FromArgb(255, 102, 204), Color.FromArgb(255, 153, 221), Color.FromArgb(255, 180, 230), Color.FromArgb(255, 0, 127));

        using var p1 = new SolidBrush(Color.FromArgb(220, 255, 0, 127));
        using var p2 = new SolidBrush(Color.FromArgb(200, 0, 240, 255));
        e.Graphics.FillEllipse(p1, 25, 18, 4, 4);
        e.Graphics.FillEllipse(p2, 140, 22, 5, 5);
        e.Graphics.FillEllipse(p1, 175, 75, 4, 4);
        e.Graphics.FillEllipse(p2, 45, 80, 5, 5);
    }

    private static GraphicsPath GetRoundedPath(Rectangle rect, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = radius * 2;
        Rectangle arc = new Rectangle(rect.X, rect.Y, diameter, diameter);

        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private async Task SelectThemeNativeAsync(string themeKey)
    {
        try
        {
            await _httpClient.GetAsync($"http://localhost:5000/api/theme/set?name={themeKey}");
            if (_webView != null && _webView.CoreWebView2 != null)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync($"if (window.setTheme) window.setTheme('{themeKey}');");
            }
            MessageBox.Show($"Тема «{themeKey}» успешно установлена!", "Установка темы", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при смене темы: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private Task<string?> ShowOpenFileDialogAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "Выберите GIF или фоновое изображение для плеера",
                    Filter = "GIF Анимации (*.gif)|*.gif|Все форматы (*.gif;*.png;*.jpg;*.webp)|*.gif;*.png;*.jpg;*.webp",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    tcs.SetResult(dialog.FileName);
                }
                else
                {
                    tcs.SetResult(null);
                }
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public async Task ChooseCustomGifAsync()
    {
        try
        {
            string? selectedFile = await ShowOpenFileDialogAsync();
            if (string.IsNullOrEmpty(selectedFile)) return;

            byte[] bytes = await File.ReadAllBytesAsync(selectedFile);
            using var content = new ByteArrayContent(bytes);

            try
            {
                await _httpClient.PostAsync("http://localhost:5000/api/gif/upload", content);
            }
            catch
            {
                string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                Directory.CreateDirectory(wwwroot);
                string targetPath = Path.Combine(wwwroot, "custom_bg.gif");
                await File.WriteAllBytesAsync(targetPath, bytes);
                await _httpClient.GetAsync("http://localhost:5000/api/theme/set?name=custom-gif");
            }

            await NavigateToThemeAsync("custom-gif");
            MessageBox.Show("Кастомный GIF фон успешно обновлён!", "Кастомная тема", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            File.AppendAllText("gif_error.log", $"{DateTime.Now}\n{ex}\n");
            MessageBox.Show($"Ошибка при сохранении GIF файла:\n{ex.Message}", "Ошибка GIF", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public async Task NavigateToThemeAsync(string themeName)
    {
        try
        {
            if (_webView != null && _webView.CoreWebView2 != null)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync($"if (window.setTheme) window.setTheme('{themeName}');");
            }
        }
        catch (Exception ex)
        {
            File.AppendAllText("webview_error.log", $"{DateTime.Now} Theme switch error: {ex.Message}\n");
        }
    }

    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
        }
    }

    public void ClosePermanently()
    {
        _isClosingForReal = true;
        Close();
    }

    private static string GetAppDir()
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_isClosingForReal && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            base.OnFormClosing(e);
        }
    }
}
