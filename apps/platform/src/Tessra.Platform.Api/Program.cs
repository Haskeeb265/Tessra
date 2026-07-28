using System.Text;
using Serilog;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Finbuckle.MultiTenant.Extensions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Tessra.Platform.Api.Data;
using Tessra.Platform.Api.Endpoints;
using Tessra.Platform.Api.Middleware;
using Tessra.Platform.Api.Services;
using Tessra.Platform.Domain.Models;
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
    
    builder.Services.AddMultiTenant<Tenant>().WithHeaderStrategy("X-Tenant-Id").WithInMemoryStore(options =>
    {
        options.Tenants.Add(new Tenant {Id = "alpha", Identifier = "alpha-corp", Name = "Alpha Corp"});
        options.Tenants.Add(new Tenant {Id = "beta", Identifier = "beta-industries", Name = "Beta Industries"});
    });

    // Use PostgreSQL if a connection string is configured, otherwise fall back to InMemory
    // for local development without Docker.
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (!string.IsNullOrEmpty(connectionString))
    {
        builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
    }
    else
    {
        builder.Services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase("TessraPlatformDb"));
    }

    // Configure JWT authentication
    var jwtSettings = builder.Configuration.GetSection("Jwt");
    var secretKey = jwtSettings["SecretKey"]!;
    var issuer = jwtSettings["Issuer"]!;
    var audience = jwtSettings["Audience"]!;

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
            ClockSkew = TimeSpan.Zero
        };
    });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("AdminOnly", policy =>
            policy.RequireRole(Roles.Admin));
    });

    // Register application services
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<AuthService>();

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

    // Validate X-Tenant-Id header before tenant resolution
    app.UseMiddleware<TenantValidationMiddleware>();

    app.UseMultiTenant();

    app.UseAuthentication();

    // Validate that the JWT's tenant_id claim matches the X-Tenant-Id header.
    // This must run AFTER UseAuthentication() (so the JWT is decoded) and
    // BEFORE UseAuthorization() (so unauthorized + tenant-mismatch have
    // distinct error codes: 401 vs 403).
    app.UseMiddleware<TenantClaimValidationMiddleware>();

    app.UseAuthorization();

    app.MapGet("/health", () =>
    {
        return Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    })
    .WithName("HealthCheck");

    app.MapWidgetEndpoints();
    app.MapAuthEndpoints();        // Auto-apply pending migrations on startup for relational databases
    // (PostgreSQL). InMemory doesn't support migrations, so we skip it.
    //
    // For PostgreSQL, we also seed an admin user for every configured tenant
    // using raw SQL. We use SQL instead of EF Core because Finbuckle's
    // EnforceMultiTenant requires a TenantInfo context that doesn't exist
    // during startup (no HTTP request). Raw SQL bypasses the EF Core
    // change tracker and EnforceMultiTenant entirely.
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsRelational())
        {
            db.Database.Migrate();

            // Check across all tenants (bypassing the Finbuckle filter which
            // requires a tenant context that doesn't exist during startup).
            if (!await db.Users.IgnoreQueryFilters().AnyAsync())
            {
                var seedSection = app.Configuration.GetSection("SeedAdmin");
                var seedEmail = seedSection["Email"]!;
                var seedPassword = seedSection["Password"]!;

                var passwordHash = BCrypt.Net.BCrypt.HashPassword(seedPassword);
                var tenantIds = new[] { "alpha", "beta" };

                foreach (var tenantId in tenantIds)
                {
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        INSERT INTO "Users" ("Id", "Email", "PasswordHash", "Role", "TenantId", "CreatedAt")
                        VALUES ({0}, {1}, {2}, {3}, {4}, {5})
                        """,
                        Guid.NewGuid(),
                        seedEmail,
                        passwordHash,
                        Roles.Admin,
                        tenantId,
                        DateTime.UtcNow);
                }

                Log.Information(
                    "Seeded admin user ({Email}) for {TenantCount} tenant(s) via raw SQL",
                    seedEmail, tenantIds.Length);
            }
        }
    }

    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
