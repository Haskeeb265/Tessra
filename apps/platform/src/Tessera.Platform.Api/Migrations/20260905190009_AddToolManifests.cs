using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tessera.Platform.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddToolManifests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ToolManifests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    InputSchema = table.Column<string>(type: "text", nullable: false),
                    Execution = table.Column<string>(type: "text", nullable: false),
                    RequiredScopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    RateLimitOverride = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    xmin = table.Column<uint>(type: "xmin", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolManifests", x => x.Id);
                });

            // Unique per tenant only among ACTIVE manifests — a soft-deleted
            // manifest frees its tool name for re-use. EF Core cannot model
            // filtered indexes, hence raw SQL.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_ToolManifests_TenantId_ToolName_OnlyActive"
                ON "ToolManifests" ("TenantId", "ToolName")
                WHERE NOT "IsDeleted";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ToolManifests_TenantId_ToolName_OnlyActive",
                table: "ToolManifests");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ToolManifests");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "ToolManifests");

            migrationBuilder.DropTable(
                name: "ToolManifests");
        }
    }
}
