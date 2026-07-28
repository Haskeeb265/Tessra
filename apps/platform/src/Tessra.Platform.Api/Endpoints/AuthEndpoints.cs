using Tessra.Platform.Api.Services;

namespace Tessra.Platform.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/auth");

        group.MapPost("/register", async (
            RegisterRequest request,
            AuthService authService) =>
        {
            var result = await authService.RegisterAsync(request.Email, request.Password);

            return result.IsSuccess
                ? Results.Created("/auth/login", new TokenResponse(
                    result.AccessToken!, result.RefreshToken!))
                : Results.BadRequest(new { error = result.ErrorMessage });
        })
        .WithName("Register");

        group.MapPost("/login", async (
            LoginRequest request,
            AuthService authService) =>
        {
            var result = await authService.LoginAsync(request.Email, request.Password);

            return result.IsSuccess
                ? Results.Ok(new TokenResponse(
                    result.AccessToken!, result.RefreshToken!))
                : Results.Unauthorized();
        })
        .WithName("Login");

        group.MapPost("/refresh", async (
            RefreshRequest request,
            AuthService authService) =>
        {
            var result = await authService.RefreshAsync(request.RefreshToken);

            return result.IsSuccess
                ? Results.Ok(new TokenResponse(
                    result.AccessToken!, result.RefreshToken!))
                : Results.BadRequest(new { error = result.ErrorMessage });
        })
        .WithName("Refresh");

        group.MapPost("/promote", async (
            PromoteRequest request,
            AuthService authService) =>
        {
            var result = await authService.PromoteToAdminAsync(request.Email);

            return result.IsSuccess
                ? Results.Ok(new { message = result.Message })
                : Results.BadRequest(new { error = result.ErrorMessage });
        })
        .WithName("PromoteToAdmin")
        .RequireAuthorization("AdminOnly");
    }
}

// ─── Request / Response DTOs ───────────────────────────────────

public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record PromoteRequest(string Email);
public record TokenResponse(string AccessToken, string RefreshToken);
