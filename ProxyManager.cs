using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MiniBrowser;

internal sealed record ProxyEndpoint(string Address, int LatencyMilliseconds);

internal sealed class ProxyManager
{
    // فهرست عمومی HTTP proxyها؛ endpoint از API گیت‌هاب به صورت Base64 دریافت می‌شود.
    public const string SourceUrl = "https://api.github.com/repos/TheSpeedX/PROXY-List/contents/http.txt";

    private const string TestUrl = "https://example.com/";
    private const int MaxCandidatesToTest = 60;
    private const int MaxConcurrentTests = 10;
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProxyTimeout = TimeSpan.FromSeconds(4);

    public async Task<IReadOnlyList<ProxyEndpoint>> FetchAndTestAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("در حال دریافت فهرست پروکسی‌ها...");

        string sourceText;
        using (var client = new HttpClient { Timeout = SourceTimeout })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniBrowser/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await client.GetAsync(SourceUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var jsonText = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(jsonText);

            var root = document.RootElement;
            if (!root.TryGetProperty("encoding", out var encoding) || encoding.GetString() != "base64" ||
                !root.TryGetProperty("content", out var contentElement))
            {
                throw new InvalidDataException("پاسخ منبع پروکسی‌ها قابل‌خواندن نیست.");
            }

            var encodedContent = contentElement.GetString();
            if (string.IsNullOrWhiteSpace(encodedContent))
                throw new InvalidDataException("فهرست منبع پروکسی خالی است.");

            sourceText = Encoding.UTF8.GetString(Convert.FromBase64String(encodedContent));
        }

        var candidates = sourceText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeProxyAddress)
            .Where(address => address != null)
            .Select(address => address!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(_ => Random.Shared.Next())
            .Take(MaxCandidatesToTest)
            .ToArray();

        if (candidates.Length == 0)
            throw new InvalidDataException("در فهرست دریافتی پروکسی معتبری پیدا نشد.");

        progress?.Report($"در حال آزمایش اتصال {candidates.Length} پروکسی منتخب به HTTPS...");

        var working = new ConcurrentBag<ProxyEndpoint>();
        var completed = 0;
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentTests,
                CancellationToken = cancellationToken,
            },
            async (address, token) =>
            {
                var result = await TestProxyAsync(address, token);
                if (result != null) working.Add(result);

                var tested = Interlocked.Increment(ref completed);
                progress?.Report($"در حال آزمایش پروکسی‌ها: {tested}/{candidates.Length}...");
            });

        return working.OrderBy(proxy => proxy.LatencyMilliseconds).ToArray();
    }

    private static string? NormalizeProxyAddress(string value)
    {
        var candidate = value.Trim();
        if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            candidate = candidate["http://".Length..];
        if (candidate.Contains('/') || candidate.Contains('@')) return null;

        var separator = candidate.LastIndexOf(':');
        if (separator <= 0 || separator == candidate.Length - 1) return null;
        if (!IPAddress.TryParse(candidate[..separator], out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !IsPublicIpv4(address))
            return null;
        if (!int.TryParse(candidate[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
            return null;

        return $"{address}:{port}";
    }

    private static bool IsPublicIpv4(IPAddress address)
    {
        var octets = address.GetAddressBytes();
        var first = octets[0];
        var second = octets[1];
        var third = octets[2];

        if (first is 0 or 10 or 127 or >= 224) return false;
        if (first == 100 && second is >= 64 and <= 127) return false; // Shared address space
        if (first == 169 && second == 254) return false; // Link-local
        if (first == 172 && second is >= 16 and <= 31) return false; // Private
        if (first == 192 && second == 168) return false; // Private
        if (first == 192 && second == 0 && third is 0 or 2) return false; // Special/documentation
        if (first == 198 && (second is 18 or 19 || second == 51 && third == 100)) return false;
        if (first == 203 && second == 0 && third == 113) return false; // Documentation
        return true;
    }

    private static async Task<ProxyEndpoint?> TestProxyAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://{address}"),
                UseProxy = true,
            };
            using var client = new HttpClient(handler) { Timeout = ProxyTimeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniBrowser/1.0");

            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync(
                TestUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode ||
                !string.Equals(response.RequestMessage?.RequestUri?.Host, "example.com", StringComparison.OrdinalIgnoreCase))
                return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!body.Contains("Example Domain", StringComparison.OrdinalIgnoreCase)) return null;

            stopwatch.Stop();
            return new ProxyEndpoint(address, (int)stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // timeout پروکسی مرده یا کند را از فهرست حذف می‌کند.
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (WebException)
        {
            return null;
        }
    }
}
