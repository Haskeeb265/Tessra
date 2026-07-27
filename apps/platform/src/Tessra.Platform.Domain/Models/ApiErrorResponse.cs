using System.Text.Json.Serialization;

namespace Tessra.Platform.Domain.Models;

/// <summary>
/// Standardised JSON error response returned by the platform API
/// when an error occurs during request processing.
/// </summary>
public class ApiErrorResponse
{
    /// <summary>
    /// HTTP status code (e.g., 400, 404, 500).
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Additional details (e.g., stack trace in development).
    /// Only included when non-null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; init; }

    /// <summary>
    /// Correlation ID for linking errors to log entries.
    /// </summary>
    public string TraceId { get; init; } = string.Empty;

    /// <summary>
    /// UTC timestamp of when the error occurred.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
