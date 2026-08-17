using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Api.Services;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Endpoints;

public static class WidgetEndpoints
{
    public static void MapWidgetEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/widgets").RequireAuthorization();

        // ============================================================
        // Widget CRUD (action-enforced — see ActionCatalog)
        // ============================================================

        group.MapGet(
            "/",
            async (
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ViewWidgets);

                if (denied is not null)
                {
                    return denied;
                }

                var widgets = await db.Widgets.ToListAsync();

                return Results.Ok(widgets);
            })
            .WithName("GetWidgets");

        group.MapGet(
            "/{id:guid}",
            async (
                Guid id,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ViewWidgets);

                if (denied is not null)
                {
                    return denied;
                }

                var widget = await db.Widgets
                    .FirstOrDefaultAsync(w => w.Id == id);

                return widget is null
                    ? Results.NotFound()
                    : Results.Ok(widget);
            })
            .WithName("GetWidgetById");

        group.MapPost(
            "/",
            async (
                Widget widget,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.CreateWidget);

                if (denied is not null)
                {
                    return denied;
                }

                db.Widgets.Add(widget);
                await db.SaveChangesAsync();

                return Results.Created($"/widgets/{widget.Id}", widget);
            })
            .WithName("CreateWidget");

        group.MapPut(
            "/{id:guid}",
            async (
                Guid id,
                Widget updatedWidget,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.EditWidget);

                if (denied is not null)
                {
                    return denied;
                }

                var widget = await db.Widgets
                    .FirstOrDefaultAsync(w => w.Id == id);

                if (widget is null)
                {
                    return Results.NotFound();
                }

                widget.Name = updatedWidget.Name;
                widget.Description = updatedWidget.Description;

                await db.SaveChangesAsync();

                return Results.Ok(widget);
            })
            .WithName("UpdateWidget");

        group.MapDelete(
            "/{id:guid}",
            async (
                Guid id,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.DeleteWidget);

                if (denied is not null)
                {
                    return denied;
                }

                var widget = await db.Widgets
                    .FirstOrDefaultAsync(w => w.Id == id);

                if (widget is null)
                {
                    return Results.NotFound();
                }

                db.Widgets.Remove(widget);
                await db.SaveChangesAsync();

                return Results.NoContent();
            })
            .WithName("DeleteWidget");
    }
}
