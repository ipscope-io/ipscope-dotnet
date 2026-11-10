using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using IpScope.Sdk;

namespace IpScope.Sdk.Tests;

internal sealed class MockHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler
) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) => handler(request, cancellationToken);

    protected override HttpResponseMessage Send(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) => handler(request, cancellationToken).GetAwaiter().GetResult();
}

internal static class Program
{
    private const string TestKey =
        "00000000-0000-4000-8000-000000000000:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public static async Task Main()
    {
        TestValidationAndStrictIpScope();
        await TestAsyncSingleflightAndCacheAsync();
        await TestBatchDeduplicationChunkedAndStreamAsync();
        TestSyncLookupAndEndpoints();
        await TestRetriesAuthErrorOversizedGuardAndLruBoundsAsync();
        TestWireCompatibilityOfNewFields();
        Console.WriteLine("ALL 6 .NET SDK TEST SUITES PASSED!");
    }

    private static Dictionary<string, object?> MakeMockIpRecord(string ip)
    {
        int version = ip.Contains(':') ? 6 : 4;
        string cidr = version == 6 ? "2001:4860:4860::/48" : "8.8.8.0/24";
        return new Dictionary<string, object?>
        {
            ["ip"] = ip,
            ["ip_version"] = version,
            ["matched_network"] = cidr,
            ["geo"] = new Dictionary<string, object?>
            {
                ["country_code"] = "US",
                ["subdivision"] = "California",
                ["city"] = "Mountain View",
                ["postal_code"] = "94043",
                ["latitude"] = 37.422,
                ["longitude"] = -122.085,
                ["timezone"] = "America/Los_Angeles",
                ["location_type"] = "anycast",
                ["confidence"] = 0.96,
                ["accuracy_radius_km"] = 20.0,
                ["declared_country_code"] = "US",
                ["physical_country_code"] = "US",
                ["is_virtual_location"] = false
            },
            ["network"] = new Dictionary<string, object?>
            {
                ["asn"] = 15169,
                ["organization"] = "Google LLC"
            },
            ["security"] = new Dictionary<string, object?>
            {
                ["is_vpn"] = false,
                ["is_hosting"] = true,
                ["is_tor_exit"] = false,
                ["is_proxy"] = false,
                ["is_relay"] = false,
                ["anonymizer_type"] = "none",
                ["confidence"] = 0.99,
                ["reason_codes"] = new[] { "ASN_HOSTING" }
            },
            ["connection"] = new Dictionary<string, object?>
            {
                ["connection_type"] = "hosting",
                ["is_residential"] = false,
                ["is_mobile"] = false,
                ["is_business"] = false,
                ["is_hosting"] = true
            },
            ["security_extensions"] = new Dictionary<string, object?>
            {
                ["abuse_velocity_score"] = 80,
                ["is_known_scanner"] = false,
                ["observation_recency_hours"] = 1.5
            },
            ["dataset_version"] = "2026.10.25-001",
            ["updated_at"] = "2026-10-25T00:00:00Z"
        };
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode status, object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        var resp = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        resp.Headers.TryAddWithoutValidation("x-ratelimit-limit", "1000");
        resp.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "998");
        resp.Headers.TryAddWithoutValidation("x-ratelimit-reset", "42");
        resp.Headers.TryAddWithoutValidation("x-ratelimit-scope", "user");
        return resp;
    }

    private static void TestValidationAndStrictIpScope()
    {
        AssertEqual(TestKey, IpScopeValidator.ValidateApiKey("  " + TestKey + "  "));
        AssertThrows<IpScopeValidationException>(() => IpScopeValidator.ValidateApiKey("   "));
        AssertThrows<IpScopeValidationException>(() => IpScopeValidator.ValidateApiKey("bad\nheader"));
        AssertEqual("ipscope-dotnet/1.1.0", IpScopeClient.SdkUserAgent);

        var v4 = IpScopeValidator.ValidateIpAddress(" 8.8.8.8 ");
        AssertEqual("8.8.8.8", v4.CanonicalIp);
        AssertEqual(4, v4.Family);

        var v6 = IpScopeValidator.ValidateIpAddress("2001:0db8:0000:0000:0000:0000:0000:0001");
        AssertEqual("2001:db8::1", v6.CanonicalIp);
        AssertEqual(6, v6.Family);

        AssertThrows<IpScopeValidationException>(() => IpScopeValidator.ValidateIpAddress("999.999.999.999"));
        AssertThrows<IpScopeValidationException>(() => IpScopeValidator.ValidateIpAddress("123456"));

        var cidr = IpScopeValidator.ValidateCidrPrefix("8.8.8.99/24");
        AssertEqual("8.8.8.0", cidr.Address);
        AssertEqual(24, cidr.PrefixLength);
        AssertEqual("8.8.8.0/24", cidr.CanonicalCidr);

        string[] forbidden =
        [
            "GetWallet", "TopUpWallet", "CreateCheckout", "ListApiKeys",
            "CreateApiKey", "RevokeApiKey", "Login", "Register", "GetBilling"
        ];
        foreach (MethodInfo method in typeof(IpScopeClient).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (string f in forbidden)
            {
                if (method.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"IpScopeClient must not expose account/billing method {method.Name}");
                }
            }
        }
        Console.WriteLine("  [PASS] TestValidationAndStrictIpScope");
    }

    private static async Task TestAsyncSingleflightAndCacheAsync()
    {
        int networkCalls = 0;
        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            Interlocked.Increment(ref networkCalls);
            await Task.Delay(25, ct);
            return CreateJsonResponse(HttpStatusCode.OK, MakeMockIpRecord("8.8.8.8"));
        });

        using var httpClient = new HttpClient(handler);
        using var client = new IpScopeClient(
            TestKey,
            new IpScopeClientOptions(HttpClient: httpClient)
        );

        var tasks = Enumerable.Range(0, 25).Select(_ => client.LookupIpAsync("8.8.8.8")).ToArray();
        IpLookupResponse[] results = await Task.WhenAll(tasks);

        AssertEqual(1, networkCalls);
        AssertEqual("8.8.8.8", results[0].Ip);
        AssertEqual("US", results[0].Geo.CountryCode);
        AssertEqual(15169, results[0].Network.Asn);
        AssertTrue(client.GetCacheStats().CoalescedInFlightHits > 0, "Expected singleflight hits");

        RateLimitMetadata rl = client.GetLastRateLimit()!;
        AssertEqual(1000, rl.LimitPerMinute);
        AssertEqual(998, rl.Remaining);
        AssertEqual(42L, rl.ResetAfterSeconds);
        AssertEqual("user", rl.Scope);
