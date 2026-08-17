using System.Security.Claims;
using System.Text;

using Microsoft.Extensions.Options;

using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Finbuckle.MultiTenant.Extensions;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

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
    // back to InMemory for local development without Docker. The options
    // action runs lazily at first context resolution, so it reads the final
    // merged configuration (host config is merged only after Program's
    // top-level code runs — e.g. WebApplicationFactory test overrides).
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        var connectionString = builder.Configuration
            .GetConnectionString("DefaultConnection");

        if (!string.IsNullOrEmpty(connectionString))
        {
            options.UseNpgsql(connectionString);
        }
        else
        {
            // Configurable name so each test factory can use its own store
            // (the InMemory provider caches by name process-wide).
            options.UseInMemoryDatabase(
                builder.Configuration["Database:InMemoryName"]
                ?? "TesseraPlatformDb");
        }
    });

    // ============================================================
    // Authentication & Authorization
    // ============================================================

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer();

    // Configure the bearer validation parameters lazily from the final
    // configuration (host config is merged only after Program's top-level
    // code runs — e.g. WebApplicationFactory test overrides), rather than
    // capturing them at startup.
    builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>>(
        serviceProvider => new ConfigureNamedOptions<JwtBearerOptions>(
            JwtBearerDefaults.AuthenticationScheme,
            options =>
            {
                var jwt = serviceProvider
                    .GetRequiredService<IConfiguration>()
                    .GetSection("Jwt");

                var key = jwt["SecretKey"];

                if (string.IsNullOrEmpty(key))
                {
                    throw new InvalidOperationException(
                        "Jwt:SecretKey is not configured. Set the " +
                        "Jwt__SecretKey environment variable (or appsettings) " +
                        "before starting the API.");
                }

                options.TokenValidationParameters =
                    new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = jwt["Issuer"],
                        ValidAudience = jwt["Audience"],
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(key)),
                        ClockSkew = TimeSpan.Zero
                    };
            }));

    builder.Services.AddAuthorization(options =>
    {
        // Tenant-level authorization is action-based (see ActionChecks);
        // only the platform-level superadmin role stays claim-based. The
        // assertion also requires the absence of tenant claims so a tenant
        // JWT can never satisfy this policy — even if a tenant role happens
        // to be named "SuperAdmin" (role-claim comparison is
        // case-insensitive).
        options.AddPolicy("SuperAdminOnly", policy =>
            policy.RequireAssertion(context =>
                context.User.IsInRole(Roles.SuperAdmin) &&
                string.IsNullOrEmpty(
                    context.User.FindFirstValue("tenant_identifier"))));
    });

    // ============================================================
    // Application Services
    // ============================================================

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<AuthService>();
    builder.Services.AddScoped<AdminAuthService>();

    // Pluggable email: swap ConsoleEmailSender for a real provider
    // (Resend, Postmark, SES…) by registering a different IEmailSender.
    builder.Services.AddScoped<IEmailSender, ConsoleEmailSender>();

    // ============================================================
    // CORS (origins from configuration — E6)
    // ============================================================

    var corsOrigins = builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>()
        ?? [];

    if (corsOrigins.Length == 0)
    {
        // Defaults for local dev (both portals). Production overrides via
        // Cors__AllowedOrigins__0 etc. or appsettings.<Env>.json.
        corsOrigins =
        [
            "http://localhost:3000", "https://localhost:3000",
            "http://localhost:3001", "https://localhost:3001"
        ];
    }

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("WebApp", policy =>
            policy.WithOrigins(corsOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod());
    });

    // ============================================================
    // Rate Limiting (C4). Registered unconditionally; the enabled
    // flag and limits are read per-request so configuration merged
    // after startup (e.g. WebApplicationFactory test overrides)
    // takes effect. Tests disable it via RateLimiting:Enabled=false.
    // ============================================================

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode =
            StatusCodes.Status429TooManyRequests;

        options.AddPolicy("auth", context =>
        {
            // Resolve configuration from the request services, which
            // reflect the final merged configuration.
            var config = context.RequestServices
                .GetRequiredService<IConfiguration>();

            if (!config.GetValue<bool>("RateLimiting:Enabled", true))
            {
                return RateLimitPartition.GetNoLimiter("disabled");
            }

            var authPermitLimit = config.GetValue<int>(
                "RateLimiting:AuthPermitLimit", 20);
            var authWindowSeconds = config.GetValue<int>(
                "RateLimiting:AuthWindowSeconds", 60);

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey:
                    context.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authPermitLimit,
                    Window = TimeSpan.FromSeconds(authWindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
        });
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

    app.UseRateLimiter();

    // Validate X-Tenant-Id header before tenant resolution.
    app.UseMiddleware<TenantValidationMiddleware>();

    app.UseMultiTenant();

    // Block requests to suspended tenants (B3) — includes /auth endpoints.
    app.UseMiddleware<TenantSuspensionMiddleware>();

    app.UseAuthentication();

    // Validate that the JWT's tenant_id claim matches the X-Tenant-Id header.
    // This must run AFTER UseAuthentication() (so the JWT is decoded) and
    // BEFORE UseAuthorization() (so unauthorized + tenant-mismatch have
    // distinct error codes: 401 vs 403).
    app.UseMiddleware<TenantClaimValidationMiddleware>();

    // Reject stale/deleted sessions immediately (A5). Runs after the tenant
    // claim check so cross-tenant reuse still gets its distinct 403.
    app.UseMiddleware<TokenVersionValidationMiddleware>();

    app.UseAuthorization();

    // ============================================================
    // Endpoints
    // ============================================================

    // Liveness: the process is up. (Readiness: /ready below.)
    app.MapGet(
        "/health",
        () => Results.Ok(
            new { status = "healthy", timestamp = DateTime.UtcNow }))
        .WithName("HealthCheck");

    // Readiness: the API can reach its database (E4).
    app.MapGet(
        "/ready",
        async (AppDbContext db) =>
        {
            var reachable = await db.Database.CanConnectAsync();

            return reachable
                ? Results.Ok(
                    new { status = "ready", timestamp = DateTime.UtcNow })
                : Results.Json(
                    new { status = "unavailable", timestamp = DateTime.UtcNow },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithName("Readiness");

    // Public endpoint the frontends use to populate the workspace picker.
    app.MapGet(
        "/tenants",
        async (AppDbContext db) =>
        {
            var result = await db.Tenants
                .AsNoTracking()
                .Where(t => !t.IsDeleted)
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

        // E3: migrations auto-apply on startup unless Database:AutoMigrate
        // is false — production should run `dotnet ef database update` as
        // an explicit deploy step instead.
        if (db.Database.IsRelational() &&
            app.Configuration.GetValue<bool>("Database:AutoMigrate"))
        {
            db.Database.Migrate();
        }

        await SeedPlatformDataAsync(db, app.Configuration);

        if (db.Database.IsRelational())
        {
            await SeedTenantDataAsync(db, app.Configuration);
        }
    }

    app.Run();

    // ================================================================
    // Seeds platform-level data (not tenant-scoped) so both the in-memory
    // and PostgreSQL providers start with working defaults.
    // ================================================================
    static async Task SeedPlatformDataAsync(
        AppDbContext db,
        IConfiguration configuration)
    {
        // Default envelope: roles + the actions each role is allowed.
        // This is now a TEMPLATE — tenants copy these roles into their own
        // role set when the envelope is assigned.
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
                            ActionCatalog.ViewWidgets,
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
            // Assign the standard envelope to any active tenant that
            // doesn't have one.
            var unassigned = await db.Tenants
                .Where(t => t.EnvelopeId == null && !t.IsDeleted)
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

    // ================================================================
    // Seeds tenant-scoped data (roles + an admin user per tenant) on
    // PostgreSQL using raw SQL — EF Core cannot save multi-tenant entities
    // at startup because Finbuckle's EnforceMultiTenant requires a tenant
    // context that doesn't exist outside an HTTP request.
    // ================================================================
    static async Task SeedTenantDataAsync(
        AppDbContext db,
        IConfiguration configuration)
    {
        var tenantIds = await db.Tenants
            .AsNoTracking()
            .Where(t => !t.IsDeleted)
            .Select(t => t.Id)
            .ToListAsync();

        foreach (var tenantId in tenantIds)
        {
            // Tenant roles: copy from the assigned envelope, or seed the
            // built-in Admin/User roles when no envelope is assigned.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "TenantRoles" ("Id", "TenantId", "EnvelopeRoleId", "Name", "Actions", "CreatedAt")
                SELECT gen_random_uuid(), t."Id", ar."Id", ar."Name", ar."Actions", now()
                FROM "Tenants" t
                JOIN "Envelopes" e ON e."Id" = t."EnvelopeId"
                JOIN "AppRoles" ar ON ar."EnvelopeId" = e."Id"
                WHERE t."Id" = {0}
                  AND NOT EXISTS (SELECT 1 FROM "TenantRoles" tr WHERE tr."TenantId" = t."Id")
                """,
                tenantId);

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "TenantRoles" ("Id", "TenantId", "EnvelopeRoleId", "Name", "Actions", "CreatedAt")
                SELECT gen_random_uuid(), t."Id", NULL, r.name, r.actions, now()
                FROM "Tenants" t
                CROSS JOIN (VALUES
                    ('Admin', ARRAY['view_widgets','manage_users','create_widget','edit_widget','delete_widget']::text[]),
                    ('User', ARRAY['view_widgets']::text[])
                ) AS r(name, actions)
                WHERE t."Id" = {0}
                  AND t."EnvelopeId" IS NULL
                  AND NOT EXISTS (SELECT 1 FROM "TenantRoles" tr WHERE tr."TenantId" = t."Id")
                """,
                tenantId);

            // Platform-managed Superadmin role (all actions) for every
            // tenant that doesn't have one yet.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "TenantRoles" ("Id", "TenantId", "EnvelopeRoleId", "Name", "Actions", "IsSystem", "CreatedAt")
                SELECT gen_random_uuid(), t."Id", NULL, 'Superadmin',
                       ARRAY['view_widgets','create_widget','edit_widget','delete_widget','manage_users']::text[],
                       true, now()
                FROM "Tenants" t
                WHERE t."Id" = {0}
                  AND NOT EXISTS (SELECT 1 FROM "TenantRoles" tr WHERE tr."TenantId" = t."Id" AND tr."IsSystem")
                """,
                tenantId);
        }

        // Seed an admin user for every tenant that has none, assigned to the
        // tenant's Admin role.
        if (!await db.Users.IgnoreQueryFilters().AnyAsync())
        {
            var seedSection = configuration.GetSection("SeedAdmin");
            var seedEmail = seedSection["Email"]!;
            var seedPassword = seedSection["Password"]!;

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(seedPassword);

            foreach (var tenantId in tenantIds)
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Users" ("Id", "Email", "PasswordHash", "RoleId", "TenantId", "CreatedAt")
                    SELECT gen_random_uuid(), {0}, {1}, tr."Id", t."Id", now()
                    FROM "Tenants" t
                    JOIN "TenantRoles" tr ON tr."TenantId" = t."Id" AND lower(tr."Name") = 'admin'
                    WHERE t."Id" = {2}
                      AND NOT EXISTS (SELECT 1 FROM "Users" u WHERE u."TenantId" = t."Id")
                    """,
                    seedEmail,
                    passwordHash,
                    tenantId);
            }

            Log.Information(
                "Seeded admin user ({Email}) for {TenantCount} tenant(s) via raw SQL",
                seedEmail,
                tenantIds.Count);
        }
    }
}
finally
{
    Log.CloseAndFlush();
}

// Expose the Program class so WebApplicationFactory (integration tests)
// can bootstrap the API.
public partial class Program
{
}

