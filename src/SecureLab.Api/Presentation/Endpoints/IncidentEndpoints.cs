using SecureLab.Api.Application.Incidents;
using SecureLab.Api.Data.Entities;
using SecureLab.Api.Presentation.Contracts;
using Microsoft.EntityFrameworkCore;
using SecureLab.Api.Data;

namespace SecureLab.Api.Presentation.Endpoints;

public static class IncidentEndpoints
{
    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/incidents")
            .WithTags("Incidents");
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
        group.MapPost("/{id:guid}/triage", TriageAsync);
        endpoints.MapGet("/api/incident-comments/{id:guid}", GetCommentAsync);

        group.MapGet("/", GetListAsync)
            .WithName("GetIncidents")
            .Produces<IReadOnlyList<IncidentListItemResponse>>()
            .ProducesValidationProblem();

        group.MapGet("/{id:guid}", GetDetailsAsync)
            .WithName("GetIncidentDetails")
            .Produces<IncidentDetailsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

       group.MapGet("/severity-summary", GetSeveritySummaryAsync)
            .WithName("GetIncidentSeveritySummary")
            .Produces<IReadOnlyList<IncidentSeveritySummaryResponse>>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static async Task<IResult> GetListAsync(
        string? status,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        IncidentStatus? parsedStatus = null;
        if (status is not null)
        {
            if (!Enum.TryParse<IncidentStatus>(status, ignoreCase: true, out var candidate)
                || !Enum.IsDefined(candidate))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["Допустимі значення: New, Triaged, InProgress, Resolved, Closed."]
                });
            }

            parsedStatus = candidate;
        }

        return Results.Ok(await queries.GetListAsync(parsedStatus, cancellationToken));
    }

    private static async Task<IResult> GetDetailsAsync(
        Guid id,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        var incident = await queries.GetDetailsAsync(id, cancellationToken);
        return incident is null
            ? Results.Problem(
                title: "Інцидент не знайдено",
                detail: $"Інцидент '{id}' не існує.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(incident);
    }

private static async Task<IResult> GetSeveritySummaryAsync(
        string? status,
        IncidentQueries queries,
        HttpContext ctx,
        CancellationToken ct)
    {
        IncidentStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<IncidentStatus>(status, true, out var parsed) || !Enum.IsDefined(parsed))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [$"Invalid status: '{status}'"] });

            filter = parsed;
        }

        var traceId = System.Diagnostics.Activity.Current?.Id ?? ctx.TraceIdentifier;
        var data = await queries.GetSeveritySummaryAsync(filter, traceId, ct);
        return Results.Ok(data);
    }
        private static async Task<IResult> UpdateAsync(
        Guid id, UpdateIncidentRequest request, SecureLabDbContext dbContext, CancellationToken ct)
    {
        var incident = await dbContext.Incidents.SingleOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return Results.NotFound();
        incident.Title = request.Title;
        incident.Description = request.Description;
        await dbContext.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, SecureLabDbContext dbContext, CancellationToken ct)
    {
        var incident = await dbContext.Incidents
            .Include(i => i.Comments).Include(i => i.StatusHistory)
            .SingleOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return Results.NotFound();
        dbContext.IncidentComments.RemoveRange(incident.Comments);
        dbContext.IncidentStatusHistory.RemoveRange(incident.StatusHistory);
        dbContext.Incidents.Remove(incident);
        await dbContext.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TriageAsync(
        Guid id, SecureLabDbContext dbContext, CancellationToken ct)
    {
        var incident = await dbContext.Incidents.SingleOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return Results.NotFound();
        incident.Status = IncidentStatus.Triaged;
        await dbContext.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetCommentAsync(
        Guid id, SecureLabDbContext dbContext, CancellationToken ct)
    {
        var comment = await dbContext.IncidentComments.AsNoTracking()
            .Include(c => c.Author)
            .SingleOrDefaultAsync(c => c.Id == id, ct);
        return comment is null
            ? Results.NotFound()
            : Results.Ok(new IncidentCommentResponse(
                comment.Id, comment.Author.DisplayName, comment.Text, comment.CreatedAtUtc));
    }
}
