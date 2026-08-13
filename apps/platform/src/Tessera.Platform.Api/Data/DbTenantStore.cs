using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Data;

/// <summary>
/// Finbuckle tenant store backed by the database, so superadmins can create
/// tenants at runtime. The previous in-memory store was hardcoded to the two
/// seed tenants. Resolves tenant records from the Tenants table by identifier
/// (what the client sends in the X-Tenant-Id header) or by internal id.
/// </summary>
public class DbTenantStore : IMultiTenantStore<Tenant>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DbTenantStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<Tenant?> GetAsync(string id) => await GetByIdAsync(id);

    public async Task<Tenant?> GetByIdentifierAsync(string identifier)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Identifier == identifier);
    }

    public async Task<Tenant?> GetByIdAsync(string id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<IEnumerable<Tenant>> GetAllAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Tenants.AsNoTracking().ToListAsync();
    }

    public async Task<IEnumerable<Tenant>> GetAllAsync(int start, int pageSize)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Tenants.AsNoTracking().Skip(start).Take(pageSize).ToListAsync();
    }

    public async Task<bool> AddAsync(Tenant tenant)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpdateAsync(Tenant tenant)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tenants.Update(tenant);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveAsync(string id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
        if (tenant is null)
        {
            return false;
        }
        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync();
        return true;
    }
}
