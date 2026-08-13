using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Tessera.Platform.Domain.Models;
using Finbuckle.MultiTenant.EntityFrameworkCore.Extensions;

namespace Tessera.Platform.Api.Data;

public class AppDbContext : MultiTenantDbContext
{
    public AppDbContext(IMultiTenantContextAccessor multiTenantContextAccessor, DbContextOptions<AppDbContext> options) : base(multiTenantContextAccessor, options)
    {
    }

    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    // Platform-level entities (NOT multi-tenant): tenants, envelopes,
    // envelope roles, and superadmin accounts are owned by the platform.
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Envelope> Envelopes => Set<Envelope>();
    public DbSet<AppRole> AppRoles => Set<AppRole>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ─── Platform-level entities (not tenant-scoped) ───────────────

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Identifier).IsRequired().HasMaxLength(200);
            entity.HasIndex(t => t.Identifier).IsUnique();
            entity.Property(t => t.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<Envelope>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<AppRole>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Name).IsRequired().HasMaxLength(100);
            entity.HasIndex(r => new { r.EnvelopeId, r.Name }).IsUnique();
            entity.HasOne<Envelope>()
                .WithMany(e => e.Roles)
                .HasForeignKey(r => r.EnvelopeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Email).IsRequired().HasMaxLength(256);
            entity.HasIndex(a => a.Email).IsUnique();
            entity.Property(a => a.PasswordHash).IsRequired();
        });

        // ─── Tenant-scoped entities ────────────────────────────────────

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
