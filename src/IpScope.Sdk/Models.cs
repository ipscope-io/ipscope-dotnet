using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IpScope.Sdk;

/// <summary>
/// Geographic classification and confidence returned in <see cref="IpLookupResponse.Geo"/>.
/// <see cref="Latitude"/>/<see cref="Longitude"/> are representative coordinates (never device GPS);
/// <see cref="PostalCode"/> is present when the city dataset has one. All three may be <c>null</c>.
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
    [property: JsonPropertyName("is_virtual_location")] bool? IsVirtualLocation,
    [property: JsonPropertyName("postal_code")] string? PostalCode = null,
    [property: JsonPropertyName("latitude")] double? Latitude = null,
    [property: JsonPropertyName("longitude")] double? Longitude = null
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
/// <see cref="IsRelay"/> is <c>true</c> for a published consumer privacy-relay egress (e.g. Apple iCloud
/// Private Relay); relay users are not reported as VPN, proxy or hosting. <c>null</c> when unknown.
/// </summary>
public sealed record SecurityBlock(
    [property: JsonPropertyName("is_vpn")] bool? IsVpn,
    [property: JsonPropertyName("is_hosting")] bool? IsHosting,
    [property: JsonPropertyName("is_tor_exit")] bool? IsTorExit,
    [property: JsonPropertyName("is_proxy")] bool? IsProxy,
    [property: JsonPropertyName("anonymizer_type")] string AnonymizerType,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason_codes")] IReadOnlyList<string>? ReasonCodes,
    [property: JsonPropertyName("is_relay")] bool? IsRelay = null
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
/// <see cref="AbuseVelocityScore"/> is a 0-100 score (an integer on the wire) or <c>null</c>; it is typed
/// <see cref="double"/> so older fractional payloads still parse.
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
/// The API serializes this record in camelCase (<c>countryCode</c>, <c>isVpn</c>, ...); camelCase and
/// snake_case keys are both read (see <see cref="CanonicalNetworkPrefixRecordConverter"/>).
/// <see cref="AbuseVelocityScore"/> is a 0-100 score or <c>null</c>.
/// </summary>
[JsonConverter(typeof(CanonicalNetworkPrefixRecordConverter))]
public sealed record CanonicalNetworkPrefixRecord(
    string Prefix,
    int Family,
    int? Asn,
    string? Organization,
    string? CountryCode,
    string? City,
    string? LocationType,
    bool? IsVpn,
    bool? IsHosting,
    bool? IsTorExit,
    bool? IsProxy,
    string? AnonymizerType,
    string? Subdivision = null,
    string? PostalCode = null,
    double? Latitude = null,
    double? Longitude = null,
    string? Timezone = null,
    double? GeoConfidence = null,
    double? AccuracyRadiusKm = null,
    bool? IsVirtualLocation = null,
    string? ConnectionType = null,
    bool? IsRelay = null,
    double? SecurityConfidence = null,
    double? AbuseVelocityScore = null,
    bool? IsKnownScanner = null,
    IReadOnlyList<string>? ReasonCodes = null,
    string? UpdatedAt = null
);

/// <summary>
/// Network CIDR prefix lookup response (<c>GET /v1/networks/:prefix/:length</c>).
/// <see cref="MatchedNetwork"/> is the most-specific matched prefix, <see cref="BgpPrefixLength"/> its length,
/// and <see cref="Lookup"/> the full IP lookup for the prefix's base address (each may be <c>null</c>).
/// </summary>
public sealed record NetworkPrefixLookupResponse(
    [property: JsonPropertyName("queried_cidr")] string QueriedCidr,
    [property: JsonPropertyName("family")] int Family,
    [property: JsonPropertyName("dataset_version")] string DatasetVersion,
    [property: JsonPropertyName("record")] CanonicalNetworkPrefixRecord? Record,
    [property: JsonPropertyName("matched_network")] string? MatchedNetwork = null,
    [property: JsonPropertyName("bgp_prefix_length")] int? BgpPrefixLength = null,
    [property: JsonPropertyName("lookup")] IpLookupResponse? Lookup = null
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
/// Values accepted by <c>POST /v1/feedback</c>.
/// </summary>
public static class FeedbackValues
{
    /// <summary>Reporter roles accepted as <see cref="FeedbackSubmissionInput.Role"/>.</summary>
    public static IReadOnlyList<string> Roles { get; } = ["network_owner", "user", "researcher"];

    /// <summary>Issue classifications accepted as <see cref="FeedbackSubmissionInput.IssueType"/>.</summary>
    public static IReadOnlyList<string> IssueTypes { get; } =
    [
        "wrong_country",
        "wrong_city",
        "false_positive_vpn",
        "false_positive_hosting",
        "false_positive_tor",
        "virtual_location_mismatch",
        "other"
    ];
}

/// <summary>
/// Input payload for submitting IP/prefix correction feedback (<c>POST /v1/feedback</c>).
/// <see cref="Role"/> is one of <see cref="FeedbackValues.Roles"/>; <see cref="IssueType"/> one of
/// <see cref="FeedbackValues.IssueTypes"/>. Pass the optional fields by name, e.g.
/// <c>new FeedbackSubmissionInput(ip, "network_owner", "wrong_country", correction, EvidenceUrl: url)</c>.
/// Unset optional fields are not sent.
/// </summary>
public sealed record FeedbackSubmissionInput(
    [property: JsonPropertyName("prefix_or_ip")] string PrefixOrIp,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("issue_type")] string IssueType,
    [property: JsonPropertyName("requested_correction")] IReadOnlyDictionary<string, object?> RequestedCorrection,
    [property: JsonIgnore]
    [property: Obsolete("The API accepts a single URL; use EvidenceUrl. The first entry is sent as evidence_url when EvidenceUrl is null.")]
    IReadOnlyList<string>? EvidenceUrls = null,
    [property: JsonPropertyName("evidence_url")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EvidenceUrl = null,
    [property: JsonPropertyName("contact_consent")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ContactConsent = null,
    [property: JsonPropertyName("contact_email")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ContactEmail = null
)
{
    /// <summary>1.0.x constructor, kept for binary compatibility.</summary>
    [Obsolete("The API accepts a single URL; use the EvidenceUrl parameter.")]
    public FeedbackSubmissionInput(
        string PrefixOrIp,
        string Role,
        string IssueType,
        IReadOnlyDictionary<string, object?> RequestedCorrection,
        IReadOnlyList<string>? EvidenceUrls
    ) : this(PrefixOrIp, Role, IssueType, RequestedCorrection, EvidenceUrls, null, null, null)
    {
    }

    /// <summary>The evidence URL that is sent: <see cref="EvidenceUrl"/>, else the first non-blank legacy entry.</summary>
    [JsonIgnore]
    public string? EffectiveEvidenceUrl
    {
        get
        {
            if (EvidenceUrl is not null)
            {
                return EvidenceUrl;
            }
#pragma warning disable CS0618
            return EvidenceUrls?.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
#pragma warning restore CS0618
        }
    }
}

/// <summary>
/// Response returned when submitting IP/prefix correction feedback (<c>POST /v1/feedback</c>).
/// <see cref="CreatedAt"/> is read from <c>createdAt</c> (current API) or <c>created_at</c>.
/// </summary>
public sealed record FeedbackSubmissionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("prefix_or_ip")] string PrefixOrIp,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("issue_type")] string IssueType,
    [property: JsonPropertyName("requested_correction")] IReadOnlyDictionary<string, object?> RequestedCorrection,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("evidence_urls")] IReadOnlyList<string>? EvidenceUrls = null,
    [property: JsonPropertyName("evidence_url")] string? EvidenceUrl = null,
    [property: JsonPropertyName("contact_consent")] bool? ContactConsent = null,
    [property: JsonPropertyName("contact_email")] string? ContactEmail = null
)
{
    /// <summary>Reads the snake_case <c>created_at</c> spelling into <see cref="CreatedAt"/>.</summary>
    [JsonPropertyName("created_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? CreatedAtSnakeCase
    {
        get => null;
        init
        {
            if (string.IsNullOrEmpty(CreatedAt) && value is not null)
            {
                CreatedAt = value;
            }
        }
    }
}

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
/// Parsed user-scoped rate-limit headers (<c>X-RateLimit-Limit</c>, <c>-Remaining</c>, <c>-Reset</c>, <c>-Scope</c>).
/// <see cref="ResetAfterSeconds"/> is the number of seconds until the window resets
/// (<c>X-RateLimit-Reset</c> is relative, not an epoch).
/// </summary>
public sealed record RateLimitMetadata(
    int? LimitPerMinute = null,
    int? Remaining = null,
    long? ResetEpochSeconds = null,
    string? Scope = null,
    int? BurstCapacity = null,
    int? RateLimitRps = null,
    long? ResetAfterSeconds = null
)
{
    /// <summary>1.0.x constructor, kept for binary compatibility.</summary>
    public RateLimitMetadata(
        int? LimitPerMinute,
        int? Remaining,
        long? ResetEpochSeconds,
        string? Scope,
        int? BurstCapacity,
        int? RateLimitRps
    ) : this(LimitPerMinute, Remaining, ResetEpochSeconds, Scope, BurstCapacity, RateLimitRps, ResetAfterSeconds: null)
    {
    }

    /// <summary>Seconds until the current rate-limit window resets (<c>X-RateLimit-Reset</c>).</summary>
    public long? ResetAfterSeconds { get; init; } = ResetAfterSeconds ?? ResetEpochSeconds;

    /// <summary>Same value as <see cref="ResetAfterSeconds"/>; it was never a Unix epoch.</summary>
    [Obsolete("X-RateLimit-Reset is seconds until reset, not an epoch. Use ResetAfterSeconds.")]
    public long? ResetEpochSeconds { get; init; } = ResetEpochSeconds ?? ResetAfterSeconds;

    /// <summary>Always <c>null</c>: the API sends no burst header.</summary>
    [Obsolete("The API does not send a burst header; always null.")]
    public int? BurstCapacity { get; init; } = BurstCapacity;

    /// <summary>Always <c>null</c>: the API sends no RPS header.</summary>
    [Obsolete("The API does not send an RPS header; always null.")]
    public int? RateLimitRps { get; init; } = RateLimitRps;
}

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
