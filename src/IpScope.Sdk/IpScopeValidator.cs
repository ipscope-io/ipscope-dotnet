using System.Net;
using System.Net.Sockets;

namespace IpScope.Sdk;

/// <summary>
/// Zero-DNS client-side IPv4/IPv6, CIDR prefix, and API key validation utilities.
/// </summary>
public static class IpScopeValidator
{
    public const int MaxSingleBatchSize = 100;

    public readonly record struct ValidatedIp(string CanonicalIp, int Family);

    public readonly record struct ValidatedCidr(string Address, int PrefixLength, int Family, string CanonicalCidr);

    public readonly record struct ValidatedBatch(IReadOnlyList<string> OrderedIps, IReadOnlyList<string> UniqueIps);

    public static string ValidateApiKey(string? rawApiKey)
    {
        if (rawApiKey is null)
        {
            throw new IpScopeValidationException("API key must be a non-empty string", "apiKey");
        }
        string trimmed = rawApiKey.Trim();
        if (trimmed.Length == 0)
        {
            throw new IpScopeValidationException("API key cannot be empty", "apiKey");
        }
        if (trimmed.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new IpScopeValidationException("API key contains invalid control characters", "apiKey");
        }
        return trimmed;
    }

    public static ValidatedIp ValidateIpAddress(string? rawIp)
    {
        if (rawIp is null)
        {
            throw new IpScopeValidationException("IP address must be a string", "ip");
        }
        string trimmed = rawIp.Trim();
        if (trimmed.Length == 0 || trimmed.Contains('%') || trimmed.Contains('/'))
        {
            throw new IpScopeValidationException($"Invalid IPv4 or IPv6 address: \"{trimmed}\"", "ip");
        }

        // Prevent IPAddress.TryParse from accepting single integer / hex / octal shorthand as IPv4
        if (!trimmed.Contains(':') && !IsStrictIpv4DottedQuad(trimmed))
        {
            throw new IpScopeValidationException($"Invalid IPv4 or IPv6 address: \"{trimmed}\"", "ip");
        }

        if (!IPAddress.TryParse(trimmed, out IPAddress? parsed))
        {
            throw new IpScopeValidationException($"Invalid IPv4 or IPv6 address: \"{trimmed}\"", "ip");
        }

        int family = parsed.AddressFamily == AddressFamily.InterNetworkV6 ? 6 : 4;
        string canonical = parsed.ToString().ToLowerInvariant();
        return new ValidatedIp(canonical, family);
    }

    public static ValidatedCidr ValidateCidrPrefix(string? rawCidr)
    {
        if (rawCidr is null)
        {
            throw new IpScopeValidationException("CIDR prefix must be a string", "cidr");
        }
        string trimmed = rawCidr.Trim();
        int slashIdx = trimmed.IndexOf('/');
        if (slashIdx <= 0 || slashIdx != trimmed.LastIndexOf('/') || slashIdx == trimmed.Length - 1)
        {
            throw new IpScopeValidationException(
                $"Invalid CIDR notation \"{trimmed}\". Expected \"<ip>/<prefixLength>\" (e.g. \"1.1.1.0/24\")",
                "cidr"
            );
        }

        string addrPart = trimmed[..slashIdx];
        string lenPart = trimmed[(slashIdx + 1)..].Trim();
        ValidatedIp vIp = ValidateIpAddress(addrPart);

        if (!int.TryParse(lenPart, out int prefixLen))
        {
            throw new IpScopeValidationException(
                $"Invalid prefix length \"{lenPart}\" in CIDR \"{trimmed}\"",
                "cidr"
            );
        }

        int maxBits = vIp.Family == 4 ? 32 : 128;
        if (prefixLen < 0 || prefixLen > maxBits)
        {
            throw new IpScopeValidationException(
                $"Prefix length {prefixLen} out of range [0..{maxBits}] for IPv{vIp.Family} CIDR \"{trimmed}\"",
                "cidr"
            );
        }

        byte[] bytes = IPAddress.Parse(vIp.CanonicalIp).GetAddressBytes();
        MaskNetworkBytes(bytes, prefixLen);
        string maskedAddr = new IPAddress(bytes).ToString().ToLowerInvariant();
        return new ValidatedCidr(maskedAddr, prefixLen, vIp.Family, $"{maskedAddr}/{prefixLen}");
    }

    public static ValidatedBatch ValidateAndDeduplicateBatchIps(
        IReadOnlyList<string>? rawIps,
        int maxBatchSize = MaxSingleBatchSize
    )
    {
        if (rawIps is null || rawIps.Count == 0)
        {
            throw new IpScopeValidationException("Batch IPs list must contain at least 1 IP address", "ips");
        }
        if (maxBatchSize > 0 && rawIps.Count > maxBatchSize)
        {
            throw new IpScopeValidationException(
                $"Batch IPs length ({rawIps.Count}) exceeds maximum single-request batch size ({maxBatchSize}). Use LookupBatchChunkedAsync() or LookupBatchStreamAsync() for larger lists.",
                "ips"
            );
        }

        var ordered = new List<string>(rawIps.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>(rawIps.Count);

        foreach (string raw in rawIps)
        {
            ValidatedIp v = ValidateIpAddress(raw);
            ordered.Add(v.CanonicalIp);
            if (seen.Add(v.CanonicalIp))
            {
                unique.Add(v.CanonicalIp);
            }
        }

        return new ValidatedBatch(ordered, unique);
    }

    private static bool IsStrictIpv4DottedQuad(string s)
    {
        string[] parts = s.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }
        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 3)
            {
                return false;
            }
            if (part.Length > 1 && part[0] == '0')
            {
                return false;
            }
            for (int i = 0; i < part.Length; i++)
            {
                if (!char.IsAsciiDigit(part[i]))
                {
                    return false;
                }
            }
            if (!int.TryParse(part, out int val) || val < 0 || val > 255)
            {
                return false;
            }
        }
        return true;
    }

    private static void MaskNetworkBytes(byte[] bytes, int prefixLen)
    {
        int fullBytes = prefixLen / 8;
        int remBits = prefixLen % 8;
        if (fullBytes < bytes.Length)
        {
            if (remBits > 0)
            {
                byte mask = (byte)((0xFF << (8 - remBits)) & 0xFF);
                bytes[fullBytes] &= mask;
                fullBytes++;
            }
            for (int i = fullBytes; i < bytes.Length; i++)
            {
                bytes[i] = 0;
            }
        }
    }
}
