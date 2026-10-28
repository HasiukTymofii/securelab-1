using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using SecureLab.Api.Data;

namespace SecureLab.Api.Tests;

public sealed class IncidentAuthorizationTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    private static readonly Guid AliceInc = DbSeeder.AliceIncidentId;
    private static readonly Guid BobInc = DbSeeder.BobIncidentId;

   
    private static readonly Dictionary<(SecureLabApiFactory, string), HttpClient> Clients = new();

    private async Task<HttpClient> As(string userName)
    {
        if (!Clients.TryGetValue((factory, userName), out var client))
        {
            var password = Environment.GetEnvironmentVariable("SeedUsers__Password")
                ?? throw new InvalidOperationException("Set local SeedUsers__Password outside Git.");
            client = await factory.LoginAsync(userName, password);
            Clients[(factory, userName)] = client;
        }
        return client;
    }

    private static async Task<HttpStatusCode> StatusOf(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }

    private static Task<JsonObject?> GetJson(HttpClient client, Guid id) =>
        client.GetFromJsonAsync<JsonObject>($"/api/incidents/{id}");

   
    private static async Task<Guid> CreateIncidentAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/incidents", new
        {
            title = "LR4 temporary incident",
            description = "temporary",
            severity = "Low",
            occurredAtUtc = DateTimeOffset.UtcNow
        });
        Assert.True(response.IsSuccessStatusCode, $"Create failed: {(int)response.StatusCode}");

        var text = await response.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("id", out var idElement))
            {
                return idElement.GetGuid();
            }
        }

        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        return Guid.Parse(location!.TrimEnd('/').Split('/').Last());
    }

    [Fact] // A-01
    public async Task Details_Anonymous_Returns401_NotRedirect()
    {
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await anonymous.GetAsync($"/api/incidents/{AliceInc}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact] // A-02
    public async Task Details_AsOwner_ReturnsOwnIncident()
    {
        var alice = await As("alice");

        var json = await GetJson(alice, AliceInc);

        Assert.Equal(AliceInc.ToString(), json!["id"]!.GetValue<string>());
    }

    [Fact] // A-03
    public async Task Details_AsOtherReader_Returns404_WithoutBobData()
    {
        var alice = await As("alice");

        using var response = await alice.GetAsync($"/api/incidents/{BobInc}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Мельник", await response.Content.ReadAsStringAsync());
    }

    [Fact] // A-03R
    public async Task Details_AsAnalyst_ReturnsForeignIncident()
    {
        var morgan = await As("morgan");

        Assert.Equal(HttpStatusCode.OK, await StatusOf(morgan.GetAsync($"/api/incidents/{AliceInc}")));
    }

    [Fact] // A-04
    public async Task UpdateAndDelete_AsOtherReader_Return404_AndDoNotChangeIncident()
    {
        var alice = await As("alice");
        var bob = await As("bob");
        var before = await GetJson(bob, BobInc);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(alice.PutAsJsonAsync(
            $"/api/incidents/{BobInc}", new { title = "hacked", description = "hacked" })));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(alice.DeleteAsync($"/api/incidents/{BobInc}")));

        var after = await GetJson(bob, BobInc);
        Assert.True(JsonNode.DeepEquals(before, after));
    }

    [Fact] // A-05
    public async Task List_AsReporter_ContainsOnlyOwnIncidents()
    {
        var alice = await As("alice");

        var list = await alice.GetFromJsonAsync<JsonArray>("/api/incidents");
        var ids = list!.Select(item => item!["id"]!.GetValue<string>()).ToList();

        Assert.Contains(AliceInc.ToString(), ids);
        Assert.DoesNotContain(BobInc.ToString(), ids);
    }

    [Fact] // A-06
    public async Task Triage_AsReporterOwner_Returns403_AndStatusStaysNew()
    {
        var alice = await As("alice");
        var id = await CreateIncidentAsync(alice);
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden,
                await StatusOf(alice.PostAsync($"/api/incidents/{id}/triage", null)));
            Assert.Equal("New", (await GetJson(alice, id))!["status"]!.GetValue<string>());
        }
        finally
        {
            using var cleanup = await alice.DeleteAsync($"/api/incidents/{id}");
        }
    }

    [Fact] // A-07
    public async Task Triage_AsAnalyst_MovesNewToTriaged()
    {
        var alice = await As("alice");
        var morgan = await As("morgan");
        var id = await CreateIncidentAsync(alice);
        try
        {
            Assert.Equal(HttpStatusCode.NoContent,
                await StatusOf(morgan.PostAsync($"/api/incidents/{id}/triage", null)));
            Assert.Equal("Triaged", (await GetJson(alice, id))!["status"]!.GetValue<string>());
        }
        finally
        {
            using var cleanup = await alice.DeleteAsync($"/api/incidents/{id}");
        }
    }

    [Fact] // A-08a
    public async Task Update_WithForgedFields_IgnoresOwnerRoleAndStatus()
    {
        var alice = await As("alice");
        var bob = await As("bob");
        var id = await CreateIncidentAsync(alice);
        try
        {
            var before = await GetJson(alice, id);

            Assert.Equal(HttpStatusCode.NoContent, await StatusOf(alice.PutAsJsonAsync(
                $"/api/incidents/{id}",
                new
                {
                    title = "t2",
                    description = "d2",
                    ownerUserId = "10000000-0000-0000-0000-000000000002",
                    role = "Analyst",
                    status = "Triaged"
                })));

            var after = await GetJson(alice, id);
            Assert.Equal("t2", after!["title"]!.GetValue<string>());      
            Assert.Equal("New", after["status"]!.GetValue<string>());     
            Assert.Equal(before!["ownerDisplayName"]!.GetValue<string>(),
                after["ownerDisplayName"]!.GetValue<string>());           
            Assert.Equal(HttpStatusCode.NotFound, await StatusOf(bob.GetAsync($"/api/incidents/{id}")));
        }
        finally
        {
            using var cleanup = await alice.DeleteAsync($"/api/incidents/{id}");
        }
    }

    [Fact] // A-10 (лише політика 404)
    public async Task ForeignAndUnknownId_AreIndistinguishable()
    {
        var alice = await As("alice");

        using var foreign = await alice.GetAsync($"/api/incidents/{BobInc}");
        using var unknown = await alice.GetAsync($"/api/incidents/{Guid.NewGuid()}");

        Assert.Equal(unknown.StatusCode, foreign.StatusCode);
        Assert.Equal(unknown.Content.Headers.ContentType?.ToString(),
            foreign.Content.Headers.ContentType?.ToString());
        var foreignBody = (await foreign.Content.ReadFromJsonAsync<JsonObject>())!;
        var unknownBody = (await unknown.Content.ReadFromJsonAsync<JsonObject>())!;
        foreignBody.Remove("traceId");  
        unknownBody.Remove("traceId");
        Assert.True(JsonNode.DeepEquals(unknownBody, foreignBody));
    }

    [Fact] // A-12
    public async Task ChildComment_DoesNotLeakToReporter()
    {
        var alice = await As("alice");
        var morgan = await As("morgan");

        Assert.Equal(HttpStatusCode.NotFound,
            await StatusOf(alice.GetAsync($"/api/incident-comments/{DbSeeder.Comment2Id}")));
        Assert.Equal(HttpStatusCode.NotFound,
            await StatusOf(alice.GetAsync($"/api/incident-comments/{DbSeeder.InternalCommentId}")));
        Assert.Equal(HttpStatusCode.OK,
            await StatusOf(morgan.GetAsync($"/api/incident-comments/{DbSeeder.InternalCommentId}")));

        var aliceIds = (await GetJson(alice, AliceInc))!["comments"]!.AsArray()
            .Select(c => c!["id"]!.GetValue<string>());
        Assert.DoesNotContain(DbSeeder.InternalCommentId.ToString(), aliceIds);

        var morganIds = (await GetJson(morgan, AliceInc))!["comments"]!.AsArray()
            .Select(c => c!["id"]!.GetValue<string>());
        Assert.Contains(DbSeeder.InternalCommentId.ToString(), morganIds);
    }
}