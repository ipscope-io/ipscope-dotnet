using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace IpScope.Sdk;

/// <summary>
/// Official thread-safe Sync &amp; Async .NET Server-Side Client for the IPScope IP Intelligence API.
/// <para>
/// Scope Notice: Strictly limited to IP intelligence, network CIDR prefix, dataset metadata,
/// feedback, and service status operations. Exposes zero account, wallet, or billing operations.
/// </para>
/// </summary>
public sealed class IpScopeClient : IDisposable
{
    public const int MaxResponseBytes = HttpTransport.MaxResponseBytes;

    private readonly HttpTransport _transport;
    private readonly LruTtlCache<IpLookupResponse> _ipCache;
    private readonly LruTtlCache<NetworkPrefixLookupResponse> _networkCache;

    public IpScopeClient(string apiKey, IpScopeClientOptions? options = null)
    {
        _transport = new HttpTransport(apiKey, options);
        var cacheOpts = options?.Cache ?? new CacheOptions();
        _ipCache = new LruTtlCache<IpLookupResponse>(cacheOpts);
        _networkCache = new LruTtlCache<NetworkPrefixLookupResponse>(
            cacheOpts with { MaxEntries = Math.Min(128, Math.Max(1, cacheOpts.MaxEntries)) }
        );
    }

    // =========================================================================
    // Asynchronous Operations (Task / IAsyncEnumerable)
    // =========================================================================

    /// <summary>
    /// Looks up intelligence for a single IPv4 or IPv6 address (<c>GET /v1/ip/:ip</c>).
    /// Concurrent lookups for the same IP are automatically singleflight-coalesced.
    /// </summary>
    public async Task<IpLookupResponse> LookupIpAsync(
        string rawIp,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var res = await LookupIpWithMetaAsync(rawIp, options, cancellationToken).ConfigureAwait(false);
        return res.Data;
    }

    /// <summary>
    /// Looks up a single IPv4 or IPv6 address and returns both data and rate-limit metadata.
    /// </summary>
    public async Task<IpScopeResultWithMeta<IpLookupResponse>> LookupIpWithMetaAsync(
        string rawIp,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var opts = options ?? new RequestOptions();
        var validated = IpScopeValidator.ValidateIpAddress(rawIp);
        string ipStr = validated.CanonicalIp;

        int lastStatus = 200;
        RateLimitMetadata lastRl = _transport.GetLastRateLimit() ?? new RateLimitMetadata();

        var coalesced = await _ipCache.GetOrComputeAsync(ipStr, opts.BypassCache, async () =>
        {
            string path = $"/v1/ip/{Uri.EscapeDataString(ipStr)}";
            var resp = await _transport.RequestAsync<IpLookupResponse>(
                HttpMethod.Get,
                path,
                null,
                opts.Headers,
                true,
                cancellationToken
            ).ConfigureAwait(false);

            lastStatus = resp.Status;
            lastRl = resp.RateLimit;

            string canonicalLower = resp.Data.Ip.ToLowerInvariant();
            if (!string.Equals(canonicalLower, ipStr, StringComparison.Ordinal))
            {
                _ipCache.Set(canonicalLower, resp.Data);
            }
            return resp.Data;
        }).ConfigureAwait(false);

        return new IpScopeResultWithMeta<IpLookupResponse>(
            coalesced.Value,
            lastStatus,
            _transport.GetLastRateLimit() ?? lastRl,
            coalesced.FromCache
        );
    }

    /// <summary>
    /// Looks up intelligence for the caller's own server-observed IP (<c>GET /v1/ip/me</c>).
    /// </summary>
    public async Task<IpLookupResponse> LookupCallerIpAsync(
        RequestOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var opts = options ?? new RequestOptions();
        var resp = await _transport.RequestAsync<IpLookupResponse>(
            HttpMethod.Get,
            "/v1/ip/me",
            null,
            opts.Headers,
            true,
            cancellationToken
        ).ConfigureAwait(false);
        return resp.Data;
    }

