using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;

using Microsoft.Extensions.Options;

using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Finbuckle.MultiTenant.Extensions;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Api.Endpoints;
using Tessera.Platform.Api.McpOAuth;
using Tessera.Platform.Api.Middleware;
using Tessera.Platform.Api.Services;
using Tessera.Platform.Domain.Models;
using Tessera.Platform.Observability.Middleware;

using OpenIddict.Abstractions;
using OpenIddict.Server;

using static OpenIddict.Abstractions.OpenIddictConstants;

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

    // OpenIddict's store context (applications, authorizations, scopes and
    // tokens). In OpenIddict 7.7 the EF Core stores are wired with
    // UseDbContext<T>() (see the AddOpenIddict() block below), and that
    // overload requires the context to be registered here. Provider choice
    // mirrors AppDbContext: PostgreSQL when configured, InMemory otherwise.
    builder.Services.AddDbContext<OpenIddictDbContext>(options =>
    {
        var connectionString = builder.Configuration
            .GetConnectionString("DefaultConnection");

        if (!string.IsNullOrEmpty(connectionString))
        {
            options.UseNpgsql(connectionString);
        }
        else
        {
            // Distinct name from AppDbContext so the two contexts never
            // share an InMemory store (and each test factory stays isolated).
            options.UseInMemoryDatabase(
                (builder.Configuration["Database:InMemoryName"]
                 ?? "TesseraPlatformDb") + "-OpenIddict");
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
    .AddJwtBearer()
    // Interactive cookie for the MCP authorization-code flow: /connect/login
    // signs it, /connect/authorize reads it. Registered under an explicit
    // scheme name so it never becomes the default scheme for the API.
    .AddCookie(McpOAuthConstants.CookieScheme, options =>
    {
        options.Cookie.Name = McpOAuthConstants.CookieName;
        options.Cookie.HttpOnly = true;
        // Lax (not Strict) so the cookie survives the redirect back from the
        // OAuth provider in the same site.
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = false;
        options.ExpireTimeSpan =
            McpOAuthConfig.CookieLifetime(builder.Configuration);

        // The flow drives authentication explicitly, so a 401/403 must never
        // turn into a redirect to a login page.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

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

    // HttpClient infrastructure. ClientIdMetadataService fetches CIMD client
    // metadata documents over HTTP, so the factory must be registered.
    builder.Services.AddHttpClient();

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

    // ============================================================
    // MCP OAuth Authorization Server (OpenIddict)
    // ============================================================

    // Register McpOAuthService (interactive login, cookie, consent, token
    // endpoint helpers) so the McpOAuthEndpoints handlers can inject it.
    builder.Services.AddScoped<McpOAuthService>();

    // Register the CIMD client-metadata service so the authorize handler can
    // fetch + validate + upsert clients whose client_id is an HTTPS URL.
    builder.Services.AddScoped<ClientIdMetadataService>();

    // Key material is loaded once and shared by the OpenIddict server config
    // below and the /.well-known/jwks endpoint further down. Loading it twice
    // (or with a null IConfiguration) would silently publish a different key
    // than the one signing tokens.
    var (signingKey, encryptionKey) = McpOAuthKeys.Load(builder.Configuration);

    // OpenIddict serves the MCP OAuth surface (docs/mcp/README.md §8).
    builder.Services.AddOpenIddict()
        // Persist applications, authorizations, scopes and tokens in EF Core.
        // OpenIddict 7.7 registers the stores through
        // AddCore → UseEntityFrameworkCore → UseDbContext<T>(); the older
        // AddEntityFrameworkCoreStores<T>() convenience overload is gone.
        .AddCore(core =>
        {
            core.UseEntityFrameworkCore()
                .UseDbContext<OpenIddictDbContext>();
        })
        .AddServer(options =>
        {
            // Endpoints are relative; the issuer is absolute and shared by all
            // tenants (the RFC 8707 resource/audience distinguishes them).
            var issuer = McpOAuthConfig.Issuer(builder.Configuration);

            // These MUST stay relative. OpenIddict matches an inbound request
            // against the configured endpoint URIs, so an absolute URI only
            // matches when the scheme+host line up too — behind Caddy the
            // request arrives as http://<tunnel>/..., which never matches
            // https://<tunnel>/... and the passthrough handler then throws
            // "The OpenID Connect request cannot be retrieved". The public
            // https URLs are produced from the forwarded headers trusted below.
            options.SetIssuer(issuer)
                   .SetAuthorizationEndpointUris("connect/authorize")
                   .SetTokenEndpointUris("connect/token")
                   .SetJsonWebKeySetEndpointUris(".well-known/jwks");

            options.AllowAuthorizationCodeFlow()
                   .AllowRefreshTokenFlow()
                   .RequireProofKeyForCodeExchange();

            // MCP clients (Claude web, Desktop, mobile) authenticate to the
            // token endpoint as public clients: PKCE, no client secret, and
            // token_endpoint_auth_method = "none". AcceptAnonymousClients()
            // makes the server accept token requests without a client_id/
            // client secret — the OAuth 2.1 "none" auth method. This is the
            // signal MCP hosts check before sending a CIMD client_id URL.
            options.AcceptAnonymousClients();

            // Coarse v1 scopes: access this workspace's tools, stay signed in.
            options.RegisterScopes(
                McpOAuthConstants.ScopeTools, Scopes.OfflineAccess);

            // Signed-only RS256 access tokens so the Python gateway validates
            // them from the public JWKS alone; refresh tokens stay encrypted.
            options.DisableAccessTokenEncryption();

            // Strict single-use refresh-token rotation (no reuse grace).
            options.SetRefreshTokenReuseLeeway(TimeSpan.Zero);

            // Every tenant has its own MCP endpoint, so OpenIddict's static
            // audience/resource permission checks cannot apply — tenant binding
            // is enforced by McpOAuthService and, later, the gateway.
            options.DisableResourceValidation();
            options.IgnoreAudiencePermissions();
            options.IgnoreResourcePermissions();

            // RS256 signing + AES encryption keys (loaded above). Persisted
            // under McpOAuth:KeyDirectory when configured, ephemeral otherwise.
            options.AddSigningKey(signingKey);
            options.AddEncryptionKey(encryptionKey);

            // CIMD: register a client whose client_id is an HTTPS URL before
            // the built-in client lookup runs (see CimdAuthorizationRequestHandler).
            options.AddEventHandler<
                OpenIddictServerEvents.ValidateAuthorizationRequestContext>(
                handler => handler
                    .UseScopedHandler<CimdAuthorizationRequestHandler>()
                    .SetOrder(int.MinValue + 10_000));

            // MCP hosts decide whether they may use "URL client ids" (CIMD)
            // by reading the discovery document, so advertise the two flags
            // OpenIddict 7.7 does not derive from its own configuration:
            //   - client_id_metadata_document_supported, and
            //   - "none" among token_endpoint_auth_methods_supported.
            // Without these, a host like Claude web silently falls back to a
            // non-CIMD client id and the authorize request is rejected as
            // invalid_client. Runs last so it amends the built-in payload.
            options.AddEventHandler<
                OpenIddictServerEvents.ApplyConfigurationResponseContext>(
                handler => handler
                    .UseInlineHandler(context =>
                    {
                        context.Response[
                            "client_id_metadata_document_supported"] = true;

                        context.Response[
                            "token_endpoint_auth_methods_supported"] =
                            ImmutableArray.Create(
                                // Public clients (PKCE, no secret) — MCP hosts.
                                ClientAuthenticationMethods.None,
                                ClientAuthenticationMethods.ClientSecretPost,
                                ClientAuthenticationMethods.ClientSecretBasic,
                                ClientAuthenticationMethods.PrivateKeyJwt);

                        return default;
                    })
                    .SetOrder(10_000));

            var aspNetCore = options.UseAspNetCore()
                .EnableAuthorizationEndpointPassthrough()
                .EnableTokenEndpointPassthrough();



            // Tests and plain-HTTP local dev run without TLS; in production the
            // API sits behind Caddy, whose forwarded headers are trusted, so the
            // transport-security requirement stays enforced.
            if (!builder.Environment.IsProduction())
            {
                aspNetCore.DisableTransportSecurityRequirement();
            }
        });

    builder.Services.AddOpenApi();

    // TLS is terminated at Caddy (and, for Claude web, the cloudflared
    // tunnel), so this process only ever sees plain HTTP. Without trusting
    // the forwarded scheme/host the request base URI is http://<tunnel>/...
    // and every URL OpenIddict advertises (discovery, jwks_uri) comes out
    // with the wrong scheme. Caddy forces X-Forwarded-Proto: https; see
    // apps/platform/caddy/Caddyfile.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedHost |
            ForwardedHeaders.XForwardedProto;

        // Caddy is the only ingress and its address is not stable inside the
        // compose network, so all immediate upstreams are trusted.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    // ============================================================
    // Request Pipeline
    // ============================================================

    var app = builder.Build();

    // Must run before anything reads the request scheme/host (routing,
    // OpenIddict, redirect helpers).
    app.UseForwardedHeaders();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();

    // Serve the JWKS at /.well-known/jwks before tenant middleware so it's
    // always reachable (docs/mcp/README.md §2.6).
    // Closes over the signingKey already loaded at startup — no reload, no
    // config mismatch (Load(null!) would silently switch to an ephemeral key).
    app.Use(async (context, next) =>
    {
        if (context.Request.Path == "/.well-known/jwks")
        {
            try
            {
                var jwksDoc = McpOAuthKeys.GetJwksDocument(signingKey);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(jwksDoc);
            }
            catch (Exception ex)
            {
                // Surface the real failure — the global exception handler
                // returns a bare 500 with no detail (docs/mcp/README.md §2.6).
                Console.WriteLine($"[JWKS] Export failed: {ex}");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(
                    new { error = "jwks_unavailable", detail = ex.Message });
            }

            return;
        }
        await next();
    });

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

    // MCP OAuth authorization server surface (connect/*) + CIMD client
    // registration. These endpoints are platform-level, not tenant-scoped, so
    // they must be mapped before UseMultiTenant in the pipeline enforces
    // X-Tenant-Id. Added here so OAuth handlers resolve after all other
    // services are registered.
    app.MapMcpOAuthEndpoints();

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
    app.MapManifestEndpoints();
    app.MapAdminEndpoints();

    // Server-to-server surface the Python MCP gateway calls (API-key auth).
    app.MapGatewayEndpoints();

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

        // Acme Dental fixture: the tenant's three sample tool manifests
        // (seeded on both the InMemory and PostgreSQL providers).
        await SampleSmbSeeder.SeedAcmeDentalAsync(db, scope.ServiceProvider);

        if (db.Database.IsRelational())
        {
            await SeedTenantDataAsync(db, app.Configuration);
        }

        // The OpenIddict store context has its own migration history table.
        var openIddictDb = scope.ServiceProvider
            .GetRequiredService<OpenIddictDbContext>();

        if (openIddictDb.Database.IsRelational() &&
            app.Configuration.GetValue<bool>("Database:AutoMigrate"))
        {
            openIddictDb.Database.Migrate();
        }

        await SeedMcpOAuthApplicationsAsync(scope.ServiceProvider);
    }

    app.Run();

    // ================================================================
    // Seeds the pre-registered MCP OAuth client. Real MCP hosts (Claude web,
    // Desktop, mobile) register themselves through CIMD, but the scripted PKCE
    // harness and MCP Inspector use a fixed loopback client.
    // ================================================================
    static async Task SeedMcpOAuthApplicationsAsync(IServiceProvider services)
    {
        var applications = services
            .GetRequiredService<IOpenIddictApplicationManager>();

        if (await applications.FindByClientIdAsync(
                McpOAuthConstants.LocalDevClientId) is not null)
        {
            return;
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = McpOAuthConstants.LocalDevClientId,
            DisplayName = "Tessera local dev",
            // Public client: PKCE, no client secret (OAuth 2.1 §2.1).
            ClientType = ClientTypes.Public,
            // The portal owns the consent UI; this only marks the app as
            // requiring an explicit grant before tokens are issued.
            ConsentType = ConsentTypes.Explicit
        };

        descriptor.RedirectUris.Add(
            new Uri(McpOAuthConstants.LocalDevRedirectUri));

        descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
        descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
        descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
        descriptor.Permissions.Add(
            Permissions.Prefixes.Scope + McpOAuthConstants.ScopeTools);
        descriptor.Permissions.Add(
            Permissions.Prefixes.Scope + Scopes.OfflineAccess);

        await applications.CreateAsync(descriptor);
    }

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
                },
                // The Acme Dental sample SMB fixture (docs/mcp/README.md §14).
                new Tenant
                {
                    Id = SampleSmbSeeder.AcmeDentalTenantId,
                    Identifier = SampleSmbSeeder.AcmeDentalTenantId,
                    Name = "Acme Dental",
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

