using System.Security.Claims;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Api.Services;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/auth");

        // ============================================================
        // Registration
        // ============================================================

        group.MapPost(
            "/register",
            async (
                RegisterRequest request,
                AuthService authService) =>
            {
                var result = await authService.RegisterAsync(
                    request.Email,
                    request.Password,
                    request.InviteToken);

                if (!result.IsSuccess)
                {
                    return Results.BadRequest(
                        new { error = result.ErrorMessage });
                }

                if (result.AccessToken is not null)
                {
                    return Results.Created(
                        "/auth/login",
                        new TokenResponse(
                            result.AccessToken,
                            result.RefreshToken!));
                }

                return Results.Created(
                    "/auth/login",
                    new { message = result.Message });
            })
            .WithName("Register")
            .RequireRateLimiting("auth");

        // ============================================================
        // Login
        // ============================================================

        group.MapPost(
            "/login",
            async (
                LoginRequest request,
                AuthService authService) =>
            {
                var result = await authService.LoginAsync(
                    request.Email,
                    request.Password);

                if (!result.IsSuccess)
                {
                    return Results.Unauthorized();
                }

                if (result.MfaRequired)
                {
                    return Results.Ok(
                        new
                        {
                            mfaRequired = true,
                            mfaToken = result.MfaToken
                        });
                }

                return Results.Ok(
                    new TokenResponse(
                        result.AccessToken!,
                        result.RefreshToken!));
            })
            .WithName("Login")
            .RequireRateLimiting("auth");

        // ============================================================
        // MFA (second factor)
        // ============================================================

        group.MapPost(
            "/mfa",
            async (
                MfaLoginRequest request,
                AuthService authService) =>
            {
                var result = await authService.CompleteMfaLoginAsync(
                    request.MfaToken,
                    request.Code);

                return result.IsSuccess
                    ? Results.Ok(
                        new TokenResponse(
                            result.AccessToken!,
                            result.RefreshToken!))
                    : Results.BadRequest(
                        new { error = result.ErrorMessage });
            })
            .WithName("CompleteMfaLogin")
            .RequireRateLimiting("auth");

        // ============================================================
        // Refresh
        // ============================================================

        group.MapPost(
            "/refresh",
            async (
                RefreshRequest request,
                AuthService authService) =>
            {
                var result = await authService.RefreshAsync(
                    request.RefreshToken);

                return result.IsSuccess
                    ? Results.Ok(
                        new TokenResponse(
                            result.AccessToken!,
                            result.RefreshToken!))
                    : Results.BadRequest(
                        new { error = result.ErrorMessage });
            })
            .WithName("Refresh")
            .RequireRateLimiting("auth");

        // ============================================================
        // Logout (server-side revoke)
        // ============================================================

        group.MapPost(
            "/logout",
            async (
                LogoutRequest request,
                AuthService authService) =>
            {
                await authService.LogoutAsync(request.RefreshToken);
                return Results.NoContent();
            })
            .WithName("Logout")
            .RequireRateLimiting("auth");

        // ============================================================
        // Password management
        // ============================================================

        group.MapPost(
            "/change-password",
            async (
                ChangePasswordRequest request,
                AuthService authService,
                ClaimsPrincipal user) =>
            {
                var userId = AuthHelpers.GetUserId(user);

                if (userId is null)
                {
                    return Results.Unauthorized();
                }

                var result = await authService.ChangePasswordAsync(
                    userId.Value,
                    request.CurrentPassword,
                    request.NewPassword);

                return result.IsSuccess
                    ? Results.Ok(new { message = result.Message })
                    : Results.BadRequest(new { error = result.ErrorMessage });
            })
            .WithName("ChangePassword")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        group.MapPost(
            "/forgot-password",
            async (
                ForgotPasswordRequest request,
                AuthService authService) =>
            {
                await authService.ForgotPasswordAsync(request.Email);

                return Results.Ok(
                    new
                    {
                        message =
                            "If this email is registered, a reset link " +
                            "has been sent."
                    });
            })
            .WithName("ForgotPassword")
            .RequireRateLimiting("auth");

        group.MapPost(
            "/reset-password",
            async (
                ResetPasswordRequest request,
                AuthService authService) =>
            {
                var result = await authService.ResetPasswordAsync(
                    request.Token,
                    request.NewPassword);

                return result.IsSuccess
                    ? Results.Ok(new { message = result.Message })
                    : Results.BadRequest(new { error = result.ErrorMessage });
            })
            .WithName("ResetPassword")
            .RequireRateLimiting("auth");

        // ============================================================
        // Email verification
        // ============================================================

        group.MapPost(
            "/verify-email",
            async (
                VerifyEmailRequest request,
                AuthService authService) =>
            {
                var result = await authService.VerifyEmailAsync(request.Token);

                return result.IsSuccess
                    ? Results.Ok(new { message = result.Message })
                    : Results.BadRequest(new { error = result.ErrorMessage });
            })
            .WithName("VerifyEmail")
            .RequireRateLimiting("auth");

        // ============================================================
        // MFA enrollment (authenticated)
        // ============================================================

        group.MapPost(
            "/mfa/enroll",
            async (
                AuthService authService,
                ClaimsPrincipal user) =>
            {
                var userId = AuthHelpers.GetUserId(user);

                if (userId is null)
                {
                    return Results.Unauthorized();
                }

                var result = await authService.EnrollMfaAsync(userId.Value);

                if (!result.IsSuccess)
                {
                    return Results.BadRequest(
                        new { error = result.ErrorMessage });
                }

                // Message carries "secret:<secret>|uri:<otpauth-uri>".
                var parts = result.Message!.Split("|uri:");

                return Results.Ok(
                    new
                    {
                        secret = parts[0]["secret:".Length..],
                        otpauthUri = parts.Length > 1 ? parts[1] : null
                    });
            })
            .WithName("EnrollMfa")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        group.MapPost(
            "/mfa/verify",
            async (
                MfaCodeRequest request,
                AuthService authService,
                ClaimsPrincipal user) =>
            {
                var userId = AuthHelpers.GetUserId(user);

                if (userId is null)
                {
                    return Results.Unauthorized();
                }

                var result = await authService.ConfirmMfaAsync(
                    userId.Value,
                    request.Code);

                return result.IsSuccess
                    ? Results.Ok(new { message = result.Message })
                    : Results.BadRequest(new { error = result.ErrorMessage });
            })
            .WithName("ConfirmMfa")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        group.MapPost(
            "/mfa/disable",
            async (
                MfaCodeRequest request,
                AuthService authService,
                ClaimsPrincipal user) =>
            {
                var userId = AuthHelpers.GetUserId(user);

                if (userId is null)
                {
                    return Results.Unauthorized();
                }

                var result = await authService.DisableMfaAsync(
                    userId.Value,
                    request.Code);

                return result.IsSuccess
                    ? Results.Ok(new { message = result.Message })
                    : Results.BadRequest(new { error = result.ErrorMessage });
            })
            .WithName("DisableMfa")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        // ============================================================
        // Role Promotion (legacy; kept for compatibility)
        // ============================================================

        group.MapPost(
            "/promote",
            async (
                PromoteRequest request,
                AuthService authService,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db,
                    http,
                    user,
                    ActionCatalog.ManageUsers);

                if (denied is not null)
                {
                    return denied;
                }

                var result = await authService.PromoteToAdminAsync(
                    request.Email);

                return result.IsSuccess
                    ? Results.Ok(new { message = result.Message })
                    : Results.BadRequest(new { error = result.ErrorMessage });
            })
            .WithName("PromoteToAdmin")
            .RequireAuthorization()
            .RequireRateLimiting("auth");
    }
}

// ================================================================
// Request / Response DTOs
// ================================================================

public record RegisterRequest(
    string Email,
    string Password,
    string? InviteToken = null);

public record LoginRequest(string Email, string Password);

public record MfaLoginRequest(string MfaToken, string Code);

public record RefreshRequest(string RefreshToken);

public record LogoutRequest(string RefreshToken);

public record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);

public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(string Token, string NewPassword);

public record VerifyEmailRequest(string Token);

public record MfaCodeRequest(string Code);

public record PromoteRequest(string Email);

public record TokenResponse(string AccessToken, string RefreshToken);
