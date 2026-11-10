using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

namespace MiniBrowser;

public class MainForm : Form
{
    // ---- رنگ‌ها و فونت‌ها ----
    private static readonly Color Bg = Color.FromArgb(243, 244, 246);
    private static readonly Color Card = Color.White;
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color AccentDark = Color.FromArgb(29, 78, 216);
    private static readonly Color Danger = Color.FromArgb(220, 38, 38);
    private static readonly Color TextDark = Color.FromArgb(31, 41, 55);
    private static readonly Color TextMuted = Color.FromArgb(107, 114, 128);
    private static readonly Font UiFont = new("Segoe UI", 10f);
    private static readonly Font LabelFont = new("Segoe UI", 9f, FontStyle.Bold);

    // ---- موتورهای جستجو ----
    private sealed record Engine(string Name, Func<string, int, string> PageUrl);
    private static readonly Engine[] Engines =
    {
        new("Google",     (q, p) => $"https://www.google.com/search?q={q}&start={p * 10}"),
        new("Bing",       (q, p) => $"https://www.bing.com/search?q={q}&first={p * 10 + 1}"),
        new("DuckDuckGo", (q, p) => $"https://duckduckgo.com/?q={q}"),
    };

    // سقف ایمنی؛ عملاً تا آخرین صفحه نتایج می‌گردد (موتورها معمولاً بعد از چند ده صفحه نتیجه نمی‌دهند)
    private const int MaxPages = 200;

    // ---- کنترل‌ها ----
    private readonly TextBox keywordBox = new();
    private readonly TextBox siteBox = new();
    private readonly ComboBox engineBox = new();
    private readonly Button searchButton = new();
    private readonly ToolStripStatusLabel statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly CheckBox privateBox = new();
    private WebView2 webView = new();

    private CancellationTokenSource? huntCts;
    private bool suppressToggle;

