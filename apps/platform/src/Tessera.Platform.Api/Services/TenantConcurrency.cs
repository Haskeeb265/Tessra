using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Tessera.Platform.Api.Data;

namespace Tessera.Platform.Api.Services;

internal static class TenantConcurrency
{
    public static async Task<IDbContextTransaction?> BeginTenantTransactionAsync(
        AppDbContext db,
        string tenantId)
    {
        if (!db.Database.IsRelational())
        {
            return null;
        }

        var transaction = await db.Database.BeginTransactionAsync();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({tenantId}, 0))");

        return transaction;
    }
}
