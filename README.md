<div align="center">
  <br />
  <a href="https://ipscope.io">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/ipscope-io/.github/main/assets/logo-dark.svg" />
      <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/ipscope-io/.github/main/assets/logo-light.svg" />
      <img alt="ipscope" src="https://raw.githubusercontent.com/ipscope-io/.github/main/assets/logo-light.svg" height="32" />
    </picture>
  </a>
  <h1>IpScope.Sdk for .NET</h1>
  <p><strong>Official zero-dependency .NET 8+ server SDK for ipscope — IP geolocation, BGP ASN, and VPN/proxy detection</strong></p>
  <p>
    <a href="https://www.nuget.org/packages/IpScope.Sdk"><img src="https://img.shields.io/nuget/v/IpScope.Sdk.svg?style=flat-square&label=nuget&color=0f172a" alt="NuGet version" /></a>
    <img src="https://img.shields.io/badge/.NET-%3E%3D8.0-0f172a?style=flat-square" alt=".NET >= 8.0" />
    <img src="https://img.shields.io/badge/dependencies-zero-0f172a?style=flat-square" alt="Zero dependencies" />
    <a href="LICENSE"><img src="https://img.shields.io/badge/licence-MIT-0f172a?style=flat-square" alt="MIT licence" /></a>
    <a href="https://ipscope.io/docs/sdks/dotnet"><img src="https://img.shields.io/badge/docs-ipscope.io-0f172a?style=flat-square" alt="Documentation" /></a>
  </p>
  <br />
  <a href="https://ipscope.io">
    <img alt="ipscope .NET SDK" src="https://raw.githubusercontent.com/ipscope-io/.github/main/assets/banner.webp" width="100%" />
  </a>
</div>

<br />

## What is this

`IpScope.Sdk` is the official, zero-external-dependency .NET 8+ / C# server SDK for [ipscope](https://ipscope.io). Built entirely on the .NET Base Class Library (`SocketsHttpHandler`, `System.Text.Json`, `IAsyncEnumerable<T>`, and C# `sealed record` types), it queries the [ipscope REST API](https://ipscope.io/products/api) for physical and declared [IP geolocation](https://ipscope.io/products/geolocation), [BGP origin ASN and prefix routing](https://ipscope.io/products/network), and [VPN, residential proxy, Tor exit, and relay detection](https://ipscope.io/products/privacy).

