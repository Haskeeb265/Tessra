using Microsoft.EntityFrameworkCore;

namespace Tessera.Platform.Api.Data;

/// <summary>
/// OpenIddict's EF Core store context (applications, authorizations, tokens,
/// scopes). Deliberately a plain (non-tenant) DbContext: OAuth registrations
/// and tokens are platform-level records that must never be filtered by
/// Finbuckle tenant resolution — MCP access tokens are tenant-bound through
/// their claims/audience instead.
/// </summary>
public class OpenIddictDbContext : DbContext
{
    public OpenIddictDbContext(DbContextOptions<OpenIddictDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.UseOpenIddict();
    }
}
