using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SecureLab.Api.Data;

namespace SecureLab.Api.Tests;

public sealed class AuthScenarioTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    private static string Pwd =>
        Environment.GetEnvironmentVariable("SeedUsers__Password")
        ?? throw new InvalidOperationException("Set local SeedUsers__Password outside Git.");

    private HttpClient Anonymous() => factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Register_Valid_Returns201_WithoutSensitiveFields()
    {
        using var c = Anonymous();
        var name = "reg" + Guid.NewGuid().ToString("N")[..8];
        using var r = await c.PostAsJsonAsync("/api/auth/register",
            new { userName = name, email = name + "@example.test", displayName = "Test user", password = Pwd });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        foreach (var bad in new[] { "password", "passwordHash", "securityStamp", "token", "cookie" })
            Assert.False(doc.RootElement.TryGetProperty(bad, out _));
    }

    [Fact]
    public async Task Register_Duplicate_Returns409()
    {
        using var c = Anonymous();
        var name = "reg" + Guid.NewGuid().ToString("N")[..8];
        var body = new { userName = name, email = name + "@example.test", displayName = "Test user", password = Pwd };
        using var first = await c.PostAsJsonAsync("/api/auth/register", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var second = await c.PostAsJsonAsync("/api/auth/register", body);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Register_TooLongDisplayName_Returns400()
    {
        using var c = Anonymous();
        var name = "reg" + Guid.NewGuid().ToString("N")[..8];
        using var r = await c.PostAsJsonAsync("/api/auth/register",
            new { userName = name, email = name + "@example.test", displayName = new string('x', 121), password = Pwd });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task A01_Anonymous_Me_Returns401_NotRedirect()
    {
        using var c = Anonymous();
        using var r = await c.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Null(r.Headers.Location);
        Assert.Empty(r.Headers.WwwAuthenticate);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A02_Login_Me_ReturnsCurrentUser()
    {
        using var alice = await factory.LoginAsync("alice", Pwd);
        using var r = await alice.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal(DbSeeder.AliceId, doc.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("alice", doc.RootElement.GetProperty("userName").GetString());
        Assert.False(doc.RootElement.TryGetProperty("passwordHash", out _));
    }

    [Fact]
    public async Task A03_ForeignDemoHeader_DoesNotChangeCurrentUser()
    {
        using var alice = await factory.LoginAsync("alice", Pwd);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        req.Headers.Add("X-Demo-UserId", DbSeeder.BobId.ToString());
        using var r = await alice.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal(DbSeeder.AliceId, doc.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A06_Logout_ThenMe_Returns401()
    {
        using var alice = await factory.LoginAsync("alice", Pwd);
        using (var before = await alice.GetAsync("/api/me"))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using (var logout = await alice.PostAsync("/api/auth/logout", null))
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var after = await alice.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    private static async Task<(HttpStatusCode, string?, string[])> LoginShape(HttpClient c, string user, string pwd)
    {
        using var r = await c.PostAsJsonAsync("/api/auth/login", new { userName = user, password = pwd });
        var text = await r.Content.ReadAsStringAsync();
        var fields = new List<string>();
        if (text.Length > 0 && r.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var n in new[] { "type", "title", "status", "detail" })
                fields.Add(n + "=" + (doc.RootElement.TryGetProperty(n, out var v) ? v.ToString() : "<none>"));
        }
        return (r.StatusCode, r.Content.Headers.ContentType?.MediaType, fields.ToArray());
    }

    [Fact]
    public async Task A07_UnknownUser_And_WrongPassword_LookTheSame()
    {
        using var c = Anonymous();
        var unknown = await LoginShape(c, "nobody" + Guid.NewGuid().ToString("N")[..8], Pwd);
        var wrong = await LoginShape(c, "alice", Pwd + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Item1);
        Assert.Equal(unknown.Item1, wrong.Item1);
        Assert.Equal(unknown.Item2, wrong.Item2);
        Assert.Equal(unknown.Item3, wrong.Item3);
    }
}