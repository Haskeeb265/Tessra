using System.Diagnostics;
using System.Net;
using Serilog;
using Tessra.Platform.Models;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(RequestDelegate next, IHostEnvironment environment)
    {
        _next = next;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        var (statusCode, message, logLevel) = MapException(exception);

        Log.Write(
            logLevel,
            exception,
            "HTTP {Method} {Path} failed with {StatusCode}: {Message} [TraceId: {TraceId}]",
            context.Request.Method,
            context.Request.Path,
            statusCode,
            message,
            traceId
        );

        var response = new ApiErrorResponse
        {
            StatusCode = statusCode,
            Message = message,
            Details = _environment.IsDevelopment() ? exception.StackTrace : null,
            TraceId = traceId,
            Timestamp = DateTime.UtcNow
        };

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = statusCode;

        await context.Response.WriteAsJsonAsync(response);
    }

    private static (int StatusCode, string Message, Serilog.Events.LogEventLevel LogLevel) MapException(Exception exception)
    {
        return exception switch
        {
            // Client disconnected — don't treat as a server error
            OperationCanceledException or TaskCanceledException =>
                (499, "The request was cancelled.", Serilog.Events.LogEventLevel.Information),

            KeyNotFoundException or FileNotFoundException =>
                ((int)HttpStatusCode.NotFound, "The requested resource was not found.", Serilog.Events.LogEventLevel.Warning),

            ArgumentException or InvalidOperationException =>
                ((int)HttpStatusCode.BadRequest, exception.Message, Serilog.Events.LogEventLevel.Warning),

            UnauthorizedAccessException =>
                ((int)HttpStatusCode.Forbidden, "You are not authorized to perform this action.", Serilog.Events.LogEventLevel.Warning),

            NotImplementedException =>
                ((int)HttpStatusCode.NotImplemented, "This feature has not been implemented yet.", Serilog.Events.LogEventLevel.Warning),

            HttpRequestException =>
                ((int)HttpStatusCode.BadGateway, "An upstream service returned an error.", Serilog.Events.LogEventLevel.Error),

            _ =>
                ((int)HttpStatusCode.InternalServerError, "An unexpected error occurred. Please try again later.", Serilog.Events.LogEventLevel.Error)
        };
    }
}
