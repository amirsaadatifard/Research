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
        new("Google", (q, p) => $"https://www.google.com/search?q={q}&start={p * 10}"),
    };

    private const string DefaultProductQuery = "شمش نقره یاکاموز 100 گرم عیار 999";
    private const string DefaultSellerName = "زرین شمش سهند";
    private const int MaxPages = 200;
    private const int MaxTorobCandidates = 12;
    private const int MaxProxyRetriesPerSearch = 3;
    private const int ProxyRotationInterval = 10;

    // ---- کنترل‌ها ----
    private readonly TextBox keywordBox = new();
    private readonly TextBox siteBox = new();
    private readonly ComboBox engineBox = new();
    private readonly TextBox repetitionCountBox = new();
    private readonly Button searchButton = new();
    private readonly ToolStripStatusLabel statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly CheckBox privateBox = new();
    private readonly CheckBox proxyModeBox = new();
    private readonly Button fetchProxiesButton = new();
    private readonly Label proxyStatusLabel = new();
    private readonly ProxyManager proxyManager = new();
    private readonly List<ProxyEndpoint> proxyPool = new();

    private WebView2? webView;
    private CancellationTokenSource? huntCts;
    private bool suppressToggle;
    private bool suppressProxyToggle;
    private bool isPrivateMode;
    private bool isProxyModeActive;
    private bool isFetchingProxies;
    private bool isInitializing;
    private int currentProxyIndex;
    private int searchesOnCurrentProxy;
    private string? activeProxyAddress;

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
            Height = 174,
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
            RowCount = 3,
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34f));

        // ردیف اول: کلمه جستجو و سایت هدف
        var queryRow = NewRow(4);
        queryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        queryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        queryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        queryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        StyleInput(keywordBox);
        keywordBox.RightToLeft = RightToLeft.Yes;
        keywordBox.Text = DefaultProductQuery;
        keywordBox.PlaceholderText = DefaultProductQuery;
        keywordBox.KeyDown += (_, e) => OnEnter(e, () => _ = StartSearchAsync());

        StyleInput(siteBox);
        siteBox.RightToLeft = RightToLeft.Yes;
        siteBox.Text = DefaultSellerName;
        siteBox.PlaceholderText = DefaultSellerName;
        new ToolTip().SetToolTip(siteBox, "فقط لینک خریدی باز می‌شود که نام این فروشنده را دقیقاً داشته باشد.");
        siteBox.KeyDown += (_, e) => OnEnter(e, () => _ = StartSearchAsync());

        queryRow.Controls.Add(NewLabel("عبارت محصول"), 0, 0);
        queryRow.Controls.Add(keywordBox, 1, 0);
        queryRow.Controls.Add(NewLabel("فروشنده دقیق"), 2, 0);
        queryRow.Controls.Add(siteBox, 3, 0);

        // ردیف دوم: Google، تعداد تکرار، حالت خصوصی و دکمه اجرا
        var optionsRow = NewRow(7);
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));

        engineBox.DropDownStyle = ComboBoxStyle.DropDownList;
        engineBox.Font = new Font("Segoe UI", 11f);
        engineBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        engineBox.Margin = new Padding(4, 4, 4, 4);
        foreach (var engine in Engines) engineBox.Items.Add(engine.Name);
        engineBox.SelectedIndex = 0;
        engineBox.Enabled = false; // جستجوی محصول Torob همیشه از Google شروع می‌شود.

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

        StylePrimary(searchButton, "پیدا کردن در ترب و باز کردن فروشنده");
        var searchTip = new ToolTip();
        searchTip.SetToolTip(searchButton,
            "از Google وارد Torob می‌شود، محصول و نام فروشنده را بررسی می‌کند و فقط صفحهٔ فروشنده را باز می‌کند؛ " +
            "خرید، افزودن به سبد یا پرداخت انجام نمی‌شود.");
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

        var proxyRow = NewRow(3);
        proxyRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        proxyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        proxyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        proxyModeBox.Text = "استفاده از پروکسی";
        proxyModeBox.AutoSize = true;
        proxyModeBox.Anchor = AnchorStyles.None;
        proxyModeBox.Font = LabelFont;
        proxyModeBox.ForeColor = TextDark;
        proxyModeBox.Cursor = Cursors.Hand;
        var proxyTip = new ToolTip();
        proxyTip.SetToolTip(proxyModeBox,
            "پروکسی‌های عمومی ممکن است کند یا ناامن باشند. هرگز برای حساب‌ها یا اطلاعات حساس استفاده نکنید. " +
            "پروکسی فعال پس از هر ۱۰ نوبت جستجو عوض می‌شود.");
        proxyModeBox.CheckedChanged += async (_, _) => await ChangeProxyModeAsync();

        StylePrimary(fetchProxiesButton, "دریافت و بررسی");
        fetchProxiesButton.BackColor = Color.FromArgb(75, 85, 99);
        fetchProxiesButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 65, 81);
        fetchProxiesButton.Click += async (_, _) => await LoadAndTestProxiesAsync();
        proxyTip.SetToolTip(fetchProxiesButton,
            $"دریافت فهرست عمومی از {ProxyManager.SourceUrl} و آزمایش دسترسی HTTPS به Google و Torob.");

        proxyStatusLabel.Text = "پروکسی‌ها هنوز دریافت نشده‌اند";
        proxyStatusLabel.AutoEllipsis = true;
        proxyStatusLabel.Dock = DockStyle.Fill;
        proxyStatusLabel.TextAlign = ContentAlignment.MiddleRight;
        proxyStatusLabel.Font = UiFont;
        proxyStatusLabel.ForeColor = TextMuted;
        proxyStatusLabel.Margin = new Padding(8, 0, 8, 0);

        proxyRow.Controls.Add(proxyModeBox, 0, 0);
        proxyRow.Controls.Add(fetchProxiesButton, 1, 0);
        proxyRow.Controls.Add(proxyStatusLabel, 2, 0);

        card.Controls.Add(queryRow, 0, 0);
        card.Controls.Add(optionsRow, 0, 1);
        card.Controls.Add(proxyRow, 0, 2);
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
        engineBox.Enabled = false; // موتور این گردش‌کار ثابت و روی Google است.
        repetitionCountBox.Enabled = enabled;
        privateBox.Enabled = enabled;
        proxyModeBox.Enabled = enabled && proxyPool.Count > 0;
        fetchProxiesButton.Enabled = enabled && !proxyModeBox.Checked;
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

        // اجرای خودکار از خط فرمان: MiniBrowser.exe --keyword "شمش نقره یاکاموز 100 گرم" --seller "زرین شمش سهند" --repeat 3
        var keyword = Arg("--keyword");
        if (keyword != null)
        {
            keywordBox.Text = keyword;
            siteBox.Text = Arg("--seller") ?? Arg("--site") ?? DefaultSellerName;
            await StartSearchAsync();
        }
    }

    // =====================================================================
    //  تغییر حالت مرورگر
    // =====================================================================
    private async Task ChangePrivateModeAsync()
    {
        if (suppressToggle || isInitializing) return;
        if (huntCts != null || isFetchingProxies)
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
            await InitWebViewAsync(
                requestedMode,
                resetCurrent: true,
                proxyAddress: GetSelectedProxyAddress());
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
    //  دریافت و مدیریت پروکسی‌ها
    // =====================================================================
    private async Task LoadAndTestProxiesAsync()
    {
        if (isFetchingProxies || huntCts != null || proxyModeBox.Checked) return;

        isFetchingProxies = true;
        SetInputControlsEnabled(false);
        searchButton.Enabled = false;
        SetStatus("در حال دریافت و آزمایش پروکسی‌های عمومی...");

        try
        {
            var progress = new Progress<string>(message =>
            {
                if (IsDisposed || Disposing) return;
                proxyStatusLabel.Text = message;
                SetStatus(message);
            });
            var discovered = await proxyManager.FetchAndTestAsync(progress, CancellationToken.None);

            if (discovered.Count == 0)
            {
                if (proxyPool.Count == 0)
                {
                    proxyStatusLabel.Text = "پروکسی قابل استفاده‌ای پیدا نشد";
                    SetStatus("در نمونه‌ی دریافت‌شده پروکسی قابل استفاده‌ای پیدا نشد؛ بعداً دوباره امتحان کنید");
                }
                else
                {
                    proxyStatusLabel.Text = $"آزمایش جدید پروکسی موفق نبود؛ فهرست قبلی ({proxyPool.Count}) حفظ شد";
                    SetStatus("پروکسی جدیدی پاسخ نداد؛ فهرست قبلی دست‌نخورده باقی ماند");
                }
                return;
            }

            proxyPool.Clear();
            proxyPool.AddRange(discovered);
            currentProxyIndex = 0;
            searchesOnCurrentProxy = 0;
            proxyStatusLabel.Text = $"{proxyPool.Count} پروکسی دارای دسترسی Google و Torob؛ مرتب‌شده بر اساس سرعت";
            SetStatus($"{proxyPool.Count} پروکسی از منبع عمومی دریافت و آزمایش شد");
        }
        catch (Exception ex)
        {
            proxyStatusLabel.Text = "دریافت یا آزمایش پروکسی ناموفق بود";
            SetStatus($"دریافت یا آزمایش پروکسی ناموفق بود: {ex.Message}");
        }
        finally
        {
            isFetchingProxies = false;
            SetInputControlsEnabled(true);
            if (!IsDisposed && !Disposing)
                searchButton.Enabled = true;
        }
    }

    private async Task ChangeProxyModeAsync()
    {
        if (suppressProxyToggle || isInitializing) return;

        var requestedMode = proxyModeBox.Checked;
        if (huntCts != null || isFetchingProxies)
        {
            suppressProxyToggle = true;
            proxyModeBox.Checked = isProxyModeActive;
            suppressProxyToggle = false;
            return;
        }
        if (requestedMode && proxyPool.Count == 0)
        {
            suppressProxyToggle = true;
            proxyModeBox.Checked = false;
            suppressProxyToggle = false;
            SetStatus("ابتدا «دریافت و بررسی» را بزنید تا پروکسی قابل استفاده پیدا شود");
            return;
        }

        var previousMode = isProxyModeActive;
        SetInputControlsEnabled(false);
        searchButton.Enabled = false;
        SetStatus(requestedMode ? "در حال فعال‌سازی پروکسی..." : "در حال غیرفعال‌سازی پروکسی...");

        try
        {
            var address = requestedMode ? proxyPool[currentProxyIndex].Address : null;
            await InitWebViewAsync(isPrivateMode, resetCurrent: true, proxyAddress: address);
            isProxyModeActive = requestedMode;
            if (requestedMode)
                proxyStatusLabel.Text = $"فعال: {address} — تعویض خودکار هر {ProxyRotationInterval} جستجو";
            else if (proxyPool.Count > 0)
                proxyStatusLabel.Text = $"{proxyPool.Count} پروکسی آزمایش‌شده برای Google و Torob؛ استفاده خاموش است";
            SetStatus(requestedMode
                ? $"پروکسی فعال شد: {address} — تعویض خودکار هر {ProxyRotationInterval} جستجو"
                : "پروکسی غیرفعال شد؛ جستجو مستقیم انجام می‌شود");
        }
        catch (Exception ex)
        {
            suppressProxyToggle = true;
            proxyModeBox.Checked = previousMode;
            suppressProxyToggle = false;
            SetStatus($"تغییر حالت پروکسی ناموفق بود: {ex.Message}");
        }
        finally
        {
            SetInputControlsEnabled(true);
            if (!IsDisposed && !Disposing)
                searchButton.Enabled = true;
        }
    }

    private string? GetSelectedProxyAddress() =>
        isProxyModeActive && proxyPool.Count > 0
            ? proxyPool[Math.Clamp(currentProxyIndex, 0, proxyPool.Count - 1)].Address
            : null;

    private static string GetProxyUserDataFolder(string proxyAddress)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        var profileName = proxyAddress.Replace(':', '_');
        return Path.Combine(root, "MiniBrowser", "ProxyProfiles", profileName);
    }

    // =====================================================================
    //  ساخت WebView2
    // =====================================================================
    private async Task InitWebViewAsync(bool inPrivate, bool resetCurrent = false, string? proxyAddress = null)
    {
        var old = webView;
        if (resetCurrent)
        {
            // هنگام تعویض پروکسی یا ساخت نشست خصوصی تازه، کنترل قبلی بسته می‌شود.
            DisposeWebView(old);
            webView = null;
            activeProxyAddress = null;
        }

        var fresh = new WebView2 { Dock = DockStyle.Fill };
        try
        {
            var environmentOptions = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions();
            string? userDataFolder = null;
            if (proxyAddress != null)
            {
                environmentOptions.AdditionalBrowserArguments = $"--proxy-server=http://{proxyAddress}";
                userDataFolder = GetProxyUserDataFolder(proxyAddress);
                Directory.CreateDirectory(userDataFolder);
            }

            var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                null,
                userDataFolder,
                environmentOptions);
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = inPrivate;

            Controls.Add(fresh);
            Controls.SetChildIndex(fresh, 0); // WebView باید تمام فضای باقی‌مانده را بگیرد
            await fresh.EnsureCoreWebView2Async(environment, options);
            fresh.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var requestedUri) &&
                    (requestedUri.Scheme == Uri.UriSchemeHttp || requestedUri.Scheme == Uri.UriSchemeHttps))
                    fresh.CoreWebView2.Navigate(requestedUri.ToString());
            };

            webView = fresh;
            isPrivateMode = inPrivate;
            activeProxyAddress = proxyAddress;
            if (!resetCurrent)
                DisposeWebView(old);
        }
        catch
        {
            DisposeWebView(fresh);
            if (resetCurrent)
            {
                webView = null;
                activeProxyAddress = null;
            }
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
        if (huntCts != null || isFetchingProxies) return;

        var productQuery = keywordBox.Text.Trim();
        var sellerName = siteBox.Text.Trim();
        if (productQuery.Length == 0)
        {
            SetStatus("لطفاً عبارت محصول را وارد کنید");
            return;
        }
        if (sellerName.Length == 0)
        {
            SetStatus("لطفاً نام دقیق فروشنده را وارد کنید");
            return;
        }
        if (!BigInteger.TryParse(repetitionCountBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var repetitions) ||
            repetitions < BigInteger.One)
        {
            SetStatus("تعداد تکرار باید یک عدد صحیح مثبت باشد");
            return;
        }
        if (proxyModeBox.Checked && proxyPool.Count == 0)
        {
            SetStatus("حالت پروکسی فعال است اما پروکسی قابل استفاده‌ای وجود ندارد");
            return;
        }
        if (webView?.CoreWebView2 == null)
        {
            SetStatus("مرورگر هنوز آماده نیست");
            return;
        }

        var usePrivateMode = privateBox.Checked;
        var useProxy = proxyModeBox.Checked;
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

                if (useProxy && searchesOnCurrentProxy >= ProxyRotationInterval)
                {
                    searchesOnCurrentProxy = 0;
                    if (proxyPool.Count > 1)
                    {
                        currentProxyIndex = (currentProxyIndex + 1) % proxyPool.Count;
                        var nextProxy = proxyPool[currentProxyIndex];
                        proxyStatusLabel.Text = $"فعال: {nextProxy.Address} — تعویض در {ProxyRotationInterval} جستجوی بعدی";
                        SetStatus($"در حال تعویض پروکسی به {nextProxy.Address} برای جستجوی {attempt} از {repetitions}...");
                    }
                    else
                    {
                        SetStatus("فقط یک پروکسی قابل استفاده است؛ همان پروکسی برای ادامه حفظ می‌شود");
                    }
                }

                ct.ThrowIfCancellationRequested();

                var result = await RunTorobAttemptWithProxyFailoverAsync(
                    productQuery,
                    sellerName,
                    attempt,
                    repetitions,
                    usePrivateMode,
                    useProxy,
                    ct);
                lastSummary = result.Summary;
                if (!result.Success)
                {
                    aborted = true;
                    break;
                }

                completed = attempt;
                if (useProxy) searchesOnCurrentProxy++;
            }

            if (!aborted)
                SetStatus($"تکمیل شد: {completed} از {repetitions} نوبت. {lastSummary}");
            else
                SetStatus($"فرایند متوقف شد؛ {completed} از {repetitions} نوبت کامل شد. {lastSummary}");
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
                searchButton.Text = "پیدا کردن در ترب و باز کردن فروشنده";
                searchButton.BackColor = Accent;
                searchButton.FlatAppearance.MouseOverBackColor = AccentDark;
                searchButton.Enabled = true;
            }
        }
    }

    private sealed record TorobAttemptResult(bool Success, bool RetryWithNextProxy, string Summary);

    private sealed record TorobSearchCandidate(string Url, string Context, int Score);
    private sealed record SellerOffer(string Text, string Url);
    private sealed record TorobProductInspection(
        bool ProductMatches,
        SellerOffer? Offer,
        string ProductTitle,
        bool RetryWithNextProxy = false);

    private async Task<TorobAttemptResult> RunTorobAttemptWithProxyFailoverAsync(
        string productQuery,
        string sellerName,
        BigInteger attempt,
        BigInteger repetitions,
        bool usePrivateMode,
        bool useProxy,
        CancellationToken ct)
    {
        var maxAttempts = useProxy
            ? Math.Min(proxyPool.Count, MaxProxyRetriesPerSearch + 1)
            : 1;
        var triedProxies = new HashSet<string>(StringComparer.Ordinal);

        for (var proxyAttempt = 0; proxyAttempt < maxAttempts; proxyAttempt++)
        {
            ct.ThrowIfCancellationRequested();
            var requestedProxy = useProxy ? proxyPool[currentProxyIndex].Address : null;
            if (requestedProxy != null) triedProxies.Add(requestedProxy);

            if (usePrivateMode || !string.Equals(activeProxyAddress, requestedProxy, StringComparison.Ordinal))
            {
                SetStatus(usePrivateMode
                    ? $"در حال آماده‌سازی نشست خصوصی برای نوبت {attempt} از {repetitions}..."
                    : requestedProxy == null
                        ? "در حال آماده‌سازی اتصال مستقیم..."
                        : $"در حال آماده‌سازی اتصال از پروکسی {requestedProxy}...");
                try
                {
                    await InitWebViewAsync(
                        inPrivate: usePrivateMode,
                        resetCurrent: true,
                        proxyAddress: requestedProxy);
                }
                catch (Exception ex)
                {
                    var proxyLabel = requestedProxy ?? "مستقیم";
                    var initializationFailure = new TorobAttemptResult(
                        false,
                        useProxy,
                        $"آماده‌سازی مرورگر برای پروکسی {proxyLabel} ناموفق بود: {ex.Message}");
                    if (!useProxy || proxyAttempt + 1 >= maxAttempts || !SelectNextUntestedProxy(triedProxies))
                        return initializationFailure;
                    continue;
                }
            }

            var result = await RunTorobSearchAttemptAsync(
                productQuery,
                sellerName,
                attempt,
                repetitions,
                ct);
            if (!result.RetryWithNextProxy || !useProxy || proxyAttempt + 1 >= maxAttempts)
                return result;

            if (!SelectNextUntestedProxy(triedProxies))
                return result;
        }

        return new TorobAttemptResult(false, false, "اتصال با پروکسی‌های آزمایش‌شده برقرار نشد.");
    }

    private bool SelectNextUntestedProxy(HashSet<string> triedProxies)
    {
        if (proxyPool.Count < 2) return false;

        for (var offset = 1; offset <= proxyPool.Count; offset++)
        {
            var index = (currentProxyIndex + offset) % proxyPool.Count;
            var candidate = proxyPool[index];
            if (triedProxies.Contains(candidate.Address)) continue;

            currentProxyIndex = index;
            searchesOnCurrentProxy = 0;
            proxyStatusLabel.Text = $"فعال: {candidate.Address} — تعویض خودکار هر {ProxyRotationInterval} جستجو";
            SetStatus($"اتصال از پروکسی قبلی برقرار نشد؛ تلاش دوباره با {candidate.Address}...");
            return true;
        }

        return false;
    }

    private async Task<TorobAttemptResult> RunTorobSearchAttemptAsync(
        string productQuery,
        string sellerName,
        BigInteger attempt,
        BigInteger repetitions,
        CancellationToken ct)
    {
        var prefix = $"نوبت {attempt} از {repetitions}";
        var googleQuery = $"site:torob.com {productQuery}";
        var googleUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(googleQuery)}";
        SetStatus($"{prefix}: جستجوی «{productQuery}» در Google برای یافتن Torob...");

        if (!await NavigateAndWaitAsync(googleUrl, ct) || await IsBrowserNetworkErrorPageAsync())
            return new TorobAttemptResult(false, true, "صفحه Google بارگذاری نشد؛ اتصال یا پروکسی را بررسی کنید.");

        await Task.Delay(900, ct);
        if (await IsGoogleVerificationPageAsync())
            return new TorobAttemptResult(false, false, "Google درخواست تأیید هویت یا کپچا کرد؛ گردش‌کار متوقف شد.");

        var candidates = await FindTorobProductCandidatesAsync(productQuery);
        if (candidates.Count == 0)
        {
            // اگر ساختار نتایج Google عوض شده باشد، سایت Torob را مستقیم باز می‌کنیم و آنجا جستجو می‌کنیم.
            SetStatus("پیوند Torob در نتایج خوانده نشد؛ سایت Torob را باز می‌کنم...");
            candidates = new[] { new TorobSearchCandidate("https://torob.com/", "", 0) };
        }

        var sawMatchingProduct = false;
        var sellerLinkMissing = false;
        for (var index = 0; index < candidates.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            SetStatus($"{prefix}: باز کردن نتیجه {index + 1} از {candidates.Count} در Torob...");

            if (!await ClickLinkInCurrentPageAsync(candidate.Url, ct) || await IsBrowserNetworkErrorPageAsync())
                return new TorobAttemptResult(false, true, "صفحهٔ محصول Torob بارگذاری نشد؛ اتصال یا پروکسی را بررسی کنید.");

            await Task.Delay(700, ct);
            var productUri = webView?.Source;
            if (productUri == null || !IsTorobHost(productUri.Host))
                continue;

            var inspection = await FindAndInspectProductOnTorobAsync(productQuery, sellerName, ct);
            if (inspection.RetryWithNextProxy)
                return new TorobAttemptResult(false, true, "جستجوی داخلی یا صفحهٔ محصول Torob بارگذاری نشد.");
            if (!inspection.ProductMatches)
                continue;

            sawMatchingProduct = true;
            if (inspection.Offer == null)
            {
                sellerLinkMissing = true;
                continue;
            }

            if (!Uri.TryCreate(inspection.Offer.Url, UriKind.Absolute, out var sellerLink) ||
                sellerLink.Scheme != Uri.UriSchemeHttps)
            {
                sellerLinkMissing = true;
                continue;
            }

            SetStatus($"{prefix}: محصول «{inspection.ProductTitle}» و فروشندهٔ دقیق پیدا شد؛ در حال باز کردن فروشنده...");
            if (!await ClickLinkInCurrentPageAsync(sellerLink.ToString(), ct) || await IsBrowserNetworkErrorPageAsync())
                return new TorobAttemptResult(false, true, "پیوند فروشنده باز نشد؛ اتصال یا پروکسی را بررسی کنید.");

            if (!await WaitForExternalSellerLandingAsync(ct))
                return new TorobAttemptResult(false, true, "پیوند Torob باز شد اما صفحهٔ فروشنده بارگذاری نشد.");

            var landingHost = webView?.Source?.Host ?? "فروشنده";
            return new TorobAttemptResult(
                true,
                false,
                $"صفحهٔ فروشندهٔ «{sellerName}» باز شد ({landingHost}).");
        }

        if (sawMatchingProduct && sellerLinkMissing)
            return new TorobAttemptResult(false, false, $"محصول پیدا شد، اما لینک خرید با فروشندهٔ دقیق «{sellerName}» در صفحه نبود.");

        return new TorobAttemptResult(false, false, $"محصول مطابق «{productQuery}» در نتایج Torob تأیید نشد؛ هیچ لینک فروشنده‌ای باز نشد.");
    }

    private async Task<TorobProductInspection> FindAndInspectProductOnTorobAsync(
        string productQuery,
        string sellerName,
        CancellationToken ct)
    {
        var directInspection = await WaitForTorobSellerOfferAsync(productQuery, sellerName, ct);
        if (directInspection.ProductMatches && directInspection.Offer != null)
            return directInspection;

        SetStatus("در صفحهٔ Torob محصول را با کادر جستجوی خود سایت پیدا می‌کنم...");
        var listing = await SearchInsideTorobAsync(productQuery, ct);
        if (listing.RetryWithNextProxy)
            return new TorobProductInspection(false, null, "", true);

        var bestInspection = directInspection;
        foreach (var candidate in listing.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!await ClickLinkInCurrentPageAsync(candidate.Url, ct) || await IsBrowserNetworkErrorPageAsync())
                return bestInspection with { RetryWithNextProxy = true };

            var current = webView?.Source;
            if (current == null || !IsTorobHost(current.Host)) continue;

            var inspection = await WaitForTorobSellerOfferAsync(productQuery, sellerName, ct);
            if (inspection.Offer != null) return inspection;
            if (inspection.ProductMatches) bestInspection = inspection;
        }

        return bestInspection;
    }

    private sealed record TorobListingSearchResult(
        IReadOnlyList<TorobSearchCandidate> Candidates,
        bool RetryWithNextProxy);

    private async Task<TorobListingSearchResult> SearchInsideTorobAsync(string productQuery, CancellationToken ct)
    {
        var core = webView?.CoreWebView2;
        if (core == null) return new TorobListingSearchResult(Array.Empty<TorobSearchCandidate>(), true);

        var script = """
        (() => {
          const visible = element => !!element && element.getClientRects().length > 0 &&
            getComputedStyle(element).visibility !== 'hidden' && getComputedStyle(element).display !== 'none';
          const inputs = Array.from(document.querySelectorAll('input'))
            .filter(input => visible(input) && !input.disabled && !input.readOnly);
          const input = inputs.find(x => /نام\s*کالا|کالا\s*یا\s*فروشگاه|نام\s*محصول/.test(x.placeholder || '')) ||
            inputs.find(x => x.type === 'search' || /جستجو/.test(x.getAttribute('aria-label') || ''));
          if (!input) return { submitted: false };

          const valueSetter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
          if (valueSetter) valueSetter.call(input, __QUERY__); else input.value = __QUERY__;
          input.focus();
          input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: __QUERY__ }));
          input.dispatchEvent(new Event('change', { bubbles: true }));

          const form = input.closest('form');
          const button = form?.querySelector('button[type="submit"], input[type="submit"]') ||
            input.parentElement?.querySelector('button') ||
            Array.from(document.querySelectorAll('button')).find(x => visible(x) &&
              /جستجو/.test((x.innerText || x.getAttribute('aria-label') || '')));
          if (form?.requestSubmit) form.requestSubmit();
          else if (button) button.click();
          else {
            for (const type of ['keydown', 'keypress', 'keyup'])
              input.dispatchEvent(new KeyboardEvent(type, { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
          }
          return { submitted: true };
        })()
        """.Replace("__QUERY__", JsonSerializer.Serialize(productQuery));

        string raw;
        try
        {
            raw = await core.ExecuteScriptAsync(script);
        }
        catch
        {
            // فرم ممکن است با ثبت جستجو فوراً به مسیر نتایج ناوبری کرده باشد.
            await Task.Delay(700, ct);
            if (await IsBrowserNetworkErrorPageAsync())
                return new TorobListingSearchResult(Array.Empty<TorobSearchCandidate>(), true);
            var resultsAfterNavigation = await FindTorobProductCandidatesAsync(productQuery, productOnly: true);
            return new TorobListingSearchResult(resultsAfterNavigation, false);
        }
        using (var document = JsonDocument.Parse(raw))
        {
            if (!document.RootElement.TryGetProperty("submitted", out var submitted) ||
                submitted.ValueKind != JsonValueKind.True)
                return new TorobListingSearchResult(Array.Empty<TorobSearchCandidate>(), false);
        }

        for (var poll = 0; poll < 20; poll++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(400, ct);
            var candidates = await FindTorobProductCandidatesAsync(productQuery, productOnly: true);
            if (candidates.Count > 0)
                return new TorobListingSearchResult(candidates, false);
        }

        return new TorobListingSearchResult(Array.Empty<TorobSearchCandidate>(), false);
    }

    private async Task<IReadOnlyList<TorobSearchCandidate>> FindTorobProductCandidatesAsync(
        string productQuery,
        bool productOnly = false)
    {
        var core = webView?.CoreWebView2;
        if (core == null) return Array.Empty<TorobSearchCandidate>();

        var script = """
        (() => {
          const query = __QUERY__;
          const productOnly = __PRODUCT_ONLY__;
          const normalize = value => (value || '').normalize('NFKC')
            .replace(/[يى]/g, 'ی').replace(/ك/g, 'ک').replace(/[\u200c\u200d\u200e\u200f\u0640]/g, ' ')
            .toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
          const isGoogle = host => /(^|\.)google\./i.test(host);
          const unwrap = raw => {
            let url; try { url = new URL(raw, location.href); } catch (e) { return null; }
            if (isGoogle(url.hostname) && (url.pathname === '/url' || url.pathname === '/aclk')) {
              const target = url.searchParams.get('q') || url.searchParams.get('url');
              if (target) { try { url = new URL(target); } catch (e) { return null; } }
            }
            return url;
          };
          const isTorob = host => host.toLowerCase() === 'torob.com' || host.toLowerCase().endsWith('.torob.com');
          const queryTerms = normalize(query).split(' ').filter(term => term.length > 1);
          const seen = new Set(), candidates = [];
          for (const anchor of document.querySelectorAll('a[href]')) {
            if (!productOnly && isGoogle(location.hostname) && !anchor.querySelector('h3')) continue;
            const url = unwrap(anchor.href);
            if (!url || !/^https?:$/.test(url.protocol) || !isTorob(url.hostname) ||
                url.hostname.toLowerCase() === 'api.torob.com') continue;
            const path = url.pathname.toLowerCase();
            if (/^\/(v4|product-page)(\/|$)/.test(path)) continue;
            if (url.origin === location.origin && path === location.pathname.toLowerCase()) continue;
            if (productOnly && (path === '/' || path.startsWith('/browse_') || /^\/(browse|search|category|categories|shop|shops|brand|brands|blog|about)(\/|$)/.test(path))) continue;
            const key = url.origin + url.pathname;
            if (seen.has(key)) continue;
            seen.add(key);
            const container = anchor.closest('div.MjjYud, div.g, div[data-hveid]') ||
              anchor.parentElement?.parentElement?.parentElement;
            const context = [anchor.innerText, anchor.getAttribute('aria-label'), container?.innerText]
              .filter(Boolean).join(' ').replace(/\s+/g, ' ').trim().slice(0, 1400);
            const normalizedContext = normalize(context + ' ' + url.pathname);
            const score = queryTerms.reduce((total, term) => total + (normalizedContext.includes(term) ? 1 : 0), 0) +
              (productOnly && /^\/(p|product)\//.test(path) ? 20 : 0);
            candidates.push({ url: url.href, context, score });
          }
          if (productOnly) candidates.sort((left, right) => right.score - left.score);
          // از Google فقط اولین نتیجهٔ اصلی Torob باز می‌شود؛ در جستجوی داخلی Torob چند محصول رتبه‌بندی می‌شوند.
          const limit = productOnly ? __LIMIT__ : 1;
          return { candidates: candidates.slice(0, limit) };
        })()
        """.Replace("__QUERY__", JsonSerializer.Serialize(productQuery))
           .Replace("__PRODUCT_ONLY__", productOnly ? "true" : "false")
           .Replace("__LIMIT__", MaxTorobCandidates.ToString(CultureInfo.InvariantCulture));

        var raw = await core.ExecuteScriptAsync(script);
        using var document = JsonDocument.Parse(raw);
        var result = new List<TorobSearchCandidate>();
        if (!document.RootElement.TryGetProperty("candidates", out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in array.EnumerateArray())
        {
            var url = GetJsonString(item, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsTorobHost(uri.Host)) continue;
            result.Add(new TorobSearchCandidate(
                uri.ToString(),
                GetJsonString(item, "context"),
                item.TryGetProperty("score", out var score) && score.TryGetInt32(out var value) ? value : 0));
        }

        return result;
    }

    private async Task<TorobProductInspection> WaitForTorobSellerOfferAsync(
        string productQuery,
        string sellerName,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var productMatched = false;
        var lastTitle = "";
        var sellerNeedle = NormalizePersianText(sellerName);

        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(10))
        {
            ct.ThrowIfCancellationRequested();
            var core = webView?.CoreWebView2;
            if (core == null) break;

            var script = """
            (() => {
              const normalize = value => (value || '').normalize('NFKC')
                .replace(/[يى]/g, 'ی').replace(/ك/g, 'ک').replace(/[\u200c\u200d\u200e\u200f\u0640]/g, ' ')
                .toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
              const seller = normalize(__SELLER__);
              const heading = Array.from(document.querySelectorAll('h1')).map(x => x.innerText || x.textContent || '').join(' ');
              const description = document.querySelector('meta[name="description"]')?.content ||
                document.querySelector('meta[property="og:title"]')?.content || '';
              const productText = [document.title, heading, description].filter(Boolean).join(' ').slice(0, 5000);
              const offers = [];
              for (const a of document.querySelectorAll('a[href]')) {
                const text = (a.innerText || a.textContent || '').replace(/\s+/g, ' ').trim();
                const normalized = normalize(text);
                if (!normalized.includes(seller) || !normalized.includes(normalize('خرید از'))) continue;
                let url; try { url = new URL(a.href); } catch (e) { continue; }
                if (url.protocol !== 'https:') continue;
                offers.push({ text, url: url.href });
                if (offers.length >= 8) break;
              }
              return { title: document.title || '', heading, description, productText, offers };
            })()
            """.Replace("__SELLER__", JsonSerializer.Serialize(sellerName));

            var raw = await core.ExecuteScriptAsync(script);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var heading = GetJsonString(root, "heading");
            var title = heading.Length > 0 ? heading : GetJsonString(root, "title");
            if (title.Length > 0) lastTitle = title;
            var hasProductHeading = heading.Length > 0 || GetJsonString(root, "description").Length > 0;
            var productText = GetJsonString(root, "productText");

            if (IsProductTitleMatch(productQuery, productText))
            {
                productMatched = true;
                if (root.TryGetProperty("offers", out var offers) && offers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var offer in offers.EnumerateArray())
                    {
                        var text = GetJsonString(offer, "text");
                        var url = GetJsonString(offer, "url");
                        if (!NormalizePersianText(text).Contains(sellerNeedle, StringComparison.Ordinal) ||
                            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                            continue;
                        return new TorobProductInspection(true, new SellerOffer(text, uri.ToString()), title);
                    }
                }
            }
            else if (DateTime.UtcNow - started > TimeSpan.FromSeconds(2.5) && hasProductHeading)
            {
                return new TorobProductInspection(false, null, title);
            }

            await Task.Delay(450, ct);
        }

        return new TorobProductInspection(productMatched, null, lastTitle);
    }

    private async Task<bool> ClickLinkInCurrentPageAsync(string targetUrl, CancellationToken ct)
    {
        var core = webView?.CoreWebView2;
        if (core == null) return false;

        var originalSource = core.Source;
        var script = """
        (() => {
          const expected = __TARGET__;
          const unwrap = raw => {
            let url; try { url = new URL(raw, location.href); } catch (e) { return ''; }
            if (/(^|\.)google\./i.test(url.hostname) && (url.pathname === '/url' || url.pathname === '/aclk')) {
              const target = url.searchParams.get('q') || url.searchParams.get('url');
              if (target) { try { url = new URL(target); } catch (e) { return ''; } }
            }
            return url.href;
          };
          const anchor = Array.from(document.querySelectorAll('a[href]'))
            .find(a => a.href === expected || unwrap(a.href) === expected);
          if (!anchor) return false;
          anchor.target = '_self';
          anchor.click();
          return true;
        })()
        """.Replace("__TARGET__", JsonSerializer.Serialize(targetUrl));

        string raw;
        try
        {
            raw = await core.ExecuteScriptAsync(script);
        }
        catch
        {
            var currentSource = webView?.CoreWebView2?.Source;
            if (string.Equals(currentSource, originalSource, StringComparison.Ordinal))
                return await NavigateAndWaitAsync(targetUrl, ct);
            raw = "true"; // کلیک ممکن است هم‌زمان سند را عوض کرده باشد.
        }
        if (!string.Equals(raw.Trim(), "true", StringComparison.Ordinal))
            return await NavigateAndWaitAsync(targetUrl, ct);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var currentSource = webView?.CoreWebView2?.Source;
            if (!string.IsNullOrWhiteSpace(currentSource) &&
                !string.Equals(currentSource, originalSource, StringComparison.Ordinal))
            {
                try
                {
                    var readyRaw = await webView!.CoreWebView2!.ExecuteScriptAsync("document.readyState");
                    var readyState = JsonSerializer.Deserialize<string>(readyRaw) ?? "";
                    if (readyState is "interactive" or "complete")
                        return !await IsBrowserNetworkErrorPageAsync();
                }
                catch
                {
                    // هنگام تعویض سند، اجرای اسکریپت ممکن است موقتاً در دسترس نباشد.
                }
            }
            await Task.Delay(200, ct);
        }

        // اگر رویداد کلیک توسط سایت رهگیری نشد، همان href زنده را مستقیم در WebView باز می‌کنیم.
        return await NavigateAndWaitAsync(targetUrl, ct);
    }

    private async Task<bool> WaitForExternalSellerLandingAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var uri = webView?.Source;
            if (uri != null && uri.Scheme == Uri.UriSchemeHttps && !IsTorobHost(uri.Host))
                return !await IsBrowserNetworkErrorPageAsync();
            await Task.Delay(300, ct);
        }

        return false;
    }

    private async Task<bool> IsBrowserNetworkErrorPageAsync()
    {
        var core = webView?.CoreWebView2;
        if (core == null) return true;

        try
        {
            var raw = await core.ExecuteScriptAsync("(document.body?.innerText || '').slice(0, 1800)");
            var text = JsonSerializer.Deserialize<string>(raw) ?? "";
            return text.Contains("ERR_TIMED_OUT", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("ERR_PROXY_CONNECTION_FAILED", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("ERR_TUNNEL_CONNECTION_FAILED", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("ERR_NAME_NOT_RESOLVED", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("ERR_CONNECTION_RESET", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private async Task<bool> IsGoogleVerificationPageAsync()
    {
        var current = webView?.Source;
        if (current != null && current.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase) &&
            current.AbsolutePath.StartsWith("/sorry", StringComparison.OrdinalIgnoreCase))
            return true;

        var core = webView?.CoreWebView2;
        if (core == null) return false;
        var raw = await core.ExecuteScriptAsync("(document.body?.innerText || '').slice(0, 3000)");
        var text = JsonSerializer.Deserialize<string>(raw) ?? "";
        return text.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("automated queries", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("verify you are a human", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("I'm not a robot", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("captcha", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTorobHost(string host) =>
        host.Equals("torob.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".torob.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsProductTitleMatch(string expectedProduct, string actualTitle)
    {
        var actual = NormalizePersianText(actualTitle);
        if (actual.Length == 0) return false;

        var ignoredTerms = new HashSet<string>(StringComparer.Ordinal)
        {
            "شمش", "نقره", "گرم", "گرمی", "عیار", "محصول", "قیمت", "خرید", "از", "در", "برای", "با",
        };
        var expectedTerms = NormalizePersianText(expectedProduct)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var identifyingTerms = expectedTerms
            .Where(term => !ignoredTerms.Contains(term) && term.Length >= 2)
            .ToArray();
        if (identifyingTerms.Length == 0) identifyingTerms = expectedTerms;
        return identifyingTerms.Length > 0 &&
               identifyingTerms.All(term => actual.Contains(term, StringComparison.Ordinal));
    }

    private static string NormalizePersianText(string value)
    {
        var source = value.Normalize(NormalizationForm.FormKC)
            .Replace('ي', 'ی').Replace('ى', 'ی').Replace('ك', 'ک')
            .Replace('\u200c', ' ').Replace('\u200d', ' ').Replace('\u200e', ' ').Replace('\u200f', ' ')
            .Replace('\u0640', ' ');
        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.DecimalDigitNumber)
            {
                var digit = (int)char.GetNumericValue(character);
                if (digit is >= 0 and <= 9) builder.Append((char)('0' + digit));
                continue;
            }
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }
            if (builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');
        }
        return builder.ToString().Trim();
    }

    private static string GetJsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

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
        void DomHandler(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2DOMContentLoadedEventArgs e) =>
            completion.TrySetResult(true);
        void NavigationHandler(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess) completion.TrySetResult(false);
        }

        core.DOMContentLoaded += DomHandler;
        core.NavigationCompleted += NavigationHandler;
        try
        {
            core.Navigate(url);
            using var registration = ct.Register(() => completion.TrySetCanceled(ct));
            var finished = await Task.WhenAny(completion.Task, Task.Delay(25000, ct));
            ct.ThrowIfCancellationRequested();
            if (finished != completion.Task) return false;
            return await completion.Task;
        }
        finally
        {
            core.DOMContentLoaded -= DomHandler;
            core.NavigationCompleted -= NavigationHandler;
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
