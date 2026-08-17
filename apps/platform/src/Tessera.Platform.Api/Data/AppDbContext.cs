using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.EntityFrameworkCore;
using Finbuckle.MultiTenant.EntityFrameworkCore.Extensions;

using Microsoft.EntityFrameworkCore;

using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Data;

public class AppDbContext : MultiTenantDbContext
{
    public AppDbContext(
        IMultiTenantContextAccessor multiTenantContextAccessor,
        DbContextOptions<AppDbContext> options)
        : base(multiTenantContextAccessor, options)
    {
    }

    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<TenantRole> TenantRoles => Set<TenantRole>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

    // Platform-level entities (NOT multi-tenant): tenants, envelopes,
    // envelope roles, and superadmin accounts are owned by the platform.
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Envelope> Envelopes => Set<Envelope>();
    public DbSet<AppRole> AppRoles => Set<AppRole>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ============================================================
        // Platform-Level Entities (Not Tenant-Scoped)
        // ============================================================

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(t => t.Id);

            entity.Property(t => t.Identifier)
                .IsRequired()
                .HasMaxLength(200);

            entity.HasIndex(t => t.Identifier)
                .IsUnique();

            entity.Property(t => t.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(t => t.Status)
                .HasConversion<string>()
                .HasMaxLength(20);

            // Optimistic concurrency via PostgreSQL's xmin system column
            // (uint + OnAddOrUpdate + IsConcurrencyToken → Npgsql maps it to
            // xmin, which updates on every write and creates no column).
            entity.Property(t => t.RowVersion)
                .HasColumnName("xmin")
                .HasColumnType("xmin")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });

        modelBuilder.Entity<Envelope>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(100);

            entity.HasIndex(e => e.Name)
                .IsUnique();

            entity.Property(e => e.Description)
                .HasMaxLength(500);
        });

        modelBuilder.Entity<AppRole>(entity =>
        {
            entity.HasKey(r => r.Id);

            entity.Property(r => r.Name)
                .IsRequired()
                .HasMaxLength(100);

            entity.HasIndex(r => new { r.EnvelopeId, r.Name })
                .IsUnique();

            entity.HasOne<Envelope>()
                .WithMany(e => e.Roles)
                .HasForeignKey(r => r.EnvelopeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.HasKey(a => a.Id);

            entity.Property(a => a.Email)
                .IsRequired()
                .HasMaxLength(256);

            entity.HasIndex(a => a.Email)
                .IsUnique();

            entity.Property(a => a.PasswordHash)
                .IsRequired();
        });

        // Tenant-owned roles: copied from the assigned envelope template,
        // then owned by the tenant (renames/edits never cascade back).
        modelBuilder.Entity<TenantRole>(entity =>
        {
            entity.HasKey(r => r.Id);

            entity.Property(r => r.Name)
                .IsRequired()
                .HasMaxLength(100);

            entity.HasIndex(r => new { r.TenantId, r.Name })
                .IsUnique();

            entity.Property(r => r.RowVersion)
                .HasColumnName("xmin")
                .HasColumnType("xmin")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.IsMultiTenant();
        });

        // Invitations: tenant-scoped, redeemed at registration.
        modelBuilder.Entity<Invitation>(entity =>
        {
            entity.HasKey(i => i.Id);

            entity.Property(i => i.Email)
                .IsRequired()
                .HasMaxLength(256);

            entity.Property(i => i.TokenHash)
                .IsRequired()
                .HasMaxLength(64);

            entity.HasIndex(i => i.Email);
            entity.HasIndex(i => i.TokenHash);

            entity.IsMultiTenant();
        });

        // ============================================================
        // Tenant-Scoped Entities
        // ============================================================

        modelBuilder.Entity<Widget>(entity =>
        {
            entity.HasKey(w => w.Id);

            entity.Property(w => w.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(w => w.Description)
                .HasMaxLength(1000);

            entity.IsMultiTenant();
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);

            entity.Property(u => u.Email)
                .IsRequired()
                .HasMaxLength(256);

            // Non-unique lookup index; uniqueness is enforced by the filtered
            // unique index (Email, TenantId) WHERE NOT "IsDeleted" created in
            // the migration (soft-deleted accounts must not block re-use of
            // their email, and the app-level AnyAsync check is a second layer).
            entity.HasIndex(u => u.Email);

            entity.HasIndex(u => u.RoleId);

            entity.Property(u => u.PasswordHash)
                .IsRequired();

            entity.Property(u => u.RowVersion)
                .HasColumnName("xmin")
                .HasColumnType("xmin")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.IsMultiTenant();
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(r => r.Id);

            entity.Property(r => r.Token)
                .IsRequired()
                .HasMaxLength(512);

            entity.HasIndex(r => r.UserId);
            entity.HasIndex(r => r.FamilyId);

            entity.IsMultiTenant();
        });
    }
}