    /// <summary>
    /// Performs a batch lookup for 1..100 IPs (<c>POST /v1/ip/batch</c>), deduplicating wire
    /// requests and reusing warm LRU cache entries automatically.
    /// </summary>
    public async Task<BatchLookupResponse> LookupBatchAsync(
        IReadOnlyList<string> rawIps,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var opts = options ?? new RequestOptions();
        var batch = IpScopeValidator.ValidateAndDeduplicateBatchIps(rawIps, IpScopeValidator.MaxSingleBatchSize);

        var resolvedByIp = new Dictionary<string, IpLookupResponse>(batch.UniqueIps.Count, StringComparer.Ordinal);
        var missingIps = new List<string>(batch.UniqueIps.Count);

        if (!opts.BypassCache)
        {
            foreach (string ipStr in batch.UniqueIps)
            {
                IpLookupResponse? cached = _ipCache.Get(ipStr);
                if (cached is not null)
                {
                    resolvedByIp[ipStr] = cached;
                }
                else
                {
                    missingIps.Add(ipStr);
                }
            }
        }
        else
        {
            missingIps.AddRange(batch.UniqueIps);
        }

        string datasetVersion = string.Empty;

        if (missingIps.Count > 0)
        {
            var resp = await _transport.RequestAsync<BatchLookupResponse>(
                HttpMethod.Post,
                "/v1/ip/batch",
                new Dictionary<string, object> { ["ips"] = missingIps },
                opts.Headers,
                true,
                cancellationToken
            ).ConfigureAwait(false);

            datasetVersion = resp.Data.DatasetVersion;
            for (int i = 0; i < missingIps.Count && i < resp.Data.Results.Count; i++)
            {
                string reqIp = missingIps[i];
                IpLookupResponse item = resp.Data.Results[i];
                resolvedByIp[reqIp] = item;
                _ipCache.Set(reqIp, item);
                string canonicalLower = item.Ip.ToLowerInvariant();
                if (!string.Equals(canonicalLower, reqIp, StringComparison.Ordinal))
                {
                    _ipCache.Set(canonicalLower, item);
                }
            }
        }

        var orderedResults = new List<IpLookupResponse>(batch.OrderedIps.Count);
        foreach (string ipStr in batch.OrderedIps)
        {
            orderedResults.Add(resolvedByIp[ipStr]);
        }
        if (datasetVersion.Length == 0 && orderedResults.Count > 0)
        {
            datasetVersion = orderedResults[0].DatasetVersion;
        }

        return new BatchLookupResponse(datasetVersion, orderedResults.Count, orderedResults);
    }

    /// <summary>
    /// Performs a chunked batch lookup for &gt;100 IPs with bounded async concurrency (default: 4).
    /// </summary>
    public async Task<BatchLookupResponse> LookupBatchChunkedAsync(
        IReadOnlyList<string> rawIps,
        int concurrency = 4,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var opts = options ?? new RequestOptions();
        var batch = IpScopeValidator.ValidateAndDeduplicateBatchIps(rawIps, 0);
        int effectiveConcurrency = Math.Clamp(concurrency, 1, 16);

        var chunks = new List<IReadOnlyList<string>>();
        for (int i = 0; i < batch.UniqueIps.Count; i += IpScopeValidator.MaxSingleBatchSize)
        {
            int count = Math.Min(IpScopeValidator.MaxSingleBatchSize, batch.UniqueIps.Count - i);
            var slice = new List<string>(count);
            for (int j = 0; j < count; j++)
            {
                slice.Add(batch.UniqueIps[i + j]);
            }
            chunks.Add(slice);
        }

        var resolvedByIp = new ConcurrentDictionary<string, IpLookupResponse>(StringComparer.Ordinal);
        string datasetVersion = string.Empty;
        using var semaphore = new SemaphoreSlim(effectiveConcurrency, effectiveConcurrency);

        var tasks = chunks.Select(async chunk =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var res = await LookupBatchAsync(chunk, opts, cancellationToken).ConfigureAwait(false);
                if (datasetVersion.Length == 0 && res.DatasetVersion.Length > 0)
                {
                    Interlocked.CompareExchange(ref datasetVersion, res.DatasetVersion, string.Empty);
                }
                for (int idx = 0; idx < chunk.Count && idx < res.Results.Count; idx++)
                {
                    resolvedByIp[chunk[idx]] = res.Results[idx];
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var orderedResults = new List<IpLookupResponse>(batch.OrderedIps.Count);
        foreach (string ipStr in batch.OrderedIps)
        {
            orderedResults.Add(resolvedByIp[ipStr]);
        }

        return new BatchLookupResponse(datasetVersion, orderedResults.Count, orderedResults);
    }

    /// <summary>
    /// Constant-RAM async stream (<see cref="IAsyncEnumerable{BatchLookupResponse}"/>) yielding batch
    /// chunks of up to 100 IPs without holding all results in memory.
    /// </summary>
    public async IAsyncEnumerable<BatchLookupResponse> LookupBatchStreamAsync(
        IEnumerable<string> rawIps,
        RequestOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (rawIps is null)
        {
            throw new IpScopeValidationException("Batch IPs enumerable must not be null", "ips");
        }

        var currentChunk = new List<string>(IpScopeValidator.MaxSingleBatchSize);
        bool anySeen = false;

        foreach (string rawIp in rawIps)
        {
            anySeen = true;
            var v = IpScopeValidator.ValidateIpAddress(rawIp);
            currentChunk.Add(v.CanonicalIp);

            if (currentChunk.Count >= IpScopeValidator.MaxSingleBatchSize)
            {
                var toSend = currentChunk;
                currentChunk = new List<string>(IpScopeValidator.MaxSingleBatchSize);
                yield return await LookupBatchAsync(toSend, options, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!anySeen)
        {
            throw new IpScopeValidationException("Batch IPs enumerable must contain at least 1 IP address", "ips");
        }

        if (currentChunk.Count > 0)
        {
            yield return await LookupBatchAsync(currentChunk, options, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inspects canonical network prefix intelligence by CIDR notation (<c>GET /v1/networks/:prefix/:length</c>).
    /// </summary>
    public async Task<NetworkPrefixLookupResponse> LookupNetworkAsync(
        string rawCidr,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var opts = options ?? new RequestOptions();
        var vCidr = IpScopeValidator.ValidateCidrPrefix(rawCidr);

        var coalesced = await _networkCache.GetOrComputeAsync(vCidr.CanonicalCidr, opts.BypassCache, async () =>
        {
            string path = $"/v1/networks/{Uri.EscapeDataString(vCidr.Address)}/{vCidr.PrefixLength}";
            var resp = await _transport.RequestAsync<NetworkPrefixLookupResponse>(
                HttpMethod.Get,
                path,
                null,
                opts.Headers,
                true,
                cancellationToken
            ).ConfigureAwait(false);
            return resp.Data;
        }).ConfigureAwait(false);

        return coalesced.Value;
    }

    /// <summary>
    /// Lists active and historical dataset snapshot versions (<c>GET /v1/metadata/datasets</c>).
    /// </summary>
    public async Task<DatasetsMetadataResponse> GetDatasetsMetadataAsync(
        RequestOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var opts = options ?? new RequestOptions();
        var resp = await _transport.RequestAsync<DatasetsMetadataResponse>(
            HttpMethod.Get,
            "/v1/metadata/datasets",
            null,
            opts.Headers,
            true,
            cancellationToken
        ).ConfigureAwait(false);
        return resp.Data;
    }

    /// <summary>
    /// Submits geolocation, ASN, or anonymity correction feedback (<c>POST /v1/feedback</c>).
    /// </summary>
    public async Task<FeedbackSubmissionResponse> SubmitFeedbackAsync(
        FeedbackSubmissionInput feedback,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        if (feedback is null)
        {
            throw new IpScopeValidationException("feedback must not be null", "feedback");
        }
        string target = feedback.PrefixOrIp?.Trim() ?? string.Empty;
        if (target.Contains('/'))
        {
            IpScopeValidator.ValidateCidrPrefix(target);
        }
        else
        {
            IpScopeValidator.ValidateIpAddress(target);
        }

        var opts = options ?? new RequestOptions();
        var normalized = feedback with { PrefixOrIp = target };
        var resp = await _transport.RequestAsync<FeedbackSubmissionResponse>(
            HttpMethod.Post,
            "/v1/feedback",
            normalized,
            opts.Headers,
            true,
            cancellationToken
        ).ConfigureAwait(false);
        return resp.Data;
    }

    /// <summary>
    /// Fetches public service health and active snapshot status (<c>GET /v1/status</c>).
    /// </summary>
    public async Task<ServiceStatusResponse> GetStatusAsync(
        RequestOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var opts = options ?? new RequestOptions();
        var resp = await _transport.RequestAsync<ServiceStatusResponse>(
            HttpMethod.Get,
            "/v1/status",
            null,
            opts.Headers,
            false,
            cancellationToken
        ).ConfigureAwait(false);
        return resp.Data;
    }

    // =========================================================================
    // Synchronous Operations
    // =========================================================================

    public IpLookupResponse LookupIp(string rawIp, RequestOptions? options = null) =>
        LookupIpWithMeta(rawIp, options).Data;

    public IpScopeResultWithMeta<IpLookupResponse> LookupIpWithMeta(string rawIp, RequestOptions? options = null)
    {
        var opts = options ?? new RequestOptions();
        var validated = IpScopeValidator.ValidateIpAddress(rawIp);
        string ipStr = validated.CanonicalIp;

        if (!opts.BypassCache)
        {
            IpLookupResponse? cached = _ipCache.Get(ipStr);
            if (cached is not null)
            {
                return new IpScopeResultWithMeta<IpLookupResponse>(
                    cached,
                    200,
                    _transport.GetLastRateLimit() ?? new RateLimitMetadata(),
                    true
                );
            }
        }

        string path = $"/v1/ip/{Uri.EscapeDataString(ipStr)}";
        var resp = _transport.RequestSync<IpLookupResponse>(HttpMethod.Get, path, null, opts.Headers, true);
        _ipCache.Set(ipStr, resp.Data);
        string canonicalLower = resp.Data.Ip.ToLowerInvariant();
        if (!string.Equals(canonicalLower, ipStr, StringComparison.Ordinal))
        {
            _ipCache.Set(canonicalLower, resp.Data);
        }
        return new IpScopeResultWithMeta<IpLookupResponse>(resp.Data, resp.Status, resp.RateLimit, false);
    }

    public IpLookupResponse LookupCallerIp(RequestOptions? options = null)
    {
        var opts = options ?? new RequestOptions();
        return _transport.RequestSync<IpLookupResponse>(HttpMethod.Get, "/v1/ip/me", null, opts.Headers, true).Data;
    }

    public BatchLookupResponse LookupBatch(IReadOnlyList<string> rawIps, RequestOptions? options = null)
    {
        var opts = options ?? new RequestOptions();
        var batch = IpScopeValidator.ValidateAndDeduplicateBatchIps(rawIps, IpScopeValidator.MaxSingleBatchSize);

        var resolvedByIp = new Dictionary<string, IpLookupResponse>(batch.UniqueIps.Count, StringComparer.Ordinal);
        var missingIps = new List<string>(batch.UniqueIps.Count);

        if (!opts.BypassCache)
        {
            foreach (string ipStr in batch.UniqueIps)
            {
                IpLookupResponse? cached = _ipCache.Get(ipStr);
                if (cached is not null)
                {
                    resolvedByIp[ipStr] = cached;
                }
                else
                {
                    missingIps.Add(ipStr);
                }
            }
        }
        else
        {
            missingIps.AddRange(batch.UniqueIps);
        }

        string datasetVersion = string.Empty;

        if (missingIps.Count > 0)
        {
            var resp = _transport.RequestSync<BatchLookupResponse>(
                HttpMethod.Post,
                "/v1/ip/batch",
                new Dictionary<string, object> { ["ips"] = missingIps },
                opts.Headers,
                true
            );
            datasetVersion = resp.Data.DatasetVersion;
            for (int i = 0; i < missingIps.Count && i < resp.Data.Results.Count; i++)
            {
                string reqIp = missingIps[i];
                IpLookupResponse item = resp.Data.Results[i];
                resolvedByIp[reqIp] = item;
                _ipCache.Set(reqIp, item);
                string canonicalLower = item.Ip.ToLowerInvariant();
                if (!string.Equals(canonicalLower, reqIp, StringComparison.Ordinal))
                {
                    _ipCache.Set(canonicalLower, item);
                }
            }
        }

        var orderedResults = new List<IpLookupResponse>(batch.OrderedIps.Count);
        foreach (string ipStr in batch.OrderedIps)
        {
            orderedResults.Add(resolvedByIp[ipStr]);
        }
        if (datasetVersion.Length == 0 && orderedResults.Count > 0)
        {
            datasetVersion = orderedResults[0].DatasetVersion;
        }
        return new BatchLookupResponse(datasetVersion, orderedResults.Count, orderedResults);
    }

    public BatchLookupResponse LookupBatchChunked(IReadOnlyList<string> rawIps, RequestOptions? options = null)
    {
        var opts = options ?? new RequestOptions();
        var batch = IpScopeValidator.ValidateAndDeduplicateBatchIps(rawIps, 0);
        var resolvedByIp = new Dictionary<string, IpLookupResponse>(batch.UniqueIps.Count, StringComparer.Ordinal);
        string datasetVersion = string.Empty;

        for (int i = 0; i < batch.UniqueIps.Count; i += IpScopeValidator.MaxSingleBatchSize)
        {
            int count = Math.Min(IpScopeValidator.MaxSingleBatchSize, batch.UniqueIps.Count - i);
            var chunk = new List<string>(count);
            for (int j = 0; j < count; j++)
            {
                chunk.Add(batch.UniqueIps[i + j]);
            }

            var res = LookupBatch(chunk, opts);
            if (datasetVersion.Length == 0 && res.DatasetVersion.Length > 0)
            {
                datasetVersion = res.DatasetVersion;
            }
            for (int idx = 0; idx < chunk.Count && idx < res.Results.Count; idx++)
            {
                resolvedByIp[chunk[idx]] = res.Results[idx];
            }
        }

        var orderedResults = new List<IpLookupResponse>(batch.OrderedIps.Count);
        foreach (string ipStr in batch.OrderedIps)
        {
            orderedResults.Add(resolvedByIp[ipStr]);
        }
        return new BatchLookupResponse(datasetVersion, orderedResults.Count, orderedResults);
    }

    public IEnumerable<BatchLookupResponse> LookupBatchStream(
        IEnumerable<string> rawIps,
        RequestOptions? options = null
    )
    {
        if (rawIps is null)
        {
            throw new IpScopeValidationException("Batch IPs enumerable must not be null", "ips");
        }

        var currentChunk = new List<string>(IpScopeValidator.MaxSingleBatchSize);
        bool anySeen = false;

        foreach (string rawIp in rawIps)
        {
            anySeen = true;
            var v = IpScopeValidator.ValidateIpAddress(rawIp);
            currentChunk.Add(v.CanonicalIp);

            if (currentChunk.Count >= IpScopeValidator.MaxSingleBatchSize)
            {
                var toSend = currentChunk;
                currentChunk = new List<string>(IpScopeValidator.MaxSingleBatchSize);
                yield return LookupBatch(toSend, options);
            }
        }

        if (!anySeen)
        {
            throw new IpScopeValidationException("Batch IPs enumerable must contain at least 1 IP address", "ips");
        }

        if (currentChunk.Count > 0)
        {
            yield return LookupBatch(currentChunk, options);
        }
    }

    public NetworkPrefixLookupResponse LookupNetwork(string rawCidr, RequestOptions? options = null)
    {
        var opts = options ?? new RequestOptions();
        var vCidr = IpScopeValidator.ValidateCidrPrefix(rawCidr);

        if (!opts.BypassCache)
        {
            NetworkPrefixLookupResponse? cached = _networkCache.Get(vCidr.CanonicalCidr);
            if (cached is not null)
            {
                return cached;
            }
        }

        string path = $"/v1/networks/{Uri.EscapeDataString(vCidr.Address)}/{vCidr.PrefixLength}";
        var resp = _transport.RequestSync<NetworkPrefixLookupResponse>(HttpMethod.Get, path, null, opts.Headers, true);
        _networkCache.Set(vCidr.CanonicalCidr, resp.Data);
        return resp.Data;
    }

    public DatasetsMetadataResponse GetDatasetsMetadata(RequestOptions? options = null)
    {
        var opts = options ?? new RequestOptions();
        return _transport.RequestSync<DatasetsMetadataResponse>(
            HttpMethod.Get,
            "/v1/metadata/datasets",
            null,
            opts.Headers,
            true
        ).Data;
    }

    public FeedbackSubmissionResponse SubmitFeedback(FeedbackSubmissionInput feedback, RequestOptions? options = null)
    {
        if (feedback is null)
        {
            throw new IpScopeValidationException("feedback must not be null", "feedback");
        }
        string target = feedback.PrefixOrIp?.Trim() ?? string.Empty;
        if (target.Contains('/'))
        {
            IpScopeValidator.ValidateCidrPrefix(target);
        }
        else
        {
            IpScopeValidator.ValidateIpAddress(target);
        }

        var opts = options ?? new RequestOptions();
        var normalized = feedback with { PrefixOrIp = target };
        return _transport.RequestSync<FeedbackSubmissionResponse>(
            HttpMethod.Post,
            "/v1/feedback",
            normalized,
            opts.Headers,
            true
        ).Data;
    }

    public ServiceStatusResponse GetStatus(RequestOptions? options = null)
    {
        var opts = options ?? new RequestOptions();
        return _transport.RequestSync<ServiceStatusResponse>(
            HttpMethod.Get,
            "/v1/status",
            null,
            opts.Headers,
            false
        ).Data;
    }

    // =========================================================================
    // Telemetry & Lifecycle
    // =========================================================================

    public RateLimitMetadata? GetLastRateLimit() => _transport.GetLastRateLimit();

    public CacheStats GetCacheStats() => _ipCache.GetStats();

    public void ClearCache()
    {
        _ipCache.Clear();
        _networkCache.Clear();
    }

    public void Dispose()
    {
        ClearCache();
        _transport.Dispose();
    }
}
