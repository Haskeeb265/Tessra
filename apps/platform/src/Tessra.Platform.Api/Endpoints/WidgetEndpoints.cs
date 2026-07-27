using Microsoft.EntityFrameworkCore;
using Tessra.Platform.Api.Data;
using Tessra.Platform.Domain.Models;

namespace Tessra.Platform.Api.Endpoints;

public static class WidgetEndpoints
{
    public static void MapWidgetEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/widgets").RequireAuthorization();

        group.MapGet(
                "/",
                async (AppDbContext db) =>
                {
                    var widgets = await db.Widgets.ToListAsync();

                    return Results.Ok(widgets);
                })
            .WithName("GetWidgets");

        group.MapGet(
                "/{id:guid}",
                async (Guid id, AppDbContext db) =>
                {
                    var widget = await db.Widgets.FindAsync(id);

                    return widget is null
                        ? Results.NotFound()
                        : Results.Ok(widget);
                })
            .WithName("GetWidgetById");

        group.MapPost(
                "/",
                async (Widget widget, AppDbContext db) =>
                {
                    db.Widgets.Add(widget);
                    await db.SaveChangesAsync();

                    return Results.Created($"/widgets/{widget.Id}", widget);
                })
            .WithName("CreateWidget");

        group.MapPut(
                "/{id:guid}",
                async (Guid id, Widget updatedWidget, AppDbContext db) =>
                {
                    var widget = await db.Widgets.FindAsync(id);

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
                async (Guid id, AppDbContext db) =>
                {
                    var widget = await db.Widgets.FindAsync(id);

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