using System.Globalization;
using System.Numerics;
using System.Text;
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

    // سقف ایمنی برای تعداد صفحه‌های بررسی‌شده در هر نوبت جستجو.
    private const int MaxPages = 200;

    // ---- کنترل‌ها ----
    private readonly TextBox keywordBox = new();
    private readonly TextBox siteBox = new();
    private readonly ComboBox engineBox = new();
    private readonly TextBox repetitionCountBox = new();
    private readonly Button searchButton = new();
    private readonly ToolStripStatusLabel statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly CheckBox privateBox = new();

    private WebView2? webView;
    private CancellationTokenSource? huntCts;
    private bool suppressToggle;
    private bool isPrivateMode;
    private bool isInitializing;

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

        // ترتیب اضافه شدن: نوار وضعیت پایین و هدر بالا قرار می‌گیرد.
        Controls.Add(status);
        Controls.Add(header);

        FormClosing += (_, _) => huntCts?.Cancel();
        Load += async (_, _) => await InitializeApplicationAsync();
    }

    // =====================================================================
    //  طراحی ظاهر
    // =====================================================================
    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 128,
            BackColor = Bg,
            Padding = new Padding(14, 10, 14, 8),
        };

        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Card,
            Padding = new Padding(14, 6, 14, 6),
            RightToLeft = RightToLeft.Yes,
            ColumnCount = 1,
            RowCount = 2,
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        // ردیف اول: کلمه جستجو و سایت هدف
        var queryRow = NewRow(4);
        queryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        queryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        queryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        queryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        StyleInput(keywordBox);
        keywordBox.RightToLeft = RightToLeft.Yes; // نوشتن فارسی راحت‌تر باشد
        keywordBox.PlaceholderText = "اقیانوس";
        keywordBox.KeyDown += (_, e) => OnEnter(e, () => _ = StartSearchAsync());

        StyleInput(siteBox);
        siteBox.PlaceholderText = "wikipedia.org";
        new ToolTip().SetToolTip(siteBox, "الگوی رجکس آدرس سایت هدف. اگر خالی باشد فقط صفحه نتایج جستجو باز می‌شود.");
        siteBox.KeyDown += (_, e) => OnEnter(e, () => _ = StartSearchAsync());

        queryRow.Controls.Add(NewLabel("کلمه جستجو"), 0, 0);
        queryRow.Controls.Add(keywordBox, 1, 0);
        queryRow.Controls.Add(NewLabel("سایت هدف"), 2, 0);
        queryRow.Controls.Add(siteBox, 3, 0);

        // ردیف دوم: موتور، تعداد تکرار، حالت خصوصی و دکمه اجرا
        var optionsRow = NewRow(7);
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));

        engineBox.DropDownStyle = ComboBoxStyle.DropDownList;
        engineBox.Font = new Font("Segoe UI", 11f);
        engineBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        engineBox.Margin = new Padding(4, 4, 4, 4);
        foreach (var engine in Engines) engineBox.Items.Add(engine.Name);
        engineBox.SelectedIndex = 0;

        repetitionCountBox.Text = "1";
        repetitionCountBox.BorderStyle = BorderStyle.FixedSingle;
        repetitionCountBox.ForeColor = TextDark;
        repetitionCountBox.Font = new Font("Segoe UI", 11f);
        repetitionCountBox.TextAlign = HorizontalAlignment.Center;
        repetitionCountBox.RightToLeft = RightToLeft.No;
        repetitionCountBox.Dock = DockStyle.Fill;
        repetitionCountBox.Margin = new Padding(4, 5, 4, 5);
        repetitionCountBox.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && char.GetUnicodeCategory(e.KeyChar) != UnicodeCategory.DecimalDigitNumber)
                e.Handled = true;
        };
        repetitionCountBox.TextChanged += (_, _) => NormalizeRepetitionDigits();
        repetitionCountBox.KeyDown += (_, e) => OnEnter(e, () => _ = StartSearchAsync());
        var repeatTip = new ToolTip();
        repeatTip.SetToolTip(repetitionCountBox, "تعداد اجرای کامل جستجو؛ یک عدد صحیح مثبت وارد کنید. سقف تکرار از پیش تعیین نشده است.");

        privateBox.Text = "خصوصی";
        privateBox.AutoSize = true;
        privateBox.Anchor = AnchorStyles.None;
        privateBox.Font = LabelFont;
        privateBox.ForeColor = TextDark;
        privateBox.Cursor = Cursors.Hand;
        var privateTip = new ToolTip();
        privateTip.SetToolTip(privateBox,
            "حالت InPrivate؛ کوکی، تاریخچه و کش ذخیره نمی‌شود و آی‌پی را تغییر نمی‌دهد. " +
            "در این حالت پیش از هر نوبت جستجو، نشست مرورگر از نو ساخته می‌شود.");
        privateBox.CheckedChanged += async (_, _) => await ChangePrivateModeAsync();

        StylePrimary(searchButton, "جستجو و باز کردن");
        searchButton.Click += (_, _) =>
        {
            if (huntCts is { } currentSearch)
            {
                searchButton.Text = "در حال توقف...";
                searchButton.Enabled = false;
                currentSearch.Cancel();
            }
            else
            {
                _ = StartSearchAsync();
            }
        };

        optionsRow.Controls.Add(NewLabel("موتور"), 0, 0);
        optionsRow.Controls.Add(engineBox, 1, 0);
        optionsRow.Controls.Add(NewLabel("تعداد تکرار"), 2, 0);
        optionsRow.Controls.Add(repetitionCountBox, 3, 0);
        optionsRow.Controls.Add(privateBox, 4, 0);
        optionsRow.Controls.Add(searchButton, 6, 0);

        card.Controls.Add(queryRow, 0, 0);
        card.Controls.Add(optionsRow, 0, 1);
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

    private static void StyleInput(TextBox textBox)
    {
        textBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        textBox.Font = new Font("Segoe UI", 11f);
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.ForeColor = TextDark;
        textBox.RightToLeft = RightToLeft.No;
        textBox.Margin = new Padding(4, 3, 4, 3);
    }

    private static void StylePrimary(Button button, string text)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = AccentDark;
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.Margin = new Padding(4, 3, 4, 3);
    }

    private static void OnEnter(KeyEventArgs e, Action action)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        action();
    }

    private void NormalizeRepetitionDigits()
    {
        var original = repetitionCountBox.Text;
        var normalized = new StringBuilder(original.Length);
        foreach (var character in original)
        {
            if (character is >= '0' and <= '9')
            {
                normalized.Append(character);
            }
            else if (char.GetUnicodeCategory(character) == UnicodeCategory.DecimalDigitNumber)
            {
                normalized.Append((char)('0' + (int)char.GetNumericValue(character)));
            }
        }

        var digits = normalized.ToString();
        if (digits == original) return;

        var cursor = repetitionCountBox.SelectionStart;
        repetitionCountBox.Text = digits;
        repetitionCountBox.SelectionStart = Math.Min(cursor, digits.Length);
    }

    private void SetStatus(string text)
    {
        if (!IsDisposed && !Disposing)
            statusLabel.Text = text;
    }

    private void SetInputControlsEnabled(bool enabled)
    {
        if (IsDisposed || Disposing) return;
        keywordBox.Enabled = enabled;
        siteBox.Enabled = enabled;
        engineBox.Enabled = enabled;
        repetitionCountBox.Enabled = enabled;
        privateBox.Enabled = enabled;
    }

    // =====================================================================
    //  راه‌اندازی برنامه و پارامترهای خط فرمان
    // =====================================================================
    private async Task InitializeApplicationAsync()
    {
        isInitializing = true;
        SetInputControlsEnabled(false);
        searchButton.Enabled = false;

        var args = Environment.GetCommandLineArgs();
        string? Arg(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var startPrivate = args.Contains("--private");
        suppressToggle = true;
        privateBox.Checked = startPrivate;
        suppressToggle = false;

        try
        {
            await InitWebViewAsync(startPrivate);
        }
        catch (Exception ex)
        {
            SetStatus($"راه‌اندازی مرورگر ناموفق بود: {ex.Message}");
            return;
        }
        finally
        {
            isInitializing = false;
            SetInputControlsEnabled(true);
            if (!IsDisposed && !Disposing)
                searchButton.Enabled = true;
        }

        var repeatArgument = Arg("--repeat");
        if (BigInteger.TryParse(repeatArgument, NumberStyles.None, CultureInfo.InvariantCulture, out var requestedCount) &&
            requestedCount >= BigInteger.One)
        {
            repetitionCountBox.Text = requestedCount.ToString(CultureInfo.InvariantCulture);
        }

        // اجرای خودکار از خط فرمان: MiniBrowser.exe --keyword اقیانوس --site wikipedia.org --repeat 3
        var keyword = Arg("--keyword");
        if (keyword != null)
        {
            keywordBox.Text = keyword;
            siteBox.Text = Arg("--site") ?? "";
            await StartSearchAsync();
        }
    }

    // =====================================================================
    //  تغییر حالت مرورگر
    // =====================================================================
    private async Task ChangePrivateModeAsync()
    {
        if (suppressToggle || isInitializing) return;
        if (huntCts != null)
        {
            suppressToggle = true;
            privateBox.Checked = isPrivateMode;
            suppressToggle = false;
            return;
        }

        var requestedMode = privateBox.Checked;
        var previousMode = isPrivateMode;
        SetInputControlsEnabled(false);
        searchButton.Enabled = false;
        SetStatus(requestedMode ? "در حال فعال‌سازی حالت خصوصی..." : "در حال برگشت به حالت عادی...");

        try
        {
            await InitWebViewAsync(requestedMode);
            SetStatus(requestedMode ? "حالت خصوصی (InPrivate) فعال است" : "حالت عادی فعال است");
        }
        catch (Exception ex)
        {
            suppressToggle = true;
            privateBox.Checked = previousMode;
            suppressToggle = false;
            SetStatus($"تغییر حالت مرورگر ناموفق بود: {ex.Message}");
        }
        finally
        {
            SetInputControlsEnabled(true);
            if (!IsDisposed && !Disposing)
                searchButton.Enabled = true;
        }
    }

    // =====================================================================
    //  ساخت WebView2
    // =====================================================================
    private async Task InitWebViewAsync(bool inPrivate, bool resetCurrent = false)
    {
        var old = webView;
        if (resetCurrent)
        {
            // در حالت خصوصی، کنترل قبلی قبل از ساخت نشست بعدی بسته می‌شود تا داده‌ای بین
            // دو نوبت جستجو باقی نماند.
            DisposeWebView(old);
            webView = null;
        }

        var fresh = new WebView2 { Dock = DockStyle.Fill };
        try
        {
            var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = inPrivate;

            Controls.Add(fresh);
            Controls.SetChildIndex(fresh, 0); // WebView باید تمام فضای باقی‌مانده را بگیرد
            await fresh.EnsureCoreWebView2Async(environment, options);

            webView = fresh;
            isPrivateMode = inPrivate;
            if (!resetCurrent)
                DisposeWebView(old);
        }
        catch
        {
            DisposeWebView(fresh);
            if (resetCurrent)
                webView = null;
            throw;
        }
    }

    private static void DisposeWebView(WebView2? view)
    {
        if (view == null || view.IsDisposed) return;
        view.Parent?.Controls.Remove(view);
        view.Dispose();
    }

    // =====================================================================
    //  اجرای یک یا چند نوبت جستجو
    // =====================================================================
    private async Task StartSearchAsync()
    {
        if (huntCts != null) return;

        var keyword = keywordBox.Text.Trim();
        var site = siteBox.Text.Trim();
        if (keyword.Length == 0)
        {
            SetStatus("لطفاً کلمه جستجو را وارد کنید");
            return;
        }
        if (!BigInteger.TryParse(repetitionCountBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var repetitions) ||
            repetitions < BigInteger.One)
        {
            SetStatus("تعداد تکرار باید یک عدد صحیح مثبت باشد");
            return;
        }
        if (webView?.CoreWebView2 == null)
        {
            SetStatus("مرورگر هنوز آماده نیست");
            return;
        }
        if (engineBox.SelectedIndex < 0 || engineBox.SelectedIndex >= Engines.Length)
        {
            SetStatus("لطفاً یک موتور جستجو انتخاب کنید");
            return;
        }
        if (site.Length > 0)
        {
            try { _ = new System.Text.RegularExpressions.Regex(site); }
            catch (ArgumentException)
            {
                SetStatus("الگوی سایت هدف (رجکس) معتبر نیست");
                return;
            }
        }

        var engine = Engines[engineBox.SelectedIndex];
        var usePrivateMode = privateBox.Checked;
        var cts = new CancellationTokenSource();
        huntCts = cts;
        var ct = cts.Token;
        var completed = BigInteger.Zero;
        var aborted = false;
        var lastSummary = "";

        SetInputControlsEnabled(false);
        searchButton.Text = "توقف";
        searchButton.BackColor = Danger;
        searchButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(185, 28, 28);
        searchButton.Enabled = true;

        try
        {
            for (var attempt = BigInteger.One; attempt <= repetitions; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (usePrivateMode)
                {
                    SetStatus($"در حال پاک‌سازی نشست خصوصی برای جستجوی {attempt} از {repetitions}...");
                    await InitWebViewAsync(inPrivate: true, resetCurrent: true);
                }
                ct.ThrowIfCancellationRequested();

                var result = await RunSearchAttemptAsync(keyword, site, engine, attempt, repetitions, ct);
                ct.ThrowIfCancellationRequested();
                completed = attempt;
                lastSummary = result.Summary;
                if (!result.ContinueBatch)
                {
                    aborted = true;
                    break;
                }
            }

            if (!aborted)
                SetStatus($"تکمیل شد: {completed} از {repetitions} جستجو. {lastSummary}");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"جستجو متوقف شد؛ {completed} از {repetitions} نوبت کامل شد");
        }
        catch (Exception ex)
        {
            SetStatus($"خطا در اجرای جستجو: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(huntCts, cts))
                huntCts = null;
            cts.Dispose();

            SetInputControlsEnabled(true);
            if (!IsDisposed && !Disposing)
            {
                searchButton.Text = "جستجو و باز کردن";
                searchButton.BackColor = Accent;
                searchButton.FlatAppearance.MouseOverBackColor = AccentDark;
                searchButton.Enabled = true;
            }
        }
    }

    private sealed record SearchAttemptResult(bool ContinueBatch, string Summary);

    private async Task<SearchAttemptResult> RunSearchAttemptAsync(
        string keyword,
        string site,
        Engine engine,
        BigInteger attempt,
        BigInteger repetitions,
        CancellationToken ct)
    {
        var prefix = $"جستجوی {attempt} از {repetitions}";
        var query = Uri.EscapeDataString(keyword);
        var core = webView?.CoreWebView2;
        if (core == null)
        {
            var unavailable = $"{prefix}: مرورگر آماده نیست";
            SetStatus(unavailable);
            return new SearchAttemptResult(false, unavailable);
        }

        if (site.Length == 0)
        {
            SetStatus($"{prefix}: جستجوی «{keyword}» در {engine.Name}...");
            var loaded = await NavigateAndWaitAsync(engine.PageUrl(query, 0), ct);
            var summary = loaded
                ? $"جستجوی «{keyword}» در {engine.Name} باز شد"
                : "بارگذاری صفحه نتایج ناموفق بود";
            SetStatus($"{prefix}: {summary}");
            return new SearchAttemptResult(true, summary);
        }

        for (var page = 0; page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            SetStatus($"{prefix} — «{keyword}» — بررسی صفحه {page + 1}...");

            if (page == 0 || engine.Name != "DuckDuckGo")
            {
                if (!await NavigateAndWaitAsync(engine.PageUrl(query, page), ct))
                {
                    var loadFailure = $"{prefix}: بارگذاری صفحه نتایج ناموفق بود";
                    SetStatus(loadFailure);
                    return new SearchAttemptResult(true, loadFailure);
                }
            }
            else
            {
                // DuckDuckGo صفحه‌بندی معمولی ندارد؛ دکمه «نتایج بیشتر» را می‌زنیم.
                await core.ExecuteScriptAsync("document.querySelector('#more-results, button[id*=more]')?.click()");
            }

            await Task.Delay(1200, ct); // صبر برای رندر شدن نتایج

            // قدم‌به‌قدم صفحه را پایین می‌رویم و در هر قدم نتایج را بررسی می‌کنیم.
            var found = new ScanResult(false, "", "", 0, null);
            for (var step = 0; step < 15; step++)
            {
                ct.ThrowIfCancellationRequested();
                found = await ScanResultsAsync(site);
                if (found.Error != null)
                {
                    var scanFailure = $"{prefix}: {found.Error}";
                    SetStatus(scanFailure);
                    return new SearchAttemptResult(false, scanFailure);
                }
                if (found.Found) break;

                var atBottom = await core.ExecuteScriptAsync(
                    "(window.innerHeight + window.scrollY) >= document.documentElement.scrollHeight - 5");
                if (atBottom == "true") break;

                await core.ExecuteScriptAsync(
                    "window.scrollBy({top: Math.round(window.innerHeight * 0.4), behavior: 'smooth'})");
                await Task.Delay(700, ct);
            }

            if (!found.Found)
            {
                var source = webView?.Source?.ToString() ?? "";
                if (source.Contains("/sorry/") || source.Contains("captcha", StringComparison.OrdinalIgnoreCase))
                {
                    var captcha = "موتور جستجو درخواست تایید هویت (کپچا) داد؛ تکرار جستجو متوقف شد";
                    SetStatus($"{prefix}: {captcha}");
                    return new SearchAttemptResult(false, captcha);
                }
                if (found.Seen == 0)
                {
                    var noResults = $"در صفحه {page + 1} نتیجه قابل‌خواندن پیدا نشد";
                    SetStatus($"{prefix}: {noResults}");
                    return new SearchAttemptResult(true, noResults);
                }
                continue;
            }

            SetStatus($"{prefix}: سایت هدف در صفحه {page + 1} پیدا شد — {found.Text}");
            await core.ExecuteScriptAsync(
                "document.querySelector('[data-mb-hit]')?.scrollIntoView({block:'center',behavior:'smooth'})");
            await Task.Delay(900, ct);

            if (!Uri.TryCreate(found.Url, UriKind.Absolute, out var targetUri) ||
                (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
            {
                var invalidTarget = "نشانی نتیجه پیدا شد، اما قابل باز کردن نیست";
                SetStatus($"{prefix}: {invalidTarget}");
                return new SearchAttemptResult(true, invalidTarget);
            }

            // رفتن مستقیم به href، پنجره‌های جدید احتمالی موتور جستجو را هم دور می‌زند.
            if (!await NavigateAndWaitAsync(targetUri.ToString(), ct))
            {
                var targetFailure = $"بارگذاری سایت پیدا‌شده ناموفق بود: {found.Text}";
                SetStatus($"{prefix}: {targetFailure}");
                return new SearchAttemptResult(true, targetFailure);
            }

            SetStatus($"{prefix}: باز شد — {found.Text} — در حال مرور سایت...");
            await BrowseSiteAsync(ct);
            var opened = $"سایت مطابق «{site}» باز شد: {found.Text}";
            SetStatus($"{prefix}: {opened}");
            return new SearchAttemptResult(true, opened);
        }

        var exhausted = $"در {MaxPages} صفحه، سایتی مطابق «{site}» پیدا نشد";
        SetStatus($"{prefix}: {exhausted}");
        return new SearchAttemptResult(true, exhausted);
    }

    private async Task<bool> NavigateAndWaitAsync(string url, CancellationToken ct)
    {
        var core = webView?.CoreWebView2;
        if (core == null) return false;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // DOMContentLoaded زودتر از NavigationCompleted می‌آید؛ موتور ممکن است منابع جانبی را دیرتر لود کند.
        void Handler(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2DOMContentLoadedEventArgs e) =>
            completion.TrySetResult(true);

        core.DOMContentLoaded += Handler;
        try
        {
            core.Navigate(url);
            using var registration = ct.Register(() => completion.TrySetCanceled(ct));
            var finished = await Task.WhenAny(completion.Task, Task.Delay(20000, ct));
            ct.ThrowIfCancellationRequested();
            if (finished != completion.Task) return false;
            return await completion.Task;
        }
        finally
        {
            core.DOMContentLoaded -= Handler;
        }
    }

    // بعد از ورود به سایت: کمی پایین و سپس بالا اسکرول می‌کند.
    private async Task BrowseSiteAsync(CancellationToken ct)
    {
        var core = webView?.CoreWebView2;
        if (core == null) return;

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

    private sealed record ScanResult(bool Found, string Text, string Url, int Seen, string? Error);

    // نتیجه‌ای را پیدا می‌کند که آدرس نمایش‌داده‌شده‌اش با رجکس بخواند.
    private async Task<ScanResult> ScanResultsAsync(string pattern)
    {
        var core = webView?.CoreWebView2;
        if (core == null)
            return new ScanResult(false, "", "", 0, "مرورگر آماده نیست");

        var script = """
        (() => {
          let re; try { re = new RegExp(__PATTERN__, 'i'); } catch (e) { return { error: 'الگوی رجکس نامعتبر است' }; }
          const engineHost = /(^|\.)(google|bing|duckduckgo)\./i;
          let hit = null, text = '', targetUrl = '', seen = 0;
          for (const a of document.querySelectorAll('a[href]')) {
            let u; try { u = new URL(a.href); } catch (e) { continue; }
            if (!/^https?:$/.test(u.protocol)) continue;
            let probe = null;
            if (!engineHost.test(u.hostname)) probe = u.hostname + u.pathname;
            else { const c = a.querySelector('cite'); if (c) probe = c.parentElement.innerText || c.innerText; }
            if (!probe) continue;
            seen++;
            if (re.test(probe)) { hit = a; text = probe.replace(/\s+/g, ' ').trim(); targetUrl = u.href; break; }
          }
          document.querySelectorAll('[data-mb-hit]').forEach(e => e.removeAttribute('data-mb-hit'));
          if (hit) hit.setAttribute('data-mb-hit', '1');
          return { found: !!hit, text, url: targetUrl, seen };
        })()
        """.Replace("__PATTERN__", JsonSerializer.Serialize(pattern));

        var raw = await core.ExecuteScriptAsync(script);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
            return new ScanResult(false, "", "", 0, error.GetString());

        return new ScanResult(
            root.GetProperty("found").GetBoolean(),
            root.GetProperty("text").GetString() ?? "",
            root.GetProperty("url").GetString() ?? "",
            root.GetProperty("seen").GetInt32(),
            null);
    }
}
