using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tessera.Platform.Api.Migrations
{
    /// <inheritdoc />
    public partial class TenantSuperadminRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                table: "TenantRoles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Every existing tenant gets the platform-managed Superadmin
            // role (holds all actions, IsSystem = true) so the first
            // invitation redeemed in a workspace can be granted it.
            migrationBuilder.Sql(
                """
                INSERT INTO "TenantRoles" ("Id", "TenantId", "EnvelopeRoleId", "Name", "Actions", "IsSystem", "CreatedAt")
                SELECT gen_random_uuid(), t."Id", NULL, 'Superadmin',
                       ARRAY['view_widgets','create_widget','edit_widget','delete_widget','manage_users']::text[],
                       true, now()
                FROM "Tenants" t
                WHERE NOT EXISTS (
                    SELECT 1 FROM "TenantRoles" tr
                    WHERE tr."TenantId" = t."Id" AND tr."IsSystem"
                )
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSystem",
                table: "TenantRoles");
        }
    }
}
