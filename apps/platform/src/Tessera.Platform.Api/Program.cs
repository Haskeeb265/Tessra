using System.Text;

using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Finbuckle.MultiTenant.Extensions;
using Serilog;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Api.Endpoints;
using Tessera.Platform.Api.Middleware;
using Tessera.Platform.Api.Services;
using Tessera.Platform.Domain.Models;
using Tessera.Platform.Observability.Middleware;

// ============================================================
// Logging Bootstrap
// ============================================================

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure Serilog as the logging provider for the entire application.
    builder.Host.UseSerilog();

    // ============================================================
    // Multi-Tenancy
    // ============================================================

    // Tenants live in the database (Tenants table) so superadmins can create
    // them at runtime. The default alpha/beta tenants are seeded on startup.
    builder.Services.AddMultiTenant<Tenant>()
        .WithHeaderStrategy("X-Tenant-Id")
        .WithStore<DbTenantStore>(ServiceLifetime.Singleton);

    // ============================================================
    // Database
    // ============================================================

    // Use PostgreSQL if a connection string is configured, otherwise fall
    // back to InMemory for local development without Docker.
    var connectionString = builder.Configuration
        .GetConnectionString("DefaultConnection");

    if (!string.IsNullOrEmpty(connectionString))
    {
        builder.Services.AddDbContext<AppDbContext>(
            options => options.UseNpgsql(connectionString));
    }
    else
    {
        builder.Services.AddDbContext<AppDbContext>(
            options => options.UseInMemoryDatabase("TesseraPlatformDb"));
    }

    // ============================================================
    // Authentication & Authorization
    // ============================================================

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
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(secretKey)),
            ClockSkew = TimeSpan.Zero
        };
    });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("AdminOnly", policy =>
            policy.RequireRole(Roles.Admin));
        options.AddPolicy("SuperAdminOnly", policy =>
            policy.RequireRole(Roles.SuperAdmin));
    });

    // ============================================================
    // Application Services
    // ============================================================

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<AuthService>();
    builder.Services.AddScoped<AdminAuthService>();

    // ============================================================
    // CORS
    // ============================================================

    // Allow both frontends (apps/web tenant portal on :3000 and the
    // apps/platform-portal superadmin portal on :3001) to call this API.
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("WebApp", policy =>
            policy.WithOrigins(
                    "http://localhost:3000", "https://localhost:3000",
                    "http://localhost:3001", "https://localhost:3001")
                .AllowAnyHeader()
                .AllowAnyMethod());
    });

    builder.Services.AddOpenApi();

    // ============================================================
    // Request Pipeline
    // ============================================================

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

    // CORS must run before tenant validation so preflight (OPTIONS) requests
    // — which browsers send without custom headers — are answered by the
    // CORS middleware instead of being rejected for a missing X-Tenant-Id.
    app.UseCors("WebApp");

    // Validate X-Tenant-Id header before tenant resolution.
    app.UseMiddleware<TenantValidationMiddleware>();

    app.UseMultiTenant();

    app.UseAuthentication();

    // Validate that the JWT's tenant_id claim matches the X-Tenant-Id header.
    // This must run AFTER UseAuthentication() (so the JWT is decoded) and
    // BEFORE UseAuthorization() (so unauthorized + tenant-mismatch have
    // distinct error codes: 401 vs 403).
    app.UseMiddleware<TenantClaimValidationMiddleware>();

    app.UseAuthorization();

    // ============================================================
    // Endpoints
    // ============================================================

    app.MapGet(
        "/health",
        () => Results.Ok(
            new { status = "healthy", timestamp = DateTime.UtcNow }))
        .WithName("HealthCheck");

    // Public endpoint the frontends use to populate the workspace picker.
    app.MapGet(
        "/tenants",
        async (AppDbContext db) =>
        {
            var result = await db.Tenants
                .AsNoTracking()
                .OrderBy(t => t.Name)
                .Select(t => new { id = t.Identifier, name = t.Name })
                .ToListAsync();

            return Results.Ok(result);
        })
        .WithName("PublicListTenants");

    app.MapWidgetEndpoints();
    app.MapAuthEndpoints();
    app.MapTenantEndpoints();
    app.MapAdminEndpoints();

    // ============================================================
    // Database Seeding
    // ============================================================

    // Seed platform-level data (tenants, envelopes, superadmin). These
    // entities are NOT multi-tenant, so EF Core can seed them on either
    // provider (InMemory or PostgreSQL).
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Auto-apply pending migrations on startup for relational databases.
        // InMemory doesn't support migrations, so we skip it.
        if (db.Database.IsRelational())
        {
            db.Database.Migrate();
        }

        await SeedPlatformDataAsync(db, app.Configuration);

        // Seed an admin user for every configured tenant using raw SQL on
        // PostgreSQL. We use SQL instead of EF Core because Finbuckle's
        // EnforceMultiTenant requires a TenantInfo context that doesn't exist
        // during startup (no HTTP request). Raw SQL bypasses the EF Core
        // change tracker and EnforceMultiTenant entirely.
        if (db.Database.IsRelational() &&
            !await db.Users.IgnoreQueryFilters().AnyAsync())
        {
            var seedSection = app.Configuration.GetSection("SeedAdmin");
            var seedEmail = seedSection["Email"]!;
            var seedPassword = seedSection["Password"]!;

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(seedPassword);
            var tenantIds = await db.Tenants
                .AsNoTracking()
                .Select(t => t.Id)
                .ToListAsync();

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
                seedEmail,
                tenantIds.Count);
        }
    }

    app.Run();

    // Seeds platform-level data (not tenant-scoped) so both the in-memory
    // and PostgreSQL providers start with working defaults.
    static async Task SeedPlatformDataAsync(
        AppDbContext db,
        IConfiguration configuration)
    {
        // Default envelope: roles + the actions each role is allowed.
        // Actions are informational for now — enforcement is on the backlog.
        if (!await db.Envelopes.AnyAsync())
        {
            db.Envelopes.Add(new Envelope
            {
                Name = "Standard",
                Description = "Default envelope for new workspaces.",
                Roles =
                [
                    new AppRole
                    {
                        Name = Roles.Admin,
                        Actions =
                        [
                            ActionCatalog.ManageUsers,
                            ActionCatalog.CreateWidget,
                            ActionCatalog.EditWidget,
                            ActionCatalog.DeleteWidget
                        ]
                    },
                    new AppRole
                    {
                        Name = "Manager",
                        Actions =
                        [
                            ActionCatalog.CreateWidget,
                            ActionCatalog.EditWidget
                        ]
                    },
                    new AppRole
                    {
                        Name = Roles.User,
                        Actions =
                        [
                            ActionCatalog.ViewWidgets
                        ]
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var standard = await db.Envelopes
            .FirstOrDefaultAsync(e => e.Name == "Standard");

        // Default tenants (the original hardcoded ones, now in the database).
        if (!await db.Tenants.AnyAsync())
        {
            db.Tenants.AddRange(
                new Tenant
                {
                    Id = "alpha",
                    Identifier = "alpha-corp",
                    Name = "Alpha Corp",
                    EnvelopeId = standard?.Id
                },
                new Tenant
                {
                    Id = "beta",
                    Identifier = "beta-industries",
                    Name = "Beta Industries",
                    EnvelopeId = standard?.Id
                });
            await db.SaveChangesAsync();
        }
        else
        {
            // Assign the standard envelope to any tenant that doesn't have one.
            var unassigned = await db.Tenants
                .Where(t => t.EnvelopeId == null)
                .ToListAsync();

            foreach (var tenant in unassigned)
            {
                tenant.EnvelopeId = standard?.Id;
            }

            if (unassigned.Count > 0)
            {
                await db.SaveChangesAsync();
            }
        }

        // Platform-level superadmin account (not bound to a tenant).
        if (!await db.AdminUsers.AnyAsync())
        {
            var section = configuration.GetSection("SeedSuperAdmin");
            var email = section["Email"] ?? "superadmin@tessera.com";
            var password = section["Password"] ?? "Admin123!";

            db.AdminUsers.Add(new AdminUser
            {
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
            });
            await db.SaveChangesAsync();
        }
    }
}
finally
{
    Log.CloseAndFlush();
}
