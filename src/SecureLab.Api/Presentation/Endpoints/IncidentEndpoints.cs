using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;
using SecureLab.Api.Application.Incidents;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;
using SecureLab.Api.Presentation.Contracts;

namespace SecureLab.Api.Presentation.Endpoints;

public static class IncidentEndpoints
{
    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/incidents")
            .WithTags("Incidents")
            .RequireAuthorization();

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

        group.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateIncident")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteIncident")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/triage", TriageAsync)
            .WithName("TriageIncident")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGet("/api/incident-comments/{id:guid}", GetCommentAsync)
            .WithName("GetIncidentComment")
            .WithTags("Incidents")
            .RequireAuthorization()
            .Produces<IncidentCommentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

  
    private static IResult IncidentNotFound() => Results.Problem(
        title: "Інцидент не знайдено",
        statusCode: StatusCodes.Status404NotFound);

    private static async Task<bool> IsAllowedAsync(
        IAuthorizationService authorizationService,
        HttpContext httpContext,
        Incident incident,
        OperationAuthorizationRequirement operation)
        => (await authorizationService.AuthorizeAsync(httpContext.User, incident, operation)).Succeeded;

    private static async Task<IResult> GetListAsync(
        string? status,
        HttpContext httpContext,
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

        if (!Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var ownerId))
        {
            return Results.Challenge();
        }

        var isAnalyst = httpContext.User.IsInRole(DbSeeder.AnalystRole);
        return Results.Ok(await queries.GetListAsync(parsedStatus, ownerId, isAnalyst, cancellationToken));
    }

    private static async Task<IResult> GetDetailsAsync(
        Guid id,
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        SecureLabDbContext dbContext,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        var incident = await dbContext.Incidents.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null
            || !await IsAllowedAsync(authorizationService, httpContext, incident, IncidentOperations.Read))
        {
            return IncidentNotFound();
        }

        var includeInternal = httpContext.User.IsInRole(DbSeeder.AnalystRole);
        var details = await queries.GetDetailsAsync(id, includeInternal, cancellationToken);
        return details is null ? IncidentNotFound() : Results.Ok(details);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateIncidentRequest request,
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        SecureLabDbContext dbContext,
        CancellationToken cancellationToken)
    {
      
        var incident = await dbContext.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null
            || !await IsAllowedAsync(authorizationService, httpContext, incident, IncidentOperations.Read))
        {
            return IncidentNotFound();
        }

        if (!await IsAllowedAsync(authorizationService, httpContext, incident, IncidentOperations.Update))
        {
            return Results.Forbid();
        }

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200)
            errors["title"] = ["Title: 1–200 символів."];
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 4000)
            errors["description"] = ["Description: 1–4000 символів."];
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        incident.Title = request.Title.Trim();
        incident.Description = request.Description.Trim();
        incident.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        SecureLabDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var incident = await dbContext.Incidents
            .Include(i => i.Comments)
            .Include(i => i.StatusHistory)
            .SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null
            || !await IsAllowedAsync(authorizationService, httpContext, incident, IncidentOperations.Read))
        {
            return IncidentNotFound();
        }

        if (!await IsAllowedAsync(authorizationService, httpContext, incident, IncidentOperations.Delete))
        {
            return Results.Forbid();
        }

        dbContext.IncidentComments.RemoveRange(incident.Comments);
        dbContext.IncidentStatusHistory.RemoveRange(incident.StatusHistory);
        dbContext.Incidents.Remove(incident);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> TriageAsync(
        Guid id,
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        SecureLabDbContext dbContext,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        var incident = await dbContext.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null
            || !await IsAllowedAsync(authorizationService, httpContext, incident, IncidentOperations.Read))
        {
            return IncidentNotFound();
        }

        if (!await IsAllowedAsync(authorizationService, httpContext, incident, IncidentOperations.Triage))
        {
            // Audit пишемо тут: декларативна policy на маршруті не виконала б код endpoint.
            logger.LogWarning(
                "authorization.denied subject={SubjectId} action={Action} resource={ResourceId} "
                + "result={Result} occurredAtUtc={OccurredAtUtc:O} traceId={TraceId}",
                httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier),
                "incident.triage",
                incident.Id,
                "denied",
                DateTimeOffset.UtcNow,
                Activity.Current?.Id ?? httpContext.TraceIdentifier);
            return Results.Forbid();
        }

        if (incident.Status != IncidentStatus.New)
        {
            return Results.Problem(title: "Перехід неможливий", statusCode: StatusCodes.Status409Conflict);
        }

        if (!Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Results.Challenge();
        }

        var now = DateTimeOffset.UtcNow;
        incident.Status = IncidentStatus.Triaged; // наступний статус визначає сервер
        incident.UpdatedAtUtc = now;
        dbContext.IncidentStatusHistory.Add(new IncidentStatusHistory
        {
            Id = Guid.NewGuid(),
            IncidentId = incident.Id,
            ChangedByUserId = userId,
            OldStatus = IncidentStatus.New,
            NewStatus = IncidentStatus.Triaged,
            Note = "Triage",
            CreatedAtUtc = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetCommentAsync(
        Guid id,
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        SecureLabDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var comment = await dbContext.IncidentComments.AsNoTracking()
            .Include(c => c.Incident)
            .Include(c => c.Author)
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (comment is null)
        {
            return IncidentNotFound();
        }

     
        var canReadParent = await IsAllowedAsync(
            authorizationService, httpContext, comment.Incident, IncidentOperations.Read);
        var isAnalyst = httpContext.User.IsInRole(DbSeeder.AnalystRole);
        if (!canReadParent || (comment.IsInternal && !isAnalyst))
        {
            return IncidentNotFound();
        }

        return Results.Ok(new IncidentCommentResponse(
            comment.Id, comment.Author.DisplayName, comment.Text, comment.CreatedAtUtc));
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

        var traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;
        var data = await queries.GetSeveritySummaryAsync(filter, traceId, ct);
        return Results.Ok(data);
    }
}