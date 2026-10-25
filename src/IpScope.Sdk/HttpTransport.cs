using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace IpScope.Sdk;

/// <summary>
/// Resilient Sync and Async HTTP transport backed by <see cref="HttpClient"/>.
/// Enforces a hard 2 MiB (<see cref="MaxResponseBytes"/>) response payload ceiling,
/// parses user-scoped rate-limit headers, and performs exponential backoff with jitter.
/// </summary>
internal sealed class HttpTransport : IDisposable
{
    public const string DefaultBaseUrl = "https://api.ipscope.io";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    public const int MaxResponseBytes = 2 * 1024 * 1024; // 2 MiB hard ceiling
    public const string SdkUserAgent = "ipscope-dotnet/1.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public readonly record struct TransportResponse<T>(T Data, int Status, RateLimitMetadata RateLimit);

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly TimeSpan _timeout;
    private readonly RetryOptions _retry;
    private readonly string _userAgent;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private RateLimitMetadata? _lastRateLimit;

    public HttpTransport(string apiKey, IpScopeClientOptions? options = null)
    {
        _apiKey = IpScopeValidator.ValidateApiKey(apiKey);
        var opts = options ?? new IpScopeClientOptions();
        string rawBase = string.IsNullOrWhiteSpace(opts.BaseUrl) ? DefaultBaseUrl : opts.BaseUrl.Trim().TrimEnd('/');
        _baseUrl = rawBase.Length == 0 ? DefaultBaseUrl : rawBase;
        _timeout = opts.Timeout is { TotalMilliseconds: > 0 } t ? t : DefaultTimeout;
        _retry = opts.Retry ?? new RetryOptions();
        _userAgent = string.IsNullOrWhiteSpace(opts.UserAgent)
            ? SdkUserAgent
            : $"{opts.UserAgent.Trim()} {SdkUserAgent}";

        if (opts.HttpClient is not null)
        {
            _httpClient = opts.HttpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                MaxConnectionsPerServer = 20,
                AllowAutoRedirect = false
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _ownsHttpClient = true;
        }
    }

    public RateLimitMetadata? GetLastRateLimit() => Volatile.Read(ref _lastRateLimit);

