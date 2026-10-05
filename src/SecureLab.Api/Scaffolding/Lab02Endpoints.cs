using Microsoft.EntityFrameworkCore;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;

namespace SecureLab.Api.Scaffolding;

// ЧАСТИНА 1: POST із валідацією (етап 2). Пошук НАВМИСНО залишено вразливим —
// він потрібен для vulnerable commit (етап 3). Виправлення пошуку — у частині 2.
public static class Lab02Endpoints
{
    public static void MapLab02Endpoints(this WebApplication app)
    {
        // ---- УРАЗЛИВИЙ ПОШУК (без змін щодо scaffold; не виправляти до vulnerable commit) ----
        app.MapGet("/api/incidents/search", async (string? q, string? sortBy, SecureLabDbContext db, CancellationToken ct) =>
        {
            var order = sortBy switch
            {
                null or "" or "createdAtUtc" => "created_at_utc DESC",
                "severity" => "severity", "status" => "status", _ => sortBy
            };
            var sql = "SELECT * FROM incidents WHERE title ILIKE '%" + (q ?? "")
                + "%' OR description ILIKE '%" + (q ?? "") + "%' ORDER BY " + order + " LIMIT 50";
            var rows = await db.Incidents.FromSqlRaw(sql).AsNoTracking().ToListAsync(ct);
            return Results.Ok(rows.Select(row => new
            {
                row.Id, row.Title, row.Description,
                Severity = row.Severity.ToString(), Status = row.Status.ToString(), row.CreatedAtUtc
            }));
        });

        // ---- POST /api/incidents з серверною валідацією ----
        app.MapPost("/api/incidents", async (CreateIncidentRequest request, SecureLabDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow; // фіксуємо один раз
            var errors = new Dictionary<string, string[]>();

            // 1. required + довжина (довжину міряємо ДО Trim)
            if (string.IsNullOrWhiteSpace(request.Title))
                errors["title"] = ["Title обов'язковий."];
            else if (request.Title.Length > 160)
                errors["title"] = ["Title не довший за 160 символів."];

            if (string.IsNullOrWhiteSpace(request.Description))
                errors["description"] = ["Description обов'язковий."];
            else if (request.Description.Length > 4000)
                errors["description"] = ["Description не довший за 4000 символів."];

            // 2. severity: і TryParse, і IsDefined (рядок "7" проходить TryParse, але не IsDefined)
            var severityIsValid =
                Enum.TryParse<IncidentSeverity>(request.Severity, ignoreCase: true, out var severity)
                && Enum.IsDefined(severity);
            if (!severityIsValid)
                errors["severity"] = ["Допустимі значення: Low, Medium, High, Critical."];

            // 3. дата: обов'язкова, не пізніше now + 5 хв
            if (request.OccurredAtUtc is null)
                errors["occurredAtUtc"] = ["occurredAtUtc обов'язковий."];
            else if (request.OccurredAtUtc > now.AddMinutes(5))
                errors["occurredAtUtc"] = ["occurredAtUtc не може бути більше ніж на 5 хвилин у майбутньому."];

            // 4. нормалізація один раз
            var title = request.Title?.Trim() ?? "";
            var description = request.Description?.Trim() ?? "";

            // 5. ➕ ДОБРЕ (T-09): cross-field для High/Critical
            if (severityIsValid
                && (severity == IncidentSeverity.High || severity == IncidentSeverity.Critical)
                && !errors.ContainsKey("description")
                && description.Length < 40)
                errors["description"] = ["Для High/Critical опис має містити щонайменше 40 символів."];

            // 6. усі помилки контракту — одна відповідь 400, до звернення до БД
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            // 7. ➕ ДОБРЕ (T-03): предметний конфлікт (регістр враховується; Closed не блокує)
            if (await db.Incidents.AnyAsync(
                    item => item.Title == title && item.Status != IncidentStatus.Closed, ct))
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Конфлікт",
                    detail: "Інцидент із таким заголовком уже існує.");

            // 8. entity: server-managed поля задає лише сервер
            var incident = new Incident
            {
                Id = Guid.NewGuid(), OwnerUserId = DbSeeder.AliceId,
                Title = title, Description = description,
                Severity = severity,
                Status = IncidentStatus.New,
                OccurredAtUtc = request.OccurredAtUtc!.Value.ToUniversalTime(),
                CreatedAtUtc = now, UpdatedAtUtc = now
            };
            db.Incidents.Add(incident);
            await db.SaveChangesAsync(ct);

            // 9. response DTO
            return Results.Created($"/api/incidents/{incident.Id}",
                new CreatedIncidentResponse(
                    incident.Id, incident.Title, incident.Severity.ToString(),
                    incident.Status.ToString(), incident.OccurredAtUtc,
                    incident.CreatedAtUtc, incident.UpdatedAtUtc));
        });
    }
}

public sealed record CreateIncidentRequest(
    string? Title, string? Description, string? Severity, DateTimeOffset? OccurredAtUtc);

public sealed record CreatedIncidentResponse(
    Guid Id, string Title, string Severity, string Status,
    DateTimeOffset OccurredAtUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
