namespace IpScope.Sdk;

/// <summary>
/// Base exception for all errors raised by the IPScope .NET SDK.
/// </summary>
public class IpScopeException : Exception
{
    public string Code { get; }

    public IpScopeException(string message, string code = "IPSCOPE_ERROR", Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }
}

/// <summary>
/// Thrown when client-side validation fails before making an HTTP request.
/// </summary>
public sealed class IpScopeValidationException : IpScopeException
{
    public string? Field { get; }

    public IpScopeValidationException(string message, string? field = null)
        : base(message, "IPSCOPE_VALIDATION_ERROR")
    {
        Field = field;
    }
}

/// <summary>
/// Thrown when an HTTP request exceeds the configured timeout.
/// </summary>
public sealed class IpScopeTimeoutException : IpScopeException
{
    public string Path { get; }
    public TimeSpan Timeout { get; }

    public IpScopeTimeoutException(string path, TimeSpan timeout, Exception? innerException = null)
        : base($"IPScope request to \"{path}\" timed out after {timeout.TotalMilliseconds:F0}ms", "IPSCOPE_TIMEOUT_ERROR", innerException)
    {
        Path = path;
        Timeout = timeout;
    }
}

/// <summary>
/// Thrown when the IPScope API returns a non-2xx HTTP status code or transport error.
/// </summary>
public class IpScopeApiException : IpScopeException
{
    public int Status { get; }
    public string Path { get; }
    public RateLimitMetadata? RateLimit { get; }

    public IpScopeApiException(
        string message,
        int status,
        string path,
        string? code = null,
        RateLimitMetadata? rateLimit = null,
        Exception? innerException = null
    ) : base(message, code ?? $"IPSCOPE_HTTP_{status}", innerException)
    {
        Status = status;
        Path = path;
        RateLimit = rateLimit;
    }
}

/// <summary>
/// Thrown on HTTP 401 Unauthorized or 403 Forbidden.
/// </summary>
public sealed class IpScopeAuthenticationException : IpScopeApiException
{
    public IpScopeAuthenticationException(
        string message,
        int status,
        string path,
        RateLimitMetadata? rateLimit = null
    ) : base(
        message,
        status,
        path,
        status == 401 ? "IPSCOPE_UNAUTHORIZED" : "IPSCOPE_FORBIDDEN_SCOPE",
        rateLimit
    )
    {
    }
}

/// <summary>
/// Thrown on HTTP 429 Too Many Requests when retries are exhausted or disabled.
/// </summary>
public sealed class IpScopeRateLimitException : IpScopeApiException
{
    public TimeSpan? RetryAfter { get; }

    public IpScopeRateLimitException(
        string message,
        string path,
        TimeSpan? retryAfter = null,
        RateLimitMetadata? rateLimit = null
    ) : base(message, 429, path, "IPSCOPE_RATE_LIMITED", rateLimit)
    {
        RetryAfter = retryAfter;
    }
}