    public async Task<TransportResponse<T>> RequestAsync<T>(
        HttpMethod method,
        string path,
        object? requestBody,
        IReadOnlyDictionary<string, string>? extraHeaders,
        bool requiresAuth,
        CancellationToken cancellationToken
    )
    {
        string? jsonPayload = requestBody is null ? null : JsonSerializer.Serialize(requestBody, JsonOptions);
        int maxRetries = Math.Max(0, _retry.MaxRetries);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            using HttpRequestMessage request = BuildRequestMessage(method, path, jsonPayload, extraHeaders, requiresAuth);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cts.Token
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IpScopeTimeoutException(path, _timeout, ex);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < maxRetries)
                {
                    await Task.Delay(ComputeBackoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new IpScopeApiException(
                    $"Network request to \"{path}\" failed: {ex.Message}",
                    0,
                    path,
                    "IPSCOPE_NETWORK_ERROR",
                    null,
                    ex
                );
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                byte[] bodyBytes = await ReadBoundedStreamAsync(response, status, path, cts.Token).ConfigureAwait(false);
                RateLimitMetadata rl = ParseRateLimitHeaders(response.Headers);
                Volatile.Write(ref _lastRateLimit, rl);

                if (status is >= 200 and < 300)
                {
                    T? parsed = JsonSerializer.Deserialize<T>(bodyBytes, JsonOptions);
                    if (parsed is null)
                    {
                        throw new IpScopeApiException(
                            $"Empty or null JSON response from \"{path}\"",
                            status,
                            path,
                            "IPSCOPE_PARSE_ERROR",
                            rl
                        );
                    }
                    return new TransportResponse<T>(parsed, status, rl);
                }

                string errMsg = ExtractErrorMessage(bodyBytes, status, path);

                if (status is 401 or 403)
                {
                    throw new IpScopeAuthenticationException(errMsg, status, path, rl);
                }

                if (status == 429)
                {
                    TimeSpan? retryAfter = ComputeRetryAfter(response.Headers, rl);
                    if (_retry.RetryOnRateLimit && attempt < maxRetries)
                    {
                        TimeSpan delay = retryAfter is { } ra
                            ? TimeSpan.FromMilliseconds(Math.Min(_retry.EffectiveMaxDelay.TotalMilliseconds, ra.TotalMilliseconds))
                            : ComputeBackoff(attempt);
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw new IpScopeRateLimitException(errMsg, path, retryAfter, rl);
                }

                if (status is 502 or 503 or 504 && attempt < maxRetries)
                {
                    await Task.Delay(ComputeBackoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new IpScopeApiException(errMsg, status, path, $"IPSCOPE_HTTP_{status}", rl);
            }
        }

        throw new IpScopeApiException(
            $"Exhausted {maxRetries} retries for \"{path}\"",
            0,
            path,
            "IPSCOPE_RETRY_EXHAUSTED"
        );
    }

    public TransportResponse<T> RequestSync<T>(
        HttpMethod method,
        string path,
        object? requestBody,
        IReadOnlyDictionary<string, string>? extraHeaders,
        bool requiresAuth
    )
    {
        string? jsonPayload = requestBody is null ? null : JsonSerializer.Serialize(requestBody, JsonOptions);
        int maxRetries = Math.Max(0, _retry.MaxRetries);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            using var cts = new CancellationTokenSource(_timeout);
            using HttpRequestMessage request = BuildRequestMessage(method, path, jsonPayload, extraHeaders, requiresAuth);
            HttpResponseMessage response;
            try
            {
                response = _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }
            catch (OperationCanceledException ex)
            {
                throw new IpScopeTimeoutException(path, _timeout, ex);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < maxRetries)
                {
                    Thread.Sleep(ComputeBackoff(attempt));
                    continue;
                }
                throw new IpScopeApiException(
                    $"Network request to \"{path}\" failed: {ex.Message}",
                    0,
                    path,
                    "IPSCOPE_NETWORK_ERROR",
                    null,
                    ex
                );
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                byte[] bodyBytes = ReadBoundedStreamSync(response, status, path);
                RateLimitMetadata rl = ParseRateLimitHeaders(response.Headers);
                Volatile.Write(ref _lastRateLimit, rl);

                if (status is >= 200 and < 300)
                {
                    T? parsed = JsonSerializer.Deserialize<T>(bodyBytes, JsonOptions);
                    if (parsed is null)
                    {
                        throw new IpScopeApiException(
                            $"Empty or null JSON response from \"{path}\"",
                            status,
                            path,
                            "IPSCOPE_PARSE_ERROR",
                            rl
                        );
                    }
                    return new TransportResponse<T>(parsed, status, rl);
                }

                string errMsg = ExtractErrorMessage(bodyBytes, status, path);

                if (status is 401 or 403)
                {
                    throw new IpScopeAuthenticationException(errMsg, status, path, rl);
                }

                if (status == 429)
                {
                    TimeSpan? retryAfter = ComputeRetryAfter(response.Headers, rl);
                    if (_retry.RetryOnRateLimit && attempt < maxRetries)
                    {
                        TimeSpan delay = retryAfter is { } ra
                            ? TimeSpan.FromMilliseconds(Math.Min(_retry.EffectiveMaxDelay.TotalMilliseconds, ra.TotalMilliseconds))
                            : ComputeBackoff(attempt);
                        Thread.Sleep(delay);
                        continue;
                    }
                    throw new IpScopeRateLimitException(errMsg, path, retryAfter, rl);
                }

                if (status is 502 or 503 or 504 && attempt < maxRetries)
                {
                    Thread.Sleep(ComputeBackoff(attempt));
                    continue;
                }

                throw new IpScopeApiException(errMsg, status, path, $"IPSCOPE_HTTP_{status}", rl);
            }
        }

        throw new IpScopeApiException(
            $"Exhausted {maxRetries} retries for \"{path}\"",
            0,
            path,
            "IPSCOPE_RETRY_EXHAUSTED"
        );
    }

    private HttpRequestMessage BuildRequestMessage(
        HttpMethod method,
        string path,
        string? jsonPayload,
        IReadOnlyDictionary<string, string>? extraHeaders,
        bool requiresAuth
    )
    {
        var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        if (requiresAuth)
        {
            req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        }

        if (extraHeaders is not null)
        {
            foreach (var kvp in extraHeaders)
            {
                req.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }
        }

        if (jsonPayload is not null)
        {
            req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        }

        return req;
    }

