# Changelog

All notable changes to the `IpScope.Sdk` NuGet package are documented here. The project follows
[Semantic Versioning](https://semver.org/).

## [1.1.0] - 2026-11-10

### Added
- `GeoBlock`: `Latitude`, `Longitude`, `PostalCode`.
- `SecurityBlock`: `IsRelay` (consumer privacy relays such as iCloud Private Relay; not reported as VPN/proxy/hosting).
- `NetworkPrefixLookupResponse`: `MatchedNetwork`, `BgpPrefixLength`, `Lookup` (full `IpLookupResponse` for the prefix
  base address).
- `CanonicalNetworkPrefixRecord`: `Subdivision`, `PostalCode`, `Latitude`, `Longitude`, `Timezone`, `GeoConfidence`,
  `AccuracyRadiusKm`, `IsVirtualLocation`, `ConnectionType`, `IsRelay`, `SecurityConfidence`, `AbuseVelocityScore`,
  `IsKnownScanner`, `ReasonCodes`, `UpdatedAt`.
- `FeedbackSubmissionInput`: `EvidenceUrl`, `ContactConsent`, `ContactEmail` (optional, omitted from the request when
  `null`) and `EffectiveEvidenceUrl`.
- `FeedbackSubmissionResponse`: `EvidenceUrl`, `ContactConsent`, `ContactEmail`.
- `FeedbackValues.Roles` and `FeedbackValues.IssueTypes` listing the values the API accepts.
- `RateLimitMetadata.ResetAfterSeconds`.
- `IpScopeClient.SdkVersion` and `IpScopeClient.SdkUserAgent`.

### Fixed
- Network record fields were always `null`: the API sends the record in camelCase while the SDK expected snake_case.
  A converter now reads both.
- `FeedbackSubmissionResponse.CreatedAt` was always `null`: the API sends `createdAt`. Both spellings are read.
- Feedback evidence was sent as `evidence_urls` (array), which the API silently dropped. The SDK now sends
  `evidence_url` (single string).
- `X-RateLimit-Reset` is seconds until reset, not a Unix epoch. 429 retry delays derived from it were wrong; they now
  use the relative value.
- `AbuseVelocityScore` is documented as a 0-100 score (integer on the wire); fractional legacy values still parse.
- README examples compile against 1.1.0 (the stream example used an undefined variable).
- `User-Agent` is now `ipscope-dotnet/1.1.0`.

### Deprecated
- `FeedbackSubmissionInput.EvidenceUrls` and the 5-argument constructor: the first non-blank entry is sent as
  `evidence_url` when `EvidenceUrl` is `null`.
- `RateLimitMetadata.ResetEpochSeconds`: same relative seconds value as `ResetAfterSeconds`.
- `RateLimitMetadata.BurstCapacity` and `RateLimitRps`: the API sends no such headers; always `null`.

### Compatibility notes
- New record parameters are optional and appended, so existing source compiles unchanged. Response records
  (`GeoBlock`, `SecurityBlock`, `NetworkPrefixLookupResponse`, `CanonicalNetworkPrefixRecord`,
  `FeedbackSubmissionResponse`) have new constructor signatures, so code that constructs them (for example test
  fakes) must be recompiled; positional deconstruction of those records now has more elements.
  `FeedbackSubmissionInput` and `RateLimitMetadata` keep their 1.0.x constructors.
- Serializing `CanonicalNetworkPrefixRecord` with `System.Text.Json` now writes the API's camelCase keys, and
  `FeedbackSubmissionResponse.CreatedAt` is written as `createdAt`.

## [1.0.0]

- Initial release.
