using Finbuckle.MultiTenant.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore;
using Serilog;

using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Data;

/// <summary>
/// Seeds the local/sample SMB used for testing the MCP flow: the
/// <c>acme-dental</c> tenant plus its tool manifests (docs/sample_smb.md,
/// docs/sample-smb/acme-dental-manifest.json). Seeding at startup keeps the
/// fixture deterministic across runs on both PostgreSQL and the InMemory
/// test provider.
/// </summary>
public static class SampleSmbSeeder
{
    /// <summary>Internal tenant id AND identifier of the sample SMB.</summary>
    public const string AcmeDentalTenantId = "acme-dental";

    /// <summary>
    /// The Acme Dental tool set. InputSchema/Execution mirror
    /// docs/sample-smb/acme-dental-manifest.json exactly — keep them in sync.
    /// </summary>
    public static List<ToolManifest> AcmeDentalManifests { get; } =
    [
        new ToolManifest
        {
            ToolName = "book_appointment",
            Description =
                "Books a dental appointment for a given date/time and " +
                "service type.",
            InputSchema =
                """
                {
                  "type": "object",
                  "properties": {
                    "patient_name": { "type": "string" },
                    "date": { "type": "string", "format": "date" },
                    "time": { "type": "string", "pattern": "^([01]\\d|2[0-3]):[0-5]\\d$" },
                    "service_type": {
                      "type": "string",
                      "enum": ["cleaning", "checkup", "whitening", "fillings"]
                    }
                  },
                  "required": ["patient_name", "date", "time", "service_type"]
                }
                """,
            Execution =
                """
                {
                  "type": "http",
                  "method": "POST",
                  "url": "https://api.acmedental.test/v1/appointments",
                  "auth": {
                    "type": "api_key",
                    "credential_ref": "vault://acme-dental/booking-api-key"
                  },
                  "body_template": {
                    "patient": "${patient_name}",
                    "preferred_time": "${date}T${time}:00Z",
                    "service": "${service_type}"
                  },
                  "response_mapping": "$.data.appointment"
                }
                """,
            RequiredScopes = ["appointments:write"]
        },
        new ToolManifest
        {
            ToolName = "list_appointments",
            Description =
                "Lists the current patient's upcoming appointments.",
            InputSchema =
                """
                {
                  "type": "object",
                  "properties": {
                    "patient_name": { "type": "string" }
                  },
                  "required": ["patient_name"]
                }
                """,
            Execution =
                """
                {
                  "type": "http",
                  "method": "GET",
                  "url": "https://api.acmedental.test/v1/appointments",
                  "auth": {
                    "type": "api_key",
                    "credential_ref": "vault://acme-dental/booking-api-key"
                  },
                  "query_template": {
                    "patient": "${patient_name}"
                  },
                  "response_mapping": "$.data.appointments"
                }
                """,
            RequiredScopes = ["appointments:read"]
        },
        new ToolManifest
        {
            ToolName = "cancel_appointment",
            Description = "Cancels an existing appointment by its ID.",
            InputSchema =
                """
                {
                  "type": "object",
                  "properties": {
                    "appointment_id": { "type": "string" }
                  },
                  "required": ["appointment_id"]
                }
                """,
            Execution =
                """
                {
                  "type": "http",
                  "method": "DELETE",
                  "url": "https://api.acmedental.test/v1/appointments/${appointment_id}",
                  "auth": {
                    "type": "api_key",
                    "credential_ref": "vault://acme-dental/booking-api-key"
                  },
                  "response_mapping": "$.data.cancellation"
                }
                """,
            RequiredScopes = ["appointments:write"]
        }
    ];

    /// <summary>
    /// Ensures the sample SMB's manifests exist for the acme-dental tenant
    /// (no-op when the tenant is missing or already has manifests). Runs
    /// through a tenant-bound context because Finbuckle's EnforceMultiTenant
    /// stamps TenantId on save and there is no request tenant at startup.
    /// </summary>
    public static async Task SeedAcmeDentalAsync(
        AppDbContext db,
        IServiceProvider serviceProvider)
    {
        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.Identifier == AcmeDentalTenantId && !t.IsDeleted);

        if (tenant is null)
        {
            return;
        }

        await using var bound =
            MultiTenantDbContext.Create<AppDbContext, Tenant>(
                new Tenant { Id = tenant.Id },
                serviceProvider);

        // The tenant already has manifests (created via the API or a prior
        // seed) — respect that and don't re-add the samples.
        if (await bound.ToolManifests.AnyAsync())
        {
            return;
        }

        bound.ToolManifests.AddRange(AcmeDentalManifests);
        await bound.SaveChangesAsync();

        Log.Information(
            "Seeded {Count} sample tool manifest(s) for tenant {TenantId}",
            AcmeDentalManifests.Count,
            tenant.Id);
    }
}