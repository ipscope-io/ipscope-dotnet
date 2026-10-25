using System.Text.Json.Serialization;

namespace IpScope.Sdk;

/// <summary>
/// Geographic classification and confidence returned in <see cref="IpLookupResponse.Geo"/>.
/// </summary>
public sealed record GeoBlock(
    [property: JsonPropertyName("country_code")] string? CountryCode,
    [property: JsonPropertyName("subdivision")] string? Subdivision,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("timezone")] string? Timezone,
    [property: JsonPropertyName("location_type")] string LocationType,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("accuracy_radius_km")] double? AccuracyRadiusKm,
    [property: JsonPropertyName("declared_country_code")] string? DeclaredCountryCode,
    [property: JsonPropertyName("physical_country_code")] string? PhysicalCountryCode,
    [property: JsonPropertyName("is_virtual_location")] bool? IsVirtualLocation
);

/// <summary>
/// Autonomous System and network operator details returned in <see cref="IpLookupResponse.Network"/>.
/// </summary>
public sealed record NetworkBlock(
    [property: JsonPropertyName("asn")] int? Asn,
    [property: JsonPropertyName("organization")] string? Organization
);

/// <summary>
/// Security, hosting, and anonymity classification returned in <see cref="IpLookupResponse.Security"/>.
/// </summary>
public sealed record SecurityBlock(
    [property: JsonPropertyName("is_vpn")] bool? IsVpn,
    [property: JsonPropertyName("is_hosting")] bool? IsHosting,
    [property: JsonPropertyName("is_tor_exit")] bool? IsTorExit,
    [property: JsonPropertyName("is_proxy")] bool? IsProxy,
    [property: JsonPropertyName("anonymizer_type")] string AnonymizerType,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason_codes")] IReadOnlyList<string>? ReasonCodes
);

/// <summary>
/// Optional connection context block returned when licensed.
/// </summary>
public sealed record ConnectionBlock(
    [property: JsonPropertyName("connection_type")] string ConnectionType,
    [property: JsonPropertyName("is_residential")] bool? IsResidential,
    [property: JsonPropertyName("is_mobile")] bool? IsMobile,
    [property: JsonPropertyName("is_business")] bool? IsBusiness,
    [property: JsonPropertyName("is_hosting")] bool? IsHosting
);

/// <summary>
/// Optional security extensions telemetry block returned when licensed.
/// </summary>
public sealed record SecurityExtensionsBlock(
    [property: JsonPropertyName("abuse_velocity_score")] double? AbuseVelocityScore,
    [property: JsonPropertyName("is_known_scanner")] bool? IsKnownScanner,
    [property: JsonPropertyName("observation_recency_hours")] double? ObservationRecencyHours
);

/// <summary>
/// Canonical single IP lookup response (<c>GET /v1/ip/:ip</c> and <c>GET /v1/ip/me</c>).
/// </summary>
public sealed record IpLookupResponse(
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("ip_version")] int IpVersion,
    [property: JsonPropertyName("matched_network")] string? MatchedNetwork,
    [property: JsonPropertyName("geo")] GeoBlock Geo,
    [property: JsonPropertyName("network")] NetworkBlock Network,
    [property: JsonPropertyName("security")] SecurityBlock Security,
    [property: JsonPropertyName("connection")] ConnectionBlock? Connection,
    [property: JsonPropertyName("security_extensions")] SecurityExtensionsBlock? SecurityExtensions,
    [property: JsonPropertyName("dataset_version")] string DatasetVersion,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt
);

/// <summary>
/// Batch IP lookup response (<c>POST /v1/ip/batch</c>).
/// </summary>
public sealed record BatchLookupResponse(
    [property: JsonPropertyName("dataset_version")] string DatasetVersion,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("results")] IReadOnlyList<IpLookupResponse> Results
);

/// <summary>
/// Canonical network prefix record returned in <c>GET /v1/networks/:prefix/:length</c>.
/// </summary>
public sealed record CanonicalNetworkPrefixRecord(
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("family")] int Family,
    [property: JsonPropertyName("asn")] int? Asn,
    [property: JsonPropertyName("organization")] string? Organization,
    [property: JsonPropertyName("country_code")] string? CountryCode,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("location_type")] string? LocationType,
    [property: JsonPropertyName("is_vpn")] bool? IsVpn,
    [property: JsonPropertyName("is_hosting")] bool? IsHosting,
    [property: JsonPropertyName("is_tor_exit")] bool? IsTorExit,
    [property: JsonPropertyName("is_proxy")] bool? IsProxy,
    [property: JsonPropertyName("anonymizer_type")] string? AnonymizerType
);

/// <summary>
/// Network CIDR prefix lookup response (<c>GET /v1/networks/:prefix/:length</c>).
/// </summary>
public sealed record NetworkPrefixLookupResponse(
    [property: JsonPropertyName("queried_cidr")] string QueriedCidr,
    [property: JsonPropertyName("family")] int Family,
    [property: JsonPropertyName("dataset_version")] string DatasetVersion,
    [property: JsonPropertyName("record")] CanonicalNetworkPrefixRecord? Record
);

/// <summary>
/// Dataset snapshot summary entry returned in <c>GET /v1/metadata/datasets</c>.
/// </summary>
public sealed record DatasetSnapshotSummary(
    [property: JsonPropertyName("snapshot_version")] string SnapshotVersion,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("ruleset_version")] string RulesetVersion,
    [property: JsonPropertyName("published_at")] string PublishedAt,
    [property: JsonPropertyName("ipv4_prefix_count")] int Ipv4PrefixCount,
    [property: JsonPropertyName("ipv6_prefix_count")] int Ipv6PrefixCount
);

/// <summary>
/// Datasets metadata response (<c>GET /v1/metadata/datasets</c>).
/// </summary>
public sealed record DatasetsMetadataResponse(
    [property: JsonPropertyName("active_version")] string ActiveVersion,
    [property: JsonPropertyName("previous_version")] string? PreviousVersion,
    [property: JsonPropertyName("snapshots")] IReadOnlyList<DatasetSnapshotSummary> Snapshots
);

/// <summary>
/// Input payload for submitting IP/prefix correction feedback (<c>POST /v1/feedback</c>).
/// </summary>
public sealed record FeedbackSubmissionInput(
    [property: JsonPropertyName("prefix_or_ip")] string PrefixOrIp,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("issue_type")] string IssueType,
    [property: JsonPropertyName("requested_correction")] IReadOnlyDictionary<string, object?> RequestedCorrection,
    [property: JsonPropertyName("evidence_urls")] IReadOnlyList<string>? EvidenceUrls = null
);

/// <summary>
/// Response returned when submitting IP/prefix correction feedback (<c>POST /v1/feedback</c>).
/// </summary>
public sealed record FeedbackSubmissionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("prefix_or_ip")] string PrefixOrIp,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("issue_type")] string IssueType,
    [property: JsonPropertyName("requested_correction")] IReadOnlyDictionary<string, object?> RequestedCorrection,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("evidence_urls")] IReadOnlyList<string>? EvidenceUrls = null
);

public sealed record ServiceSnapshotInfo(
    [property: JsonPropertyName("currentVersion")] string? CurrentVersion,
    [property: JsonPropertyName("previousVersion")] string? PreviousVersion
);

/// <summary>
/// Public service health and active snapshot status (<c>GET /v1/status</c>).
/// </summary>
public sealed record ServiceStatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("snapshot")] ServiceSnapshotInfo? Snapshot
)
{
    [JsonIgnore]
    public string? CurrentVersion => Snapshot?.CurrentVersion;

    [JsonIgnore]
    public string? PreviousVersion => Snapshot?.PreviousVersion;
}

/// <summary>
/// Parsed user-scoped rate-limit headers returned by <c>@ipscope/api</c>.
/// </summary>
public sealed record RateLimitMetadata(
    int? LimitPerMinute = null,
    int? Remaining = null,
    long? ResetEpochSeconds = null,
    string? Scope = null,
    int? BurstCapacity = null,
    int? RateLimitRps = null
);

/// <summary>
/// Envelope containing both the typed API response data and HTTP/rate-limit metadata.
/// </summary>
public sealed record IpScopeResultWithMeta<T>(
    T Data,
    int Status,
    RateLimitMetadata RateLimit,
    bool FromCache
);

/// <summary>
/// Telemetry for the in-memory O(1) LRU + TTL cache.
/// </summary>
public sealed record CacheStats(
    bool Enabled,
    int Size,
    int MaxEntries,
    TimeSpan Ttl,
    long Hits,
    long Misses,
    long CoalescedInFlightHits
);

/// <summary>
/// Retry policy configuration for transient network, 5xx, and 429 errors.
/// </summary>
public sealed record RetryOptions(
    int MaxRetries = 2,
    TimeSpan? BaseDelay = null,
    TimeSpan? MaxDelay = null,
    bool RetryOnRateLimit = true
)
{
    public TimeSpan EffectiveBaseDelay => BaseDelay ?? TimeSpan.FromMilliseconds(150);
    public TimeSpan EffectiveMaxDelay => MaxDelay ?? TimeSpan.FromSeconds(3);
}

/// <summary>
/// In-memory O(1) LRU + TTL cache configuration (&lt; 400 KB default RAM ceiling).
/// </summary>
public sealed record CacheOptions(
    bool Enabled = true,
    int MaxEntries = 512,
    TimeSpan? Ttl = null
)
{
    public TimeSpan EffectiveTtl => Ttl ?? TimeSpan.FromSeconds(60);
}

/// <summary>
/// Per-request options for cache bypass and custom headers.
/// </summary>
public sealed record RequestOptions(
    bool BypassCache = false,
    IReadOnlyDictionary<string, string>? Headers = null
);

/// <summary>
/// Top-level client configuration for <see cref="IpScopeClient"/>.
/// </summary>
public sealed record IpScopeClientOptions(
    string BaseUrl = "https://api.ipscope.io",
    TimeSpan? Timeout = null,
    RetryOptions? Retry = null,
    CacheOptions? Cache = null,
    string? UserAgent = null,
    HttpClient? HttpClient = null
);