#pragma warning disable CS0618
        AssertEqual(42L, rl.ResetEpochSeconds);
        AssertEqual<int?>(null, rl.BurstCapacity);
        AssertEqual<int?>(null, rl.RateLimitRps);
        AssertEqual(30L, new RateLimitMetadata(10, 9, 30L, "user", null, null).ResetAfterSeconds);
#pragma warning restore CS0618

        IpLookupResponse first = results[0];
        AssertEqual(37.422, first.Geo.Latitude);
        AssertEqual(-122.085, first.Geo.Longitude);
        AssertEqual("94043", first.Geo.PostalCode);
        AssertEqual(false, first.Security.IsRelay);
        AssertEqual(80.0, first.SecurityExtensions?.AbuseVelocityScore);

        Console.WriteLine("  [PASS] TestAsyncSingleflightAndCacheAsync");
    }

    private static async Task TestBatchDeduplicationChunkedAndStreamAsync()
    {
        var sentBatches = new List<List<string>>();
        var syncObj = new object();

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/ip/8.8.8.8")
            {
                return CreateJsonResponse(HttpStatusCode.OK, MakeMockIpRecord("8.8.8.8"));
            }
            if (path == "/v1/ip/batch")
            {
                string body = await req.Content!.ReadAsStringAsync(ct);
                using JsonDocument doc = JsonDocument.Parse(body);
                var ips = doc.RootElement.GetProperty("ips").EnumerateArray().Select(x => x.GetString()!).ToList();
                lock (syncObj)
                {
                    sentBatches.Add(ips);
                }
                var records = ips.Select(MakeMockIpRecord).ToList();
                return CreateJsonResponse(HttpStatusCode.OK, new Dictionary<string, object?>
                {
                    ["dataset_version"] = "2026.10.25-001",
                    ["count"] = records.Count,
                    ["results"] = records
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(handler);
        using var client = new IpScopeClient(
            TestKey,
            new IpScopeClientOptions(HttpClient: httpClient)
        );

        // Prime cache with 8.8.8.8
        await client.LookupIpAsync("8.8.8.8");

        BatchLookupResponse batch = await client.LookupBatchAsync(
            ["8.8.8.8", "1.1.1.1", "8.8.8.8", "1.1.1.1", "9.9.9.9"]
        );
        AssertEqual(5, batch.Count);
        AssertEqual(1, sentBatches.Count);
        AssertEqual(2, sentBatches[0].Count);
        AssertEqual("1.1.1.1", sentBatches[0][0]);
        AssertEqual("9.9.9.9", sentBatches[0][1]);

        var largeIps = Enumerable.Range(1, 250).Select(i => $"10.0.0.{i}").ToList();
        BatchLookupResponse chunked = await client.LookupBatchChunkedAsync(
            largeIps,
            concurrency: 3,
            options: new RequestOptions(BypassCache: true)
        );
        AssertEqual(250, chunked.Count);

        int streamedTotal = 0;
        await foreach (BatchLookupResponse chunk in client.LookupBatchStreamAsync(
                           largeIps,
                           new RequestOptions(BypassCache: true)))
        {
            streamedTotal += chunk.Count;
        }
        AssertEqual(250, streamedTotal);

        Console.WriteLine("  [PASS] TestBatchDeduplicationChunkedAndStreamAsync");
    }

    private static void TestSyncLookupAndEndpoints()
    {
        var feedbackBodies = new List<JsonElement>();
        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/feedback")
            {
                string body = await req.Content!.ReadAsStringAsync(ct);
                using JsonDocument doc = JsonDocument.Parse(body);
                lock (feedbackBodies)
                {
                    feedbackBodies.Add(doc.RootElement.Clone());
                }
                // Echo the payload the way the API does: dto spread + id, camelCase createdAt, status.
                var echo = JsonSerializer.Deserialize<Dictionary<string, object?>>(body)!;
                echo["id"] = "fb_dotnet_999";
                echo["createdAt"] = "2026-10-25T10:00:00Z";
                echo["status"] = "pending_review";
                return CreateJsonResponse(HttpStatusCode.Created, echo);
            }
            return path switch
            {
                "/v1/ip/8.8.8.8" => CreateJsonResponse(HttpStatusCode.OK, MakeMockIpRecord("8.8.8.8")),
                "/v1/ip/me" => CreateJsonResponse(HttpStatusCode.OK, MakeMockIpRecord("203.0.113.42")),
                "/v1/networks/8.8.8.0/24" => CreateJsonResponse(HttpStatusCode.OK, new Dictionary<string, object?>
                {
                    ["queried_cidr"] = "8.8.8.0/24",
                    ["matched_network"] = "8.8.8.0/24",
                    ["family"] = 4,
                    ["bgp_prefix_length"] = 24,
                    ["dataset_version"] = "2026.10.25-001",
                    ["record"] = new Dictionary<string, object?>
                    {
                        ["family"] = 4,
                        ["prefix"] = "8.8.8.0/24",
                        ["asn"] = 15169,
                        ["organization"] = "Google LLC",
                        ["locations"] = new Dictionary<string, object?>(),
                        ["countryCode"] = "US",
                        ["subdivision"] = "California",
                        ["city"] = "Mountain View",
                        ["postalCode"] = "94043",
                        ["latitude"] = 37.422,
                        ["longitude"] = -122.085,
                        ["timezone"] = "America/Los_Angeles",
                        ["locationType"] = "declared",
                        ["geoConfidence"] = 0.85,
                        ["accuracyRadiusKm"] = 300,
                        ["isVirtualLocation"] = null,
                        ["connectionType"] = "hosting",
                        ["isHosting"] = true,
                        ["isVpn"] = false,
                        ["isTorExit"] = false,
                        ["isProxy"] = false,
                        ["anonymizerType"] = "none",
                        ["securityConfidence"] = 0.85,
                        ["abuseVelocityScore"] = 80,
                        ["isKnownScanner"] = false,
                        ["reasonCodes"] = new[] { "KNOWN_HOSTING_INFRASTRUCTURE_RANGE" },
                        ["sourceIds"] = new[] { "bgp" },
                        ["licensePolicyIds"] = new[] { "cc0" },
                        ["updatedAt"] = "2026-11-10T03:02:07.437Z"
                    },
                    ["lookup"] = MakeMockIpRecord("8.8.8.0")
                }),
                "/v1/metadata/datasets" => CreateJsonResponse(HttpStatusCode.OK, new Dictionary<string, object?>
                {
                    ["active_version"] = "2026.10.25-001",
                    ["previous_version"] = "2026.10.24-001",
                    ["snapshots"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["snapshot_version"] = "2026.10.25-001",
                            ["digest"] = "sha256:abc123",
                            ["ruleset_version"] = "v1",
                            ["published_at"] = "2026-10-25T00:00:00Z",
                            ["ipv4_prefix_count"] = 950000,
                            ["ipv6_prefix_count"] = 220000
                        }
                    }
                }),
                "/v1/status" => CreateJsonResponse(HttpStatusCode.OK, new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["snapshot"] = new Dictionary<string, object?>
                    {
                        ["currentVersion"] = "2026.10.25-001",
                        ["previousVersion"] = "2026.10.24-001"
                    }
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

        using var httpClient = new HttpClient(handler);
        using var client = new IpScopeClient(
            TestKey,
            new IpScopeClientOptions(HttpClient: httpClient)
        );

        IpLookupResponse ipRes = client.LookupIp("8.8.8.8");
        AssertEqual("8.8.8.8", ipRes.Ip);

        IpLookupResponse callerRes = client.LookupCallerIp();
        AssertEqual("203.0.113.42", callerRes.Ip);

        NetworkPrefixLookupResponse netRes = client.LookupNetwork("8.8.8.0/24");
        AssertEqual("8.8.8.0/24", netRes.QueriedCidr);
        AssertEqual("8.8.8.0/24", netRes.MatchedNetwork);
        AssertEqual(24, netRes.BgpPrefixLength);
        AssertEqual("8.8.8.0", netRes.Lookup?.Ip);
        AssertEqual(37.422, netRes.Lookup?.Geo.Latitude);
        CanonicalNetworkPrefixRecord rec = netRes.Record!;
        AssertEqual(15169, rec.Asn);
        AssertEqual("US", rec.CountryCode);
        AssertEqual("California", rec.Subdivision);
        AssertEqual("Mountain View", rec.City);
        AssertEqual("94043", rec.PostalCode);
        AssertEqual(37.422, rec.Latitude);
        AssertEqual("declared", rec.LocationType);
        AssertEqual(0.85, rec.GeoConfidence);
        AssertEqual(300.0, rec.AccuracyRadiusKm);
        AssertEqual("hosting", rec.ConnectionType);
        AssertEqual(true, rec.IsHosting);
        AssertEqual(false, rec.IsVpn);
        AssertEqual(false, rec.IsTorExit);
        AssertEqual(false, rec.IsProxy);
        AssertEqual("none", rec.AnonymizerType);
        AssertEqual(0.85, rec.SecurityConfidence);
        AssertEqual(80.0, rec.AbuseVelocityScore);
        AssertEqual(false, rec.IsKnownScanner);
        AssertEqual("KNOWN_HOSTING_INFRASTRUCTURE_RANGE", rec.ReasonCodes?[0]);
        AssertEqual("2026-11-10T03:02:07.437Z", rec.UpdatedAt);

        DatasetsMetadataResponse metaRes = client.GetDatasetsMetadata();
        AssertEqual("2026.10.25-001", metaRes.ActiveVersion);

        FeedbackSubmissionResponse fbRes = client.SubmitFeedback(new FeedbackSubmissionInput(
            "8.8.8.8",
            "network_owner",
            "wrong_country",
            new Dictionary<string, object?> { ["country_code"] = "US" },
            EvidenceUrl: "https://example.net/geofeed.csv",
            ContactConsent: true,
            ContactEmail: "noc@example.net"
        ));
        AssertEqual("fb_dotnet_999", fbRes.Id);
        AssertEqual("2026-10-25T10:00:00Z", fbRes.CreatedAt);
        AssertEqual("network_owner", fbRes.Role);
        AssertEqual("https://example.net/geofeed.csv", fbRes.EvidenceUrl);
        AssertEqual(true, fbRes.ContactConsent);
        AssertEqual("noc@example.net", fbRes.ContactEmail);

        JsonElement sent = feedbackBodies[0];
        AssertEqual("https://example.net/geofeed.csv", sent.GetProperty("evidence_url").GetString());
        AssertEqual(true, sent.GetProperty("contact_consent").GetBoolean());
        AssertEqual("noc@example.net", sent.GetProperty("contact_email").GetString());
        AssertTrue(!sent.TryGetProperty("evidence_urls", out _), "evidence_urls must not be sent");

        // 1.0.x shape: the first EvidenceUrls entry is sent as evidence_url; unset fields are omitted.
#pragma warning disable CS0618
        var legacy = new FeedbackSubmissionInput(
            "8.8.8.0/24",
            "researcher",
            "other",
            new Dictionary<string, object?>(),
            new[] { " ", "https://example.net/rdap" }
        );
#pragma warning restore CS0618
        client.SubmitFeedback(legacy);
        JsonElement legacySent = feedbackBodies[1];
        AssertEqual("https://example.net/rdap", legacySent.GetProperty("evidence_url").GetString());
        AssertTrue(!legacySent.TryGetProperty("evidence_urls", out _), "evidence_urls must not be sent");
        AssertTrue(!legacySent.TryGetProperty("contact_email", out _), "unset contact_email must be omitted");
        AssertTrue(!legacySent.TryGetProperty("contact_consent", out _), "unset contact_consent must be omitted");

        ServiceStatusResponse statusRes = client.GetStatus();
        AssertEqual("ok", statusRes.Status);
        AssertEqual("2026.10.25-001", statusRes.CurrentVersion);

        Console.WriteLine("  [PASS] TestSyncLookupAndEndpoints");
    }

    private static async Task TestRetriesAuthErrorOversizedGuardAndLruBoundsAsync()
    {
        int attempts = 0;

        var handler = new MockHttpMessageHandler((req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/ip/1.1.1.1")
            {
                int n = Interlocked.Increment(ref attempts);
                if (n == 1)
                {
                    var r429 = CreateJsonResponse(HttpStatusCode.TooManyRequests, new { message = "Rate limited" });
                    r429.Headers.TryAddWithoutValidation("retry-after", "0.005");
                    return Task.FromResult(r429);
                }
                if (n == 2)
                {
                    return Task.FromResult(CreateJsonResponse(HttpStatusCode.ServiceUnavailable, new { message = "Unavailable" }));
                }
                return Task.FromResult(CreateJsonResponse(HttpStatusCode.OK, MakeMockIpRecord("1.1.1.1")));
            }
            if (path == "/v1/ip/8.8.4.4")
            {
                return Task.FromResult(CreateJsonResponse(HttpStatusCode.Unauthorized, new { message = "Invalid API key" }));
            }
            if (path == "/v1/ip/9.9.9.9")
            {
                byte[] oversized = new byte[IpScopeClient.MaxResponseBytes + 128];
                var rBig = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(oversized)
                };
                return Task.FromResult(rBig);
            }
            string ip = path[(path.LastIndexOf('/') + 1)..];
            return Task.FromResult(CreateJsonResponse(HttpStatusCode.OK, MakeMockIpRecord(ip)));
        });

        using var httpClient = new HttpClient(handler);
        using var client = new IpScopeClient(
            TestKey,
            new IpScopeClientOptions(
                HttpClient: httpClient,
                Retry: new RetryOptions(
                    MaxRetries: 3,
                    BaseDelay: TimeSpan.FromMilliseconds(5),
                    MaxDelay: TimeSpan.FromMilliseconds(20)
                ),
                Cache: new CacheOptions(Enabled: true, MaxEntries: 3, Ttl: TimeSpan.FromMinutes(1))
            )
        );

        IpLookupResponse rec = await client.LookupIpAsync("1.1.1.1");
        AssertEqual("1.1.1.1", rec.Ip);
        AssertEqual(3, attempts);

        await AssertThrowsAsync<IpScopeAuthenticationException>(() => client.LookupIpAsync("8.8.4.4"));

        var sizeErr = await AssertThrowsAsync<IpScopeApiException>(() => client.LookupIpAsync("9.9.9.9"));
        AssertEqual("IPSCOPE_RESPONSE_TOO_LARGE", sizeErr.Code);

        for (int i = 1; i <= 12; i++)
        {
            await client.LookupIpAsync($"10.0.0.{i}");
        }
        AssertTrue(client.GetCacheStats().Size <= 3, "LRU cache must not exceed MaxEntries=3");

        Console.WriteLine("  [PASS] TestRetriesAuthErrorOversizedGuardAndLruBoundsAsync");
    }

    private static void TestWireCompatibilityOfNewFields()
    {
        static IpLookupResponse Parse(Dictionary<string, object?> raw) =>
            JsonSerializer.Deserialize<IpLookupResponse>(
                JsonSerializer.Serialize(raw),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        // abuse_velocity_score: integer 0-100 (new) and legacy fraction both parse.
        var raw = MakeMockIpRecord("146.75.164.1");
        ((Dictionary<string, object?>)raw["security_extensions"]!)["abuse_velocity_score"] = 0.14;
        AssertEqual(0.14, Parse(raw).SecurityExtensions?.AbuseVelocityScore);
        ((Dictionary<string, object?>)raw["security_extensions"]!)["abuse_velocity_score"] = null;
        AssertEqual<double?>(null, Parse(raw).SecurityExtensions?.AbuseVelocityScore);

        // Payloads without the new optional fields still parse.
        var geo = (Dictionary<string, object?>)raw["geo"]!;
        geo.Remove("latitude");
        geo.Remove("postal_code");
        ((Dictionary<string, object?>)raw["security"]!).Remove("is_relay");
        IpLookupResponse parsed = Parse(raw);
        AssertEqual<double?>(null, parsed.Geo.Latitude);
        AssertEqual<string?>(null, parsed.Geo.PostalCode);
        AssertEqual<bool?>(null, parsed.Security.IsRelay);

        // Feedback response reads both createdAt and created_at.
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var snake = JsonSerializer.Deserialize<FeedbackSubmissionResponse>(
            """{"id":"fb_1","prefix_or_ip":"8.8.8.8","role":"user","issue_type":"wrong_city","requested_correction":{},"created_at":"2026-10-25T10:00:00Z","status":"pending_review"}""",
            opts)!;
        AssertEqual("2026-10-25T10:00:00Z", snake.CreatedAt);
        var camel = JsonSerializer.Deserialize<FeedbackSubmissionResponse>(
            """{"id":"fb_2","prefix_or_ip":"8.8.8.8","role":"user","issue_type":"other","requested_correction":{},"createdAt":"2026-11-10T00:00:00Z","status":"pending_review"}""",
            opts)!;
        AssertEqual("2026-11-10T00:00:00Z", camel.CreatedAt);

        // Network record still reads snake_case keys; missing new fields stay null; record round-trips.
        var net = JsonSerializer.Deserialize<NetworkPrefixLookupResponse>(
            """{"queried_cidr":"10.0.0.0/8","family":4,"dataset_version":"v","record":{"prefix":"10.0.0.0/8","family":4,"country_code":"ZZ","is_vpn":false}}""",
            opts)!;
        AssertEqual("ZZ", net.Record?.CountryCode);
        AssertEqual(false, net.Record?.IsVpn);
        AssertEqual<string?>(null, net.MatchedNetwork);
        AssertTrue(net.Lookup is null, "lookup should be null when absent");
        var roundTrip = JsonSerializer.Deserialize<CanonicalNetworkPrefixRecord>(JsonSerializer.Serialize(net.Record), opts)!;
        AssertEqual("ZZ", roundTrip.CountryCode);
        var nullRecord = JsonSerializer.Deserialize<NetworkPrefixLookupResponse>(
            """{"queried_cidr":"10.0.0.0/8","family":4,"dataset_version":"v","record":null}""", opts)!;
        AssertTrue(nullRecord.Record is null, "null record should stay null");

        AssertTrue(FeedbackValues.Roles.Contains("network_owner"), "roles");
        AssertTrue(FeedbackValues.IssueTypes.Contains("virtual_location_mismatch"), "issue types");

        Console.WriteLine("  [PASS] TestWireCompatibilityOfNewFields");
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"Expected <{expected}> but got <{actual}>");
        }
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private static T AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex)
        {
            return ex;
        }
        throw new Exception($"Expected exception {typeof(T).Name} but none was thrown");
    }

    private static async Task<T> AssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T ex)
        {
            return ex;
        }
        throw new Exception($"Expected exception {typeof(T).Name} but none was thrown");
    }
}