    private static async Task<byte[]> ReadBoundedStreamAsync(
        HttpResponseMessage response,
        int status,
        string path,
        CancellationToken ct
    )
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new IpScopeApiException(
                $"IPScope response payload ({response.Content.Headers.ContentLength} bytes) exceeds 2 MiB safety limit",
                status,
                path,
                "IPSCOPE_RESPONSE_TOO_LARGE"
            );
        }

        using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream(1024);
        byte[] buffer = new byte[8192];
        int total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                throw new IpScopeApiException(
                    "IPScope response payload exceeds 2 MiB safety limit",
                    status,
                    path,
                    "IPSCOPE_RESPONSE_TOO_LARGE"
                );
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static byte[] ReadBoundedStreamSync(HttpResponseMessage response, int status, string path)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new IpScopeApiException(
                $"IPScope response payload ({response.Content.Headers.ContentLength} bytes) exceeds 2 MiB safety limit",
                status,
                path,
                "IPSCOPE_RESPONSE_TOO_LARGE"
            );
        }

        using Stream stream = response.Content.ReadAsStream();
        using var ms = new MemoryStream(1024);
        byte[] buffer = new byte[8192];
        int total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                throw new IpScopeApiException(
                    "IPScope response payload exceeds 2 MiB safety limit",
                    status,
                    path,
                    "IPSCOPE_RESPONSE_TOO_LARGE"
                );
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static RateLimitMetadata ParseRateLimitHeaders(HttpResponseHeaders headers)
    {
        static int? ParseInt(HttpResponseHeaders h, string name) =>
            h.TryGetValues(name, out IEnumerable<string>? vals) &&
            int.TryParse(vals.FirstOrDefault()?.Trim(), out int v)
                ? v
                : null;

        static long? ParseLong(HttpResponseHeaders h, string name) =>
            h.TryGetValues(name, out IEnumerable<string>? vals) &&
            long.TryParse(vals.FirstOrDefault()?.Trim(), out long v)
                ? v
                : null;

        static string? ParseString(HttpResponseHeaders h, string name) =>
            h.TryGetValues(name, out IEnumerable<string>? vals)
                ? vals.FirstOrDefault()?.Trim()
                : null;

        return new RateLimitMetadata(
            ParseInt(headers, "x-ratelimit-limit"),
            ParseInt(headers, "x-ratelimit-remaining"),
            ParseLong(headers, "x-ratelimit-reset"),
            ParseString(headers, "x-ratelimit-scope"),
            ParseInt(headers, "x-ratelimit-burst"),
            ParseInt(headers, "x-ratelimit-rps")
        );
    }

    private static string ExtractErrorMessage(byte[] bodyBytes, int status, string path)
    {
        if (bodyBytes.Length > 0)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(bodyBytes);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("message", out JsonElement msgElem))
                {
                    if (msgElem.ValueKind == JsonValueKind.String)
                    {
                        string? msg = msgElem.GetString();
                        if (!string.IsNullOrWhiteSpace(msg))
                        {
                            return msg.Trim();
                        }
                    }
                    else if (msgElem.ValueKind == JsonValueKind.Array)
                    {
                        var parts = new List<string>();
                        foreach (JsonElement item in msgElem.EnumerateArray())
                        {
                            parts.Add(item.ToString());
                        }
                        if (parts.Count > 0)
                        {
                            return string.Join("; ", parts);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore and fall back
            }
        }
        return $"IPScope API error ({status}) on {path}";
    }

    private static TimeSpan? ComputeRetryAfter(HttpResponseHeaders headers, RateLimitMetadata rl)
    {
        if (headers.TryGetValues("retry-after", out IEnumerable<string>? vals))
        {
            string? raw = vals.FirstOrDefault()?.Trim();
            if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out double secs) && secs >= 0)
            {
                return TimeSpan.FromSeconds(secs);
            }
        }
        if (rl.ResetEpochSeconds is { } resetEpoch)
        {
            TimeSpan delta = DateTimeOffset.FromUnixTimeSeconds(resetEpoch) - DateTimeOffset.UtcNow;
            if (delta > TimeSpan.Zero)
            {
                return delta;
            }
        }
        return null;
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        double baseMs = Math.Max(1.0, _retry.EffectiveBaseDelay.TotalMilliseconds);
        double maxMs = Math.Max(baseMs, _retry.EffectiveMaxDelay.TotalMilliseconds);
        int shift = Math.Min(attempt, 10);
        double exp = Math.Min(maxMs, baseMs * (1 << shift));
        double jitter = Random.Shared.NextDouble() * Math.Max(5.0, exp * 0.25);
        return TimeSpan.FromMilliseconds(Math.Min(maxMs, exp + jitter));
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
