using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tessera.Platform.Api.Migrations
{
    /// <inheritdoc />
    public partial class TenantOwnedRolesAndSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ------------------------------------------------------------
            // 1. Schema: new columns + tables (Role column kept until the
            //    backfill below has copied its values into RoleId).
            // ------------------------------------------------------------

            migrationBuilder.AddColumn<bool>(
                name: "EmailVerified",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MfaEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MfaSecret",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PasswordResetToken",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordResetTokenExpiresAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TokenVersion",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VerificationToken",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerificationTokenExpiresAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            // PostgreSQL xmin system column (optimistic concurrency, D2).
            // Npgsql's SQL generator skips system columns — no column is
            // physically created.
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Users",
                type: "xmin",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Tenants",
                type: "xmin",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            // Refresh tokens get a family for rotation + reuse detection (C1).
            migrationBuilder.AddColumn<Guid>(
                name: "FamilyId",
                table: "RefreshTokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    EnvelopeRoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Actions = table.Column<List<string>>(type: "text[]", nullable: false),
                    xmin = table.Column<uint>(type: "xmin", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantRoles", x => x.Id);
                });

            // ------------------------------------------------------------
            // 2. Backfill: every tenant gets its own role set, copied from
            //    the assigned envelope template (or the built-ins when no
            //    envelope is assigned).
            // ------------------------------------------------------------

            migrationBuilder.Sql(
                """
                INSERT INTO "TenantRoles" ("Id", "TenantId", "EnvelopeRoleId", "Name", "Actions", "CreatedAt")
                SELECT gen_random_uuid(), t."Id", ar."Id", ar."Name", ar."Actions", now()
                FROM "Tenants" t
                JOIN "Envelopes" e ON e."Id" = t."EnvelopeId"
                JOIN "AppRoles" ar ON ar."EnvelopeId" = e."Id"
                WHERE NOT EXISTS (SELECT 1 FROM "TenantRoles" tr WHERE tr."TenantId" = t."Id")
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "TenantRoles" ("Id", "TenantId", "EnvelopeRoleId", "Name", "Actions", "CreatedAt")
                SELECT gen_random_uuid(), t."Id", NULL, r.name, r.actions, now()
                FROM "Tenants" t
                CROSS JOIN (VALUES
                    ('Admin', ARRAY['view_widgets','manage_users','create_widget','edit_widget','delete_widget']::text[]),
                    ('User', ARRAY['view_widgets']::text[])
                ) AS r(name, actions)
                WHERE t."EnvelopeId" IS NULL
                  AND NOT EXISTS (SELECT 1 FROM "TenantRoles" tr WHERE tr."TenantId" = t."Id")
                """);

            // ------------------------------------------------------------
            // 3. Backfill Users.RoleId from the old Role name (matched
            //    case-insensitively against the tenant's role set), give
            //    every existing token its own family, and mark existing
            //    users as email-verified (they predate verification).
            // ------------------------------------------------------------

            migrationBuilder.Sql(
                """
                UPDATE "Users" u
                SET "RoleId" = tr."Id"
                FROM "TenantRoles" tr
                WHERE tr."TenantId" = u."TenantId"
                  AND lower(tr."Name") = lower(u."Role")
                """);

            migrationBuilder.Sql(
                "UPDATE \"RefreshTokens\" SET \"FamilyId\" = gen_random_uuid();");

            migrationBuilder.Sql(
                "UPDATE \"Users\" SET \"EmailVerified\" = true;");

            // ------------------------------------------------------------
            // 4. Indexes, including the filtered unique (Email, TenantId)
            //    index (D1): soft-deleted accounts don't block email reuse.
            // ------------------------------------------------------------

            migrationBuilder.CreateIndex(
                name: "IX_Users_RoleId",
                table: "Users",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_FamilyId",
                table: "RefreshTokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_Email",
                table: "Invitations",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_TenantRoles_TenantId_Name",
                table: "TenantRoles",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_Users_Email_TenantId"
                ON "Users" ("Email", "TenantId")
                WHERE NOT "IsDeleted";
                """);

            // ------------------------------------------------------------
            // 5. The old Role string is now fully superseded by RoleId.
            // ------------------------------------------------------------

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Email_TenantId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_RoleId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_FamilyId",
                table: "RefreshTokens");

            // Restore the role name column and backfill it from RoleId
            // while the TenantRoles table still exists.
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                UPDATE "Users" u
                SET "Role" = tr."Name"
                FROM "TenantRoles" tr
                WHERE tr."TenantId" = u."TenantId"
                  AND tr."Id" = u."RoleId"
                """);

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "TenantRoles");

            migrationBuilder.DropColumn(
                name: "EmailVerified",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MfaEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MfaSecret",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PasswordResetToken",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PasswordResetTokenExpiresAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RoleId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TokenVersion",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "VerificationToken",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "VerificationTokenExpiresAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "FamilyId",
                table: "RefreshTokens");
        }
    }
}
