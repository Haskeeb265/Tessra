using System.Diagnostics;

using Serilog;

using Microsoft.AspNetCore.Http;

namespace Tessera.Platform.Observability.Middleware;

/// <summary>
/// Middleware that logs every HTTP request with its method, path,
/// response status code, and processing duration.
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;

    public RequestLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        await _next(context);

        stopwatch.Stop();

        Log.Information(
            "HTTP {Method} {Path} responded {StatusCode} in {Duration:F1}ms",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            stopwatch.Elapsed.TotalMilliseconds
        );
    }
}