    public MainForm()
    {
        Text = "Mini Browser";
        Width = 1200;
        Height = 800;
        MinimumSize = new Size(800, 500);
        BackColor = Bg;
        Font = UiFont;

        var header = BuildHeader();

        var status = new StatusStrip { BackColor = Card, SizingGrip = false };
        statusLabel.ForeColor = TextMuted;
        statusLabel.Text = "آماده";
        status.Items.Add(statusLabel);

        // ترتیب اضافه شدن: آخرین Dock بالاتر/بیرونی‌تر قرار می‌گیرد
        Controls.Add(status);
        Controls.Add(header);

        Load += async (_, _) =>
        {
            var startPrivate = Environment.GetCommandLineArgs().Contains("--private");
            suppressToggle = true; privateBox.Checked = startPrivate; suppressToggle = false;
            await InitWebViewAsync(startPrivate);

            // اجرای خودکار از خط فرمان: MiniBrowser.exe --keyword اقیانوس --site wikipedia.org
            var args = Environment.GetCommandLineArgs();
            string? Arg(string name)
            {
                var i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            var kw = Arg("--keyword");
            if (kw != null)
            {
                keywordBox.Text = kw;
                siteBox.Text = Arg("--site") ?? "";
                await StartSearchAsync();
            }
        };
    }

    // =====================================================================
    //  طراحی ظاهر
    // =====================================================================
    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = Bg,
            Padding = new Padding(14, 12, 14, 8),
        };

        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Card,
            Padding = new Padding(14, 10, 14, 10),
            RightToLeft = RightToLeft.Yes,
            ColumnCount = 1,
            RowCount = 1,
        };
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ----- ردیف جستجو: کلمه جستجو + سایت هدف + موتور + دکمه -----
        var row2 = NewRow(7);
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));

        StyleInput(keywordBox);
        keywordBox.RightToLeft = RightToLeft.Yes; // نوشتن فارسی راحت‌تر باشد
        keywordBox.PlaceholderText = "اقیانوس";
        keywordBox.KeyDown += (_, e) => OnEnter(e, () => _ = StartSearchAsync());

        StyleInput(siteBox);
        siteBox.PlaceholderText = "wikipedia.org";
        new ToolTip().SetToolTip(siteBox, "رجکس آدرس سایت هدف. اگر خالی باشد فقط نتایج جستجو نمایش داده می‌شود.");
        siteBox.KeyDown += (_, e) => OnEnter(e, () => _ = StartSearchAsync());

        engineBox.DropDownStyle = ComboBoxStyle.DropDownList;
        engineBox.Font = new Font("Segoe UI", 11f);
        engineBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        engineBox.Margin = new Padding(4, 3, 4, 3);
        foreach (var e in Engines) engineBox.Items.Add(e.Name);
        engineBox.SelectedIndex = 0;

        StylePrimary(searchButton, "جستجو و باز کردن");
        searchButton.Click += (_, _) =>
        {
            if (huntCts != null) huntCts.Cancel();
            else _ = StartSearchAsync();
        };

        row2.Controls.Add(NewLabel("کلمه جستجو"), 0, 0);
        row2.Controls.Add(keywordBox, 1, 0);
        row2.Controls.Add(NewLabel("سایت هدف"), 2, 0);
        row2.Controls.Add(siteBox, 3, 0);
        row2.Controls.Add(engineBox, 4, 0);
        privateBox.Text = "خصوصی";
        privateBox.AutoSize = true;
        privateBox.Anchor = AnchorStyles.None;
        privateBox.Font = LabelFont;
        privateBox.ForeColor = TextDark;
        privateBox.Cursor = Cursors.Hand;
        new ToolTip().SetToolTip(privateBox, "مرور InPrivate: کوکی، تاریخچه و کش ذخیره نمی‌شود (آیپی تغییر نمی‌کند)");
        privateBox.CheckedChanged += async (_, _) =>
        {
            if (suppressToggle) return;
            huntCts?.Cancel();
            SetStatus(privateBox.Checked ? "در حال فعال‌سازی حالت خصوصی..." : "در حال برگشت به حالت عادی...");
            await InitWebViewAsync(privateBox.Checked);
            SetStatus(privateBox.Checked ? "حالت خصوصی (InPrivate) فعال است" : "حالت عادی");
        };

        row2.Controls.Add(privateBox, 5, 0);
        row2.Controls.Add(searchButton, 6, 0);

        card.Controls.Add(row2, 0, 0);
        header.Controls.Add(card);
        return header;
    }

    private static TableLayoutPanel NewRow(int cols) => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = cols,
        RowCount = 1,
        RightToLeft = RightToLeft.Yes,
        Margin = new Padding(0),
        BackColor = Card,
    };

    private static Label NewLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = LabelFont,
        ForeColor = TextMuted,
        Margin = new Padding(8, 0, 8, 0),
    };

    private static void StyleInput(TextBox t)
    {
        t.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        t.Font = new Font("Segoe UI", 11f);
        t.BorderStyle = BorderStyle.FixedSingle;
        t.ForeColor = TextDark;
        t.RightToLeft = RightToLeft.No;
        t.Margin = new Padding(4, 3, 4, 3);
    }

    private static void StylePrimary(Button b, string text)
    {
        b.Text = text;
        b.Dock = DockStyle.Fill;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentDark;
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
        b.Margin = new Padding(4, 3, 4, 3);
    }

    private static void OnEnter(KeyEventArgs e, Action action)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        action();
    }

    private void SetStatus(string text) => statusLabel.Text = text;

    // =====================================================================
    //  ساخت WebView2؛ حالت InPrivate باید هنگام ساخت مشخص شود، پس با تغییر حالت دوباره ساخته می‌شود
    // =====================================================================
    private async Task InitWebViewAsync(bool inPrivate)
    {
        var old = webView;
        var fresh = new WebView2 { Dock = DockStyle.Fill };

        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync();
        var options = env.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = inPrivate;

        Controls.Add(fresh);
        Controls.SetChildIndex(fresh, 0); // Fill باید آخر چیده شود
        await fresh.EnsureCoreWebView2Async(env, options);

        webView = fresh;
        if (old.Parent != null) { Controls.Remove(old); old.Dispose(); }
    }

    // =====================================================================
    //  جستجو: کلمه + سایت هدف (رجکس). اگر سایت هدف خالی باشد فقط نتایج نمایش داده می‌شود.
    // =====================================================================
    private async Task StartSearchAsync()
    {
        var keyword = keywordBox.Text.Trim();
        var site = siteBox.Text.Trim();
        if (keyword.Length == 0 || webView.CoreWebView2 == null || huntCts != null) return;

        var engine = Engines[engineBox.SelectedIndex];
        var q = Uri.EscapeDataString(keyword);

        // فقط جستجو
        if (site.Length == 0)
        {
            SetStatus($"جستجوی «{keyword}» در {engine.Name}");
            webView.CoreWebView2.Navigate(engine.PageUrl(q, 0));
            return;
        }

        try { _ = new System.Text.RegularExpressions.Regex(site); }
        catch { SetStatus("سایت هدف (رجکس) معتبر نیست"); return; }

        huntCts = new CancellationTokenSource();
        var ct = huntCts.Token;
        searchButton.Text = "توقف";
        searchButton.BackColor = Danger;
        searchButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(185, 28, 28);

        try
        {
            for (var page = 0; page < MaxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                SetStatus($"جستجوی «{keyword}» — بررسی صفحه {page + 1}...");

                if (page == 0 || engine.Name != "DuckDuckGo")
                {
                    if (!await NavigateAndWaitAsync(engine.PageUrl(q, page), ct))
                    {
                        SetStatus("بارگذاری صفحه نتایج ناموفق بود");
                        return;
                    }
                }
                else
                {
                    // DuckDuckGo صفحه‌بندی ندارد؛ دکمه «نتایج بیشتر» را می‌زنیم
                    await webView.CoreWebView2.ExecuteScriptAsync(
                        "document.querySelector('#more-results, button[id*=more]')?.click()");
                }
                await Task.Delay(1200, ct); // صبر برای رندر شدن نتایج

                // قدم‌به‌قدم صفحه را پایین می‌رویم و در هر قدم نتایج را بررسی می‌کنیم
                var found = new ScanResult(false, "", 0, null);
                for (var step = 0; step < 15; step++)
                {
                    ct.ThrowIfCancellationRequested();
                    found = await ScanResultsAsync(site);
                    if (found.Error != null) { SetStatus(found.Error); return; }
                    if (found.Found) break;

                    var atBottom = await webView.CoreWebView2.ExecuteScriptAsync(
                        "(window.innerHeight + window.scrollY) >= document.documentElement.scrollHeight - 5");
                    if (atBottom == "true") break;

                    await webView.CoreWebView2.ExecuteScriptAsync(
                        "window.scrollBy({top: Math.round(window.innerHeight * 0.4), behavior: 'smooth'})");
                    await Task.Delay(700, ct);
                }
                if (!found.Found)
                {
                    var src = webView.Source?.ToString() ?? "";
                    if (src.Contains("/sorry/") || src.Contains("captcha", StringComparison.OrdinalIgnoreCase))
                    {
                        SetStatus("موتور جستجو درخواست تایید هویت (کپچا) داد؛ جستجو متوقف شد");
                        return;
                    }
                    if (found.Seen == 0)
                    {
                        SetStatus($"نتایج تمام شد؛ در {page + 1} صفحه سایتی مطابق «{site}» پیدا نشد");
                        return;
                    }
                    continue;
                }

                SetStatus($"پیدا شد در صفحه {page + 1}: {found.Text}");
                await webView.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('[data-mb-hit]')?.scrollIntoView({block:'center',behavior:'smooth'})");
                await Task.Delay(900, ct);

                // کلیک و صبر برای لود شدن سایت مقصد
                var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnLoaded(object? s, Microsoft.Web.WebView2.Core.CoreWebView2DOMContentLoadedEventArgs e) => loaded.TrySetResult(true);
                webView.CoreWebView2.DOMContentLoaded += OnLoaded;
                try
                {
                    await webView.CoreWebView2.ExecuteScriptAsync(
                        "document.querySelector('[data-mb-hit]')?.click()");
                    await Task.WhenAny(loaded.Task, Task.Delay(15000, ct));
                }
                finally { webView.CoreWebView2.DOMContentLoaded -= OnLoaded; }

                SetStatus($"باز شد: {found.Text} — در حال مرور سایت...");
                await BrowseSiteAsync(ct);
                SetStatus($"باز شد: {found.Text}");
                return;
            }
            SetStatus($"در {MaxPages} صفحه نتایج، سایتی مطابق «{site}» پیدا نشد");
        }
        catch (OperationCanceledException)
        {
            SetStatus("متوقف شد");
        }
        finally
        {
            huntCts?.Dispose();
            huntCts = null;
            searchButton.Text = "جستجو و باز کردن";
            searchButton.BackColor = Accent;
            searchButton.FlatAppearance.MouseOverBackColor = AccentDark;
        }
    }

    private async Task<bool> NavigateAndWaitAsync(string url, CancellationToken ct)
    {
        var core = webView.CoreWebView2;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // DOMContentLoaded زودتر از NavigationCompleted می‌آید (گوگل تا چند ثانیه منابع جانبی لود می‌کند)
        void Handler(object? s, Microsoft.Web.WebView2.Core.CoreWebView2DOMContentLoadedEventArgs e) =>
            tcs.TrySetResult(true);

        core.DOMContentLoaded += Handler;
        try
        {
            core.Navigate(url);
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            var done = await Task.WhenAny(tcs.Task, Task.Delay(20000, ct));
            return done == tcs.Task && await tcs.Task;
        }
        finally { core.DOMContentLoaded -= Handler; }
    }

        // بعد از ورود به سایت: کمی پایین و سپس بالا اسکرول می‌کند
    private async Task BrowseSiteAsync(CancellationToken ct)
    {
        var core = webView.CoreWebView2;
        await Task.Delay(1500, ct);
        for (var i = 0; i < 8; i++)
        {
            await core.ExecuteScriptAsync("window.scrollBy({top: Math.round(window.innerHeight * 0.6), behavior: 'smooth'})");
            await Task.Delay(800, ct);
            var atBottom = await core.ExecuteScriptAsync(
                "(window.innerHeight + window.scrollY) >= document.documentElement.scrollHeight - 5");
            if (atBottom == "true") break;
        }
        await Task.Delay(600, ct);
        for (var i = 0; i < 8; i++)
        {
            await core.ExecuteScriptAsync("window.scrollBy({top: -Math.round(window.innerHeight * 0.6), behavior: 'smooth'})");
            await Task.Delay(700, ct);
            var atTop = await core.ExecuteScriptAsync("window.scrollY <= 5");
            if (atTop == "true") break;
        }
    }

    private sealed record ScanResult(bool Found, string Text, int Seen, string? Error);

    // نتیجه‌ای را پیدا می‌کند که آدرس نمایش‌داده‌شده‌اش با رجکس بخواند
    private async Task<ScanResult> ScanResultsAsync(string pattern)
    {
        var js = """
        (() => {
          let re; try { re = new RegExp(__PATTERN__, 'i'); } catch (e) { return { error: 'رجکس نامعتبر است' }; }
          const engineHost = /(^|\.)(google|bing|duckduckgo)\./i;
          let hit = null, text = '', seen = 0;
          for (const a of document.querySelectorAll('a[href]')) {
            let u; try { u = new URL(a.href); } catch (e) { continue; }
            if (!/^https?:$/.test(u.protocol)) continue;
            let probe = null;
            if (!engineHost.test(u.hostname)) probe = u.hostname + u.pathname;
            else { const c = a.querySelector('cite'); if (c) probe = c.parentElement.innerText || c.innerText; }
            if (!probe) continue;
            seen++;
            if (re.test(probe)) { hit = a; text = probe.replace(/\s+/g, ' ').trim(); break; }
          }
          document.querySelectorAll('[data-mb-hit]').forEach(e => e.removeAttribute('data-mb-hit'));
          if (hit) hit.setAttribute('data-mb-hit', '1');
          return { found: !!hit, text, seen };
        })()
        """.Replace("__PATTERN__", JsonSerializer.Serialize(pattern));

        var raw = await webView.CoreWebView2.ExecuteScriptAsync(js);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err)) return new ScanResult(false, "", 0, err.GetString());
        return new ScanResult(root.GetProperty("found").GetBoolean(), root.GetProperty("text").GetString() ?? "", root.GetProperty("seen").GetInt32(), null);
    }
}
