# `IpScope.Sdk` - Official IPScope Server-Side .NET SDK

Official zero-dependency, thread-safe, strictly typed server-side **.NET / C# SDK** (`IpScope.Sdk`) for the **IPScope IP Intelligence Platform**, supporting both **Asynchronous (`Task` / `IAsyncEnumerable`)** and **Synchronous** execution.

> **Scope Notice:** This SDK is intentionally scoped strictly to **IP intelligence, network prefix, dataset metadata, feedback, and health operations**. It contains **zero account, billing, wallet, or key-management operations**, making it safe to deploy in production ASP.NET Core services, background workers, and API gateways.

---

## Features

- **Zero External Runtime Dependencies**: Built entirely on the .NET Base Class Library (`System.Net.Http.HttpClient`, `SocketsHttpHandler`, `System.Text.Json`, `IAsyncEnumerable<T>`, C# `sealed record` types).
- **Both Async & Sync Clients**:
  - Async methods (`LookupIpAsync`, `LookupIpWithMetaAsync`, `LookupCallerIpAsync`, `LookupBatchAsync`, `LookupBatchChunkedAsync`, `LookupBatchStreamAsync` returning `IAsyncEnumerable<BatchLookupResponse>`, `LookupNetworkAsync`, `GetDatasetsMetadataAsync`, `SubmitFeedbackAsync`, `GetStatusAsync`).
  - Sync methods (`LookupIp`, `LookupIpWithMeta`, `LookupCallerIp`, `LookupBatch`, `LookupBatchChunked`, `LookupBatchStream` returning `IEnumerable<BatchLookupResponse>`, `LookupNetwork`, `GetDatasetsMetadata`, `SubmitFeedback`, `GetStatus`) using native `HttpClient.Send` without thread-pool blocking.
- **Low-RAM & Zero Memory/Socket Leaks**:
  - Thread-safe O(1) LRU + TTL cache (`MaxEntries = 512` default, `< 400 KB` heap footprint) with `Task<T>` singleflight request coalescing.
  - Hard `2 MiB` (`MaxResponseBytes`) streaming body guard via `HttpCompletionOption.ResponseHeadersRead` prevents OOM on malformed upstream payloads.
  - Pooled `SocketsHttpHandler` (`PooledConnectionLifetime = 2m`, `MaxConnectionsPerServer = 20`) prevents socket exhaustion and DNS staleness.

---

## Installation

```bash
dotnet add package IpScope.Sdk --version 1.1.0
```

API keys have the form `<public>:<private>` (a UUID, a colon, and a 43-character base64url secret).

---

## Quick Start (Asynchronous)

```csharp
using IpScope.Sdk;

using var client = new IpScopeClient("<public>:<private>");
List<string> largeIpList = Enumerable.Range(1, 254).Select(i => $"192.0.2.{i}").ToList();

// 1. Single IPv4 or IPv6 lookup (cached in LRU cache & singleflight-coalesced)
IpLookupResponse ipInfo = await client.LookupIpAsync("8.8.8.8");
Console.WriteLine($"{ipInfo.Ip} {ipInfo.Geo.CountryCode} AS{ipInfo.Network.Asn}");

// 2. Batch lookup (up to 100 IPs; deduplicates & reuses cached entries automatically)
BatchLookupResponse batch = await client.LookupBatchAsync(
    ["8.8.8.8", "1.1.1.1", "2606:4700:4700::1111"]
);
foreach (IpLookupResponse record in batch.Results)
{
    Console.WriteLine($"{record.Ip} -> {record.Network.Organization}");
}

// 3. Chunked batch lookup for >100 IPs with bounded async concurrency (default: 4)
BatchLookupResponse largeBatch = await client.LookupBatchChunkedAsync(
    ["8.8.8.8", "1.1.1.1", "9.9.9.9"],
    concurrency: 4
);

// 4. Constant-RAM IAsyncEnumerable stream for 10,000+ IPs
await foreach (BatchLookupResponse chunk in client.LookupBatchStreamAsync(largeIpList))
{
    foreach (IpLookupResponse record in chunk.Results)
    {
        Console.WriteLine(record.Ip);
    }
}

// 5. Network CIDR prefix lookup
NetworkPrefixLookupResponse prefix = await client.LookupNetworkAsync("8.8.8.0/24");
Console.WriteLine($"{prefix.QueriedCidr} matched {prefix.MatchedNetwork} (/{prefix.BgpPrefixLength}) in {prefix.DatasetVersion}");
if (prefix.Record is { } networkRecord)
{
    Console.WriteLine($"record: {networkRecord.CountryCode} hosting={networkRecord.IsHosting}");
}
if (prefix.Lookup is { } baseLookup)
{
    Console.WriteLine($"base address city: {baseLookup.Geo.City}");
}
```

---

## Reading the Response

```csharp
using IpScope.Sdk;

using var client = new IpScopeClient("<public>:<private>");
IpLookupResponse info = await client.LookupIpAsync("8.8.8.8");

// Representative coordinates (never device GPS) and postal code; null when unknown
Console.WriteLine($"{info.Geo.Latitude}, {info.Geo.Longitude} {info.Geo.PostalCode}");

// Consumer privacy relays (e.g. iCloud Private Relay) are flagged separately from VPN/proxy
Console.WriteLine($"relay={info.Security.IsRelay} vpn={info.Security.IsVpn}");

// Abuse velocity score on a 0-100 scale (present when security_extensions is licensed)
if (info.SecurityExtensions is { } ext)
{
    Console.WriteLine($"abuse score: {ext.AbuseVelocityScore}");
}
```

---

## Rate-Limit Metadata

```csharp
using IpScope.Sdk;

using var client = new IpScopeClient("<public>:<private>");
IpScopeResultWithMeta<IpLookupResponse> res = await client.LookupIpWithMetaAsync("1.1.1.1");

// X-RateLimit-Limit / -Remaining / -Reset / -Scope.
// ResetAfterSeconds is the number of seconds until the window resets (not an epoch).
RateLimitMetadata rl = res.RateLimit;
Console.WriteLine($"{rl.Remaining}/{rl.LimitPerMinute} left, resets in {rl.ResetAfterSeconds}s (scope {rl.Scope})");
```

---

## Submitting Feedback

`Role` is one of `network_owner`, `user`, `researcher` (`FeedbackValues.Roles`); `IssueType` is one of
`wrong_country`, `wrong_city`, `false_positive_vpn`, `false_positive_hosting`, `false_positive_tor`,
`virtual_location_mismatch`, `other` (`FeedbackValues.IssueTypes`).

```csharp
using IpScope.Sdk;

using var client = new IpScopeClient("<public>:<private>");

var feedback = new FeedbackSubmissionInput(
    "203.0.113.0/24",
    "network_owner",
    "wrong_country",
    new Dictionary<string, object?> { ["country_code"] = "DE" },
    EvidenceUrl: "https://example.net/geofeed.csv",
    ContactConsent: true,
    ContactEmail: "noc@example.net"
);

FeedbackSubmissionResponse receipt = await client.SubmitFeedbackAsync(feedback);
Console.WriteLine($"{receipt.Id} {receipt.Status} {receipt.CreatedAt}");
```

---

## Quick Start (Synchronous)

```csharp
using IpScope.Sdk;

using var client = new IpScopeClient("<public>:<private>");

IpLookupResponse ipInfo = client.LookupIp("1.1.1.1");
Console.WriteLine($"{ipInfo.Ip} -> {ipInfo.Network.Organization}");

BatchLookupResponse batch = client.LookupBatch(["8.8.8.8", "1.1.1.1"]);
foreach (IpLookupResponse record in batch.Results)
{
    Console.WriteLine(record.Ip);
}
```
