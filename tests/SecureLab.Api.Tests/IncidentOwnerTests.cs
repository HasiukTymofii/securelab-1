using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureLab.Api.Data;

namespace SecureLab.Api.Tests;

public sealed class IncidentOwnerTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    private static string Pwd =>
        Environment.GetEnvironmentVariable("SeedUsers__Password")
        ?? throw new InvalidOperationException("Set local SeedUsers__Password outside Git.");

    // CP-03: анонімний POST до захищеного endpoint
    [Fact]
    public async Task Create_Anonymous_Returns401()
    {
        using var anonymous = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await anonymous.PostAsJsonAsync("/api/incidents", new
        {
            title = $"ANON-{Guid.NewGuid():N}",
            description = "Штучний опис для перевірки.",
            severity = "Low",
            occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    // A-04: у DTO немає owner, власник береться з principal (Bob)
    [Fact]
    public async Task A04_Create_WithoutOwner_UsesPrincipal()
    {
        var title = $"A04-{Guid.NewGuid():N}";
        try
        {
            using var bob = await factory.LoginAsync("bob", Pwd);
            using var response = await bob.PostAsJsonAsync("/api/incidents", new
            {
                title,
                description = "Штучний опис для перевірки.",
                severity = "Low",
                occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var id = doc.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(DbSeeder.BobId, await OwnerInDbAsync(id));
        }
        finally
        {
            await Cleanup(title);
        }
    }

    // A-05: клієнт надіслав чужий ownerUserId (Alice), власник усе одно Bob
    [Fact]
    public async Task A05_Create_WithForgedOwner_IgnoresClientValue()
    {
        var title = $"A05-{Guid.NewGuid():N}";
        try
        {
            using var bob = await factory.LoginAsync("bob", Pwd);
            using var response = await bob.PostAsJsonAsync("/api/incidents", new
            {
                title,
                description = "Штучний опис для перевірки.",
                severity = "Low",
                occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                ownerUserId = DbSeeder.AliceId
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var id = doc.RootElement.GetProperty("id").GetGuid();
            var owner = await OwnerInDbAsync(id);
            Assert.Equal(DbSeeder.BobId, owner);
            Assert.NotEqual(DbSeeder.AliceId, owner);
        }
        finally
        {
            await Cleanup(title);
        }
    }

    private async Task<Guid> OwnerInDbAsync(Guid incidentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        var incident = await db.Incidents.AsNoTracking()
            .SingleAsync(item => item.Id == incidentId);
        return incident.OwnerUserId;
    }

    private async Task Cleanup(string title)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        await db.Incidents.Where(item => item.Title == title).ExecuteDeleteAsync();
    }
}