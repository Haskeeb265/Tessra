using Serilog;
using Tessra.Platform.Api.Middleware;
using Tessra.Platform.Observability.Middleware;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure Serilog as the logging provider for the entire application
    builder.Host.UseSerilog();

    builder.Services.AddOpenApi();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();

    // Global exception handling must be registered before other middleware
    // so it can catch exceptions from everything downstream.
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseMiddleware<RequestLoggingMiddleware>();

    app.MapGet("/health", () =>
    {
        return Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    })
    .WithName("HealthCheck");

    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
