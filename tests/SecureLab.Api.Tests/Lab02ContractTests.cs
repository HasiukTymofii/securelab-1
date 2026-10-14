using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureLab.Api.Data;

namespace SecureLab.Api.Tests;

public sealed class Lab02ContractTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    private static string Pwd =>
        Environment.GetEnvironmentVariable("SeedUsers__Password")
        ?? throw new InvalidOperationException("Set local SeedUsers__Password outside Git.");

    [Fact]
    public async Task Post_WithSeverity7_Returns400WithSeverityKey()
    {
        var title = $"T02-{Guid.NewGuid():N}";
        try
        {
            using var client = await factory.LoginAsync("alice", Pwd);
            using var response = await client.PostAsJsonAsync("/api/incidents", new
            {
                title,
                description = "Штучний опис для перевірки.",
                severity = "7",
                occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json",
                response.Content.Headers.ContentType?.MediaType);

            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var errors = document.RootElement.GetProperty("errors");
            Assert.True(errors.TryGetProperty("severity", out _));
            Assert.DoesNotContain("Exception", json, StringComparison.Ordinal);
        }
        finally
        {
            await Cleanup(title);
        }
    }

    [Fact]
    public async Task Post_DuplicateActiveTitle_Returns409()
    {
        var title = $"T03-{Guid.NewGuid():N}";
        var body = new
        {
            title,
            description = "Штучний опис для перевірки.",
            severity = "Low",
            occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
        };

        try
        {
            using var client = await factory.LoginAsync("alice", Pwd);
            using var first = await client.PostAsJsonAsync("/api/incidents", body);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);

            using var second = await client.PostAsJsonAsync("/api/incidents", body);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("application/problem+json",
                second.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            await Cleanup(title);
        }
    }

    private async Task Cleanup(string title)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        await db.Incidents.Where(item => item.Title == title).ExecuteDeleteAsync();
    }
}