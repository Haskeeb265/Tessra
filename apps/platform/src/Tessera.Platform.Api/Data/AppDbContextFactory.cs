using Finbuckle.MultiTenant.Extensions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Data;

/// <summary>
/// Design-time factory for EF Core migration tooling
/// (e.g., <c>dotnet ef migrations add</c>). Builds a minimal DI container
/// to resolve <see cref="AppDbContext"/> and its dependencies.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        var connectionString =
            Environment.GetEnvironmentVariable(
                "ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=tessera_platform;" +
               "Username=postgres;Password=postgres";

        optionsBuilder.UseNpgsql(connectionString);

        // Build a minimal service provider with Finbuckle multi-tenant
        // services and the AppDbContext. This avoids manually implementing
        // Finbuckle interfaces.
        var services = new ServiceCollection();

        // Register Finbuckle core services
        // (adds IMultiTenantContextAccessor, etc.).
        services.AddMultiTenant<Tenant>()
            .WithInMemoryStore(options => { });

        // Register the pre-configured DbContext options.
        services.AddSingleton(optionsBuilder.Options);

        // Register AppDbContext as scoped.
        services.AddScoped<AppDbContext>();

        var scope = services
            .BuildServiceProvider()
            .CreateScope();

        return scope.ServiceProvider
            .GetRequiredService<AppDbContext>();
    }
}