Every `IpScopeClient` supports both asynchronous (`Task` / `IAsyncEnumerable`) and synchronous (`HttpClient.Send`) execution, includes a thread-safe `O(1)` LRU + TTL cache (`MaxEntries = 512` default, `< 400 KB` heap footprint) with `Task<T>` singleflight coalescing, and enforces a `2 MiB` streaming body guard via `HttpCompletionOption.ResponseHeadersRead`. For offline in-process lookups in .NET, see our [downloadable MMDB databases](https://ipscope.io/docs/databases/mmdb).

---

## Getting started

### 1. Get an API key

Create a free account at [dashboard.ipscope.io](https://dashboard.ipscope.io/auth/login) and copy your server API key (`<public>:<private>`).

### 2. Install the package

```bash
dotnet add package IpScope.Sdk --version 1.1.0
```

### 3. Look up an IP address

```csharp
using IpScope.Sdk;

using var client = new IpScopeClient(Environment.GetEnvironmentVariable("IPSCOPE_API_KEY")!);

IpLookupResponse info = await client.LookupIpAsync("8.8.8.8");
Console.WriteLine($"{info.Ip} {info.Geo.CountryCode} AS{info.Network.Asn} {info.Network.Organization}");
```

---

## Contents

- [Asynchronous lookups](#asynchronous-lookups)
- [Synchronous lookups](#synchronous-lookups)
- [Network CIDR prefix lookups](#network-cidr-prefix-lookups)
- [Reading the response](#reading-the-response)
- [Rate-limit metadata](#rate-limit-metadata)
- [Submitting feedback](#submitting-feedback)
- [Requirements](#requirements)
- [Documentation](#documentation)
- [Help](#help)
- [Licence](#licence)

---

## Asynchronous lookups

```csharp
using IpScope.Sdk;

using var client = new IpScopeClient("<public>:<private>");

// 1. Single IPv4 or IPv6 lookup (cached in LRU & singleflight-coalesced)
IpLookupResponse ipInfo = await client.LookupIpAsync("8.8.8.8");

// 2. Caller IP lookup (GET /v1/ip/me)
IpLookupResponse caller = await client.LookupCallerIpAsync();

// 3. Batch lookup (up to 100 IPs; deduplicates & reuses cached entries automatically)
BatchLookupResponse batch = await client.LookupBatchAsync(
    ["8.8.8.8", "1.1.1.1", "2606:4700:4700::1111"]
);
foreach (IpLookupResponse record in batch.Results)
{
    Console.WriteLine($"{record.Ip} -> {record.Network.Organization}");
}

// 4. Chunked batch lookup for >100 IPs with bounded async concurrency (default: 4)
BatchLookupResponse largeBatch = await client.LookupBatchChunkedAsync(
    ["8.8.8.8", "1.1.1.1", "9.9.9.9"],
    concurrency: 4
);

// 5. Constant-RAM IAsyncEnumerable stream for 10,000+ IPs
List<string> largeIpList = Enumerable.Range(1, 254).Select(i => $"192.0.2.{i}").ToList();
await foreach (BatchLookupResponse chunk in client.LookupBatchStreamAsync(largeIpList))
{
    foreach (IpLookupResponse record in chunk.Results)
    {
        Console.WriteLine(record.Ip);
    }
}
```

---

## Synchronous lookups

All synchronous methods use native `HttpClient.Send` without thread-pool blocking:

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

---

## Network CIDR prefix lookups

Inspect the longest-matching routed BGP prefix and its base address intelligence via `LookupNetworkAsync`:

```csharp
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

## Reading the response

```csharp
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

## Rate-limit metadata

Use `LookupIpWithMetaAsync` (or any `*WithMeta*` method) to inspect the parsed `X-RateLimit-*` headers:

```csharp
IpScopeResultWithMeta<IpLookupResponse> res = await client.LookupIpWithMetaAsync("1.1.1.1");
RateLimitMetadata rl = res.RateLimit;
Console.WriteLine($"{rl.Remaining}/{rl.LimitPerMinute} left, resets in {rl.ResetAfterSeconds}s (scope {rl.Scope})");
```

---

## Submitting feedback

Report outdated geolocation or anonymity classifications directly via `SubmitFeedbackAsync`:

```csharp
var feedback = new FeedbackSubmissionInput(
    "203.0.113.0/24",
    "network_owner", // FeedbackValues.Roles: network_owner | user | researcher
    "wrong_country", // FeedbackValues.IssueTypes
    new Dictionary<string, object?> { ["country_code"] = "DE" },
    EvidenceUrl: "https://example.net/geofeed.csv",
    ContactConsent: true,
    ContactEmail: "noc@example.net"
);

FeedbackSubmissionResponse receipt = await client.SubmitFeedbackAsync(feedback);
Console.WriteLine($"{receipt.Id} {receipt.Status} {receipt.CreatedAt}");
```

---

## Requirements

- .NET 8.0 or later (zero external NuGet dependencies).

---

## Documentation

| Topic | Link |
| :--- | :--- |
| .NET SDK guide | [ipscope.io/docs/sdks/dotnet](https://ipscope.io/docs/sdks/dotnet) |
| REST API overview | [ipscope.io/docs/api/overview](https://ipscope.io/docs/api/overview) |
| Single & batch IP lookups | [ipscope.io/docs/api/single-ip](https://ipscope.io/docs/api/single-ip) |
| Network CIDR prefix lookups | [ipscope.io/docs/api/network-prefix](https://ipscope.io/docs/api/network-prefix) |
| Errors & rate limits | [ipscope.io/docs/api/errors](https://ipscope.io/docs/api/errors) |
| Downloadable MMDB databases | [ipscope.io/docs/databases/mmdb](https://ipscope.io/docs/databases/mmdb) |

---

## Help

- Bugs and feature requests: [open an issue](https://github.com/ipscope-io/ipscope-dotnet/issues)
- Account, billing and integration questions: [contact@ipscope.io](mailto:contact@ipscope.io)
- Security vulnerabilities: see the [security policy](https://github.com/ipscope-io/.github/blob/main/SECURITY.md)

---

## Licence

[MIT](LICENSE). Made by [ipscope](https://ipscope.io).
