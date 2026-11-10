using System.Text.Json;
using System.Text.Json.Serialization;

namespace IpScope.Sdk;

/// <summary>
/// Reads <see cref="CanonicalNetworkPrefixRecord"/> from the API's camelCase snapshot record
/// (<c>countryCode</c>, <c>isVpn</c>, ...) and also accepts snake_case keys. Writes camelCase.
/// </summary>
internal sealed class CanonicalNetworkPrefixRecordConverter : JsonConverter<CanonicalNetworkPrefixRecord>
{
    public override CanonicalNetworkPrefixRecord Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        JsonElement e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected a JSON object for the network prefix record.");
        }

        return new CanonicalNetworkPrefixRecord(
            Prefix: Str(e, "prefix", "prefix") ?? string.Empty,
            Family: Int(e, "family", "family") == 6 ? 6 : 4,
            Asn: Int(e, "asn", "asn"),
            Organization: Str(e, "organization", "organization"),
            CountryCode: Str(e, "countryCode", "country_code"),
            City: Str(e, "city", "city"),
            LocationType: Str(e, "locationType", "location_type"),
            IsVpn: Bool(e, "isVpn", "is_vpn"),
            IsHosting: Bool(e, "isHosting", "is_hosting"),
            IsTorExit: Bool(e, "isTorExit", "is_tor_exit"),
            IsProxy: Bool(e, "isProxy", "is_proxy"),
            AnonymizerType: Str(e, "anonymizerType", "anonymizer_type"),
            Subdivision: Str(e, "subdivision", "subdivision"),
            PostalCode: Str(e, "postalCode", "postal_code"),
            Latitude: Dbl(e, "latitude", "latitude"),
            Longitude: Dbl(e, "longitude", "longitude"),
            Timezone: Str(e, "timezone", "timezone"),
            GeoConfidence: Dbl(e, "geoConfidence", "geo_confidence"),
            AccuracyRadiusKm: Dbl(e, "accuracyRadiusKm", "accuracy_radius_km"),
            IsVirtualLocation: Bool(e, "isVirtualLocation", "is_virtual_location"),
            ConnectionType: Str(e, "connectionType", "connection_type"),
            IsRelay: Bool(e, "isRelay", "is_relay"),
            SecurityConfidence: Dbl(e, "securityConfidence", "security_confidence"),
            AbuseVelocityScore: Dbl(e, "abuseVelocityScore", "abuse_velocity_score"),
            IsKnownScanner: Bool(e, "isKnownScanner", "is_known_scanner"),
            ReasonCodes: StrList(e, "reasonCodes", "reason_codes"),
            UpdatedAt: Str(e, "updatedAt", "updated_at")
        );
    }

    public override void Write(Utf8JsonWriter writer, CanonicalNetworkPrefixRecord value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("prefix", value.Prefix);
        writer.WriteNumber("family", value.Family);
        WriteNumber(writer, "asn", value.Asn);
        writer.WriteString("organization", value.Organization);
        writer.WriteString("countryCode", value.CountryCode);
        writer.WriteString("subdivision", value.Subdivision);
        writer.WriteString("city", value.City);
        writer.WriteString("postalCode", value.PostalCode);
        WriteNumber(writer, "latitude", value.Latitude);
        WriteNumber(writer, "longitude", value.Longitude);
        writer.WriteString("timezone", value.Timezone);
        writer.WriteString("locationType", value.LocationType);
        WriteNumber(writer, "geoConfidence", value.GeoConfidence);
        WriteNumber(writer, "accuracyRadiusKm", value.AccuracyRadiusKm);
        WriteBool(writer, "isVirtualLocation", value.IsVirtualLocation);
        writer.WriteString("connectionType", value.ConnectionType);
        WriteBool(writer, "isHosting", value.IsHosting);
        WriteBool(writer, "isVpn", value.IsVpn);
        WriteBool(writer, "isTorExit", value.IsTorExit);
        WriteBool(writer, "isProxy", value.IsProxy);
        WriteBool(writer, "isRelay", value.IsRelay);
        writer.WriteString("anonymizerType", value.AnonymizerType);
        WriteNumber(writer, "securityConfidence", value.SecurityConfidence);
        WriteNumber(writer, "abuseVelocityScore", value.AbuseVelocityScore);
        WriteBool(writer, "isKnownScanner", value.IsKnownScanner);
        writer.WriteStartArray("reasonCodes");
        foreach (string code in value.ReasonCodes ?? [])
        {
            writer.WriteStringValue(code);
        }
        writer.WriteEndArray();
        writer.WriteString("updatedAt", value.UpdatedAt);
        writer.WriteEndObject();
    }

    private static bool TryGet(JsonElement e, string camel, string snake, out JsonElement value)
    {
        if (e.TryGetProperty(camel, out value) && value.ValueKind != JsonValueKind.Null)
        {
            return true;
        }
        return e.TryGetProperty(snake, out value) && value.ValueKind != JsonValueKind.Null;
    }

    private static string? Str(JsonElement e, string camel, string snake) =>
        TryGet(e, camel, snake, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string camel, string snake) =>
        TryGet(e, camel, snake, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i
            : null;

    private static double? Dbl(JsonElement e, string camel, string snake) =>
        TryGet(e, camel, snake, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool? Bool(JsonElement e, string camel, string snake) =>
        TryGet(e, camel, snake, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static IReadOnlyList<string> StrList(JsonElement e, string camel, string snake)
    {
        if (!TryGet(e, camel, snake, out JsonElement v) || v.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var list = new List<string>(v.GetArrayLength());
        foreach (JsonElement item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                list.Add(s);
            }
        }
        return list;
    }

    private static void WriteNumber(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } d)
        {
            w.WriteNumber(name, d);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteNumber(Utf8JsonWriter w, string name, int? value)
    {
        if (value is { } i)
        {
            w.WriteNumber(name, i);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteBool(Utf8JsonWriter w, string name, bool? value)
    {
        if (value is { } b)
        {
            w.WriteBoolean(name, b);
        }
        else
        {
            w.WriteNull(name);
        }
    }
}
