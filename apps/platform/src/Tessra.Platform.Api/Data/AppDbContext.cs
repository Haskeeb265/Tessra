using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Tessra.Platform.Domain.Models;
using Finbuckle.MultiTenant.EntityFrameworkCore.Extensions;

namespace Tessra.Platform.Api.Data;

public class AppDbContext : MultiTenantDbContext
{
    public AppDbContext(IMultiTenantContextAccessor multiTenantContextAccessor, DbContextOptions<AppDbContext> options) : base(multiTenantContextAccessor, options)
    {
    }

    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Widget>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Name).IsRequired().HasMaxLength(200);
            entity.Property(w => w.Description).HasMaxLength(1000);
            entity.IsMultiTenant();
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Email).IsRequired().HasMaxLength(256);
            entity.HasIndex(u => u.Email);
            entity.Property(u => u.PasswordHash).IsRequired();
            entity.IsMultiTenant();
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Token).IsRequired().HasMaxLength(512);
            entity.HasIndex(r => r.UserId);
            entity.IsMultiTenant();
        });
    }
}
