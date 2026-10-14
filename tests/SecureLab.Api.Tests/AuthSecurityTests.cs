using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;

namespace SecureLab.Api.Tests;

internal static class AuthTestSupport
{
    public static string StudyPassword =>
        Environment.GetEnvironmentVariable("SeedUsers__Password")
        ?? throw new InvalidOperationException("Set local SeedUsers__Password outside Git.");

    public static WebApplicationFactoryClientOptions HttpsOptions() => new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    };

    public static HttpClient Anonymous<T>(WebApplicationFactory<T> host) where T : class =>
        host.CreateClient(HttpsOptions());

    
    public static async Task<HttpClient> SignInAsync<T>(WebApplicationFactory<T> host, string userName)
        where T : class
    {
        var client = Anonymous(host);
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName, password = StudyPassword });
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"Test login failed with status {(int)response.StatusCode} (credentials omitted).");
        }
        return client;
    }

    public static string UniqueTitle() => "auth-test-" + Guid.NewGuid().ToString("N")[..8];

    public static object NewIncident(string title) => new
    {
        title,
        description = "Перевірка безпеки: створення інциденту в ЛР3.",
        severity = "Low",
        occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
    };

    public static object NewIncidentWithOwner(string title, Guid ownerUserId) => new
    {
        title,
        description = "Перевірка безпеки: створення інциденту в ЛР3.",
        severity = "Low",
        occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
        ownerUserId
    };

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    // Фактичний власник запису читається з БД, бо API його назовні не віддає.
    public static async Task<Guid> OwnerOfAsync<T>(WebApplicationFactory<T> host, Guid incidentId)
        where T : class
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        var incident = await db.Incidents.AsNoTracking().SingleAsync(item => item.Id == incidentId);
        return incident.OwnerUserId;
    }

    public static async Task<bool> IncidentExistsAsync<T>(WebApplicationFactory<T> host, string title)
        where T : class
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        return await db.Incidents.AsNoTracking().AnyAsync(item => item.Title == title);
    }
}

public sealed class AuthSecurityTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    

    private static (string UserName, string Email) NewIdentity()
    {
        var userName = "reg" + Guid.NewGuid().ToString("N")[..8];
        return (userName, $"{userName}@example.test");
    }

    private static object RegisterBody(string userName, string email) => new
    {
        userName,
        email,
        displayName = "Тест Реєстрації",
        password = AuthTestSupport.StudyPassword
    };

    [Fact]
    public async Task T01_Register_ReturnsSafeDto_AndNewUserCanLogin()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        using var anonymous = AuthTestSupport.Anonymous(host);
        var (userName, email) = NewIdentity();

        using var response = await anonymous.PostAsJsonAsync("/api/auth/register", RegisterBody(userName, email));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
   
        Assert.False(body.Contains(AuthTestSupport.StudyPassword, StringComparison.Ordinal));

        using var doc = JsonDocument.Parse(body);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "displayName", "id", "userName" }, names);
        var registeredId = doc.RootElement.GetProperty("id").GetGuid();

        using var signedIn = await AuthTestSupport.SignInAsync(host, userName);
        using var me = await signedIn.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meDoc = await AuthTestSupport.ReadJsonAsync(me);
        Assert.Equal(registeredId, meDoc.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task T02_Register_WeakPassword_Returns400_AndDoesNotCreateAccount()
    {
        using var anonymous = AuthTestSupport.Anonymous(factory);
        var (userName, email) = NewIdentity();

        using var response = await anonymous.PostAsJsonAsync("/api/auth/register", new
        {
            userName, email, displayName = "Тест Реєстрації", password = "short"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await users.FindByNameAsync(userName));
    }

    [Fact]
    public async Task Register_Duplicate_UserNameOrEmail_Returns409_Consistently()
    {
        using var anonymous = AuthTestSupport.Anonymous(factory);
        var (userName, email) = NewIdentity();
        var (otherName, otherEmail) = NewIdentity();

        using (var first = await anonymous.PostAsJsonAsync("/api/auth/register", RegisterBody(userName, email)))
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var sameBoth = await anonymous.PostAsJsonAsync("/api/auth/register", RegisterBody(userName, email));
        using var sameName = await anonymous.PostAsJsonAsync("/api/auth/register", RegisterBody(userName, otherEmail));
        using var sameEmail = await anonymous.PostAsJsonAsync("/api/auth/register", RegisterBody(otherName, email));

        Assert.Equal(HttpStatusCode.Conflict, sameBoth.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sameName.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sameEmail.StatusCode);

        // Однакове безпечне тіло для всіх трьох варіантів.
        var bodies = new[]
        {
            await sameBoth.Content.ReadAsStringAsync(),
            await sameName.Content.ReadAsStringAsync(),
            await sameEmail.Content.ReadAsStringAsync()
        };
        Assert.Single(bodies.Distinct());
        Assert.False(bodies[0].Contains(AuthTestSupport.StudyPassword, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Register_IgnoresRoleAndIdFromClient()
    {
        using var anonymous = AuthTestSupport.Anonymous(factory);
        var (userName, email) = NewIdentity();
        var forgedId = Guid.NewGuid();

        using var response = await anonymous.PostAsJsonAsync("/api/auth/register", new
        {
            userName, email, displayName = "Тест Реєстрації",
            password = AuthTestSupport.StudyPassword,
            role = "Administrator", id = forgedId
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = await AuthTestSupport.ReadJsonAsync(response);
        var createdId = doc.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(forgedId, createdId);

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(createdId.ToString());
        Assert.NotNull(user);
        var roles = await users.GetRolesAsync(user!);
        Assert.Equal(new[] { DbSeeder.ReporterRole }, roles.ToArray());
    }

  

    [Fact]
    public async Task A01_AnonymousCreate_Returns401_NotRedirect_AndNothingCreated()
    {
        using var anonymous = AuthTestSupport.Anonymous(factory);
        var title = AuthTestSupport.UniqueTitle();

        using var response = await anonymous.PostAsJsonAsync("/api/incidents", AuthTestSupport.NewIncident(title));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        
        Assert.False(await AuthTestSupport.IncidentExistsAsync(factory, title));
    }

    [Fact]
    public async Task A01_AnonymousMe_Returns401()
    {
        using var anonymous = AuthTestSupport.Anonymous(factory);
        using var response = await anonymous.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    

    [Fact]
    public async Task A02_LoginThenMe_ReturnsCurrentPrincipal_WithSafeFieldsOnly()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        using var alice = await AuthTestSupport.SignInAsync(host, "alice");

        using var response = await alice.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = await AuthTestSupport.ReadJsonAsync(response);
        Assert.Equal(DbSeeder.AliceId, doc.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("alice", doc.RootElement.GetProperty("userName").GetString());

        var names = doc.RootElement.EnumerateObject().Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "displayName", "id", "userName" }, names);
    }

    

    [Fact]
    public async Task A03_MeWithForeignDemoHeader_StillReturnsCookieUser()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        using var alice = await AuthTestSupport.SignInAsync(host, "alice");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("X-Demo-UserId", DbSeeder.BobId.ToString());
        using var response = await alice.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await AuthTestSupport.ReadJsonAsync(response);
        Assert.Equal(DbSeeder.AliceId, doc.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A03_DemoHeaderWithoutCookie_Returns401()
    {
        using var anonymous = AuthTestSupport.Anonymous(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("X-Demo-UserId", DbSeeder.BobId.ToString());
        using var response = await anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

   

    [Fact]
    public async Task A04_CreateWithoutOwner_UsesPrincipalAsOwner()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        using var bob = await AuthTestSupport.SignInAsync(host, "bob");
        var title = AuthTestSupport.UniqueTitle();

        using var response = await bob.PostAsJsonAsync("/api/incidents", AuthTestSupport.NewIncident(title));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = await AuthTestSupport.ReadJsonAsync(response);
        var id = doc.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(DbSeeder.BobId, await AuthTestSupport.OwnerOfAsync(host, id));
    }

    [Fact]
    public async Task A05_CreateWithForgedOwnerUserId_KeepsPrincipalAsOwner()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        using var bob = await AuthTestSupport.SignInAsync(host, "bob");
        var title = AuthTestSupport.UniqueTitle();

        using var response = await bob.PostAsJsonAsync(
            "/api/incidents", AuthTestSupport.NewIncidentWithOwner(title, DbSeeder.AliceId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = await AuthTestSupport.ReadJsonAsync(response);
        var id = doc.RootElement.GetProperty("id").GetGuid();
        var owner = await AuthTestSupport.OwnerOfAsync(host, id);
        Assert.NotEqual(DbSeeder.AliceId, owner);
        Assert.Equal(DbSeeder.BobId, owner);
    }

    
    [Fact]
    public async Task A06_Logout_ThenSameClientGets401()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        using var alice = await AuthTestSupport.SignInAsync(host, "alice");

        using (var before = await alice.GetAsync("/api/me"))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var logout = await alice.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        // Значення основної cookie порожнє: сервер наказує браузеру її видалити.
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(".AspNetCore.Identity.Application=;", StringComparison.Ordinal));

        using var afterMe = await alice.GetAsync("/api/me");
        using var afterCheck = await alice.GetAsync("/api/auth/session-check");
        Assert.Equal(HttpStatusCode.Unauthorized, afterMe.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterCheck.StatusCode);
    }

   

    private static string StableProblemFields(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "<empty>";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "<non-object>";
            string Field(string name) =>
                doc.RootElement.TryGetProperty(name, out var value) ? value.GetRawText() : "<absent>";
            // traceId навмисно не порівнюється: він різний у кожного запиту.
            return string.Join("|", new[] { "type", "title", "status", "detail" }
                .Select(name => name + "=" + Field(name)));
        }
        catch (JsonException)
        {
            return "<non-json>";
        }
    }

    [Fact]
    public async Task A07_UnknownUserAndWrongPassword_GiveSameExternalResult()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        using var client = AuthTestSupport.Anonymous(host);
        var wrongPassword = AuthTestSupport.StudyPassword + "-wrong";

        using var unknown = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = "no-such-user-" + Guid.NewGuid().ToString("N")[..6], password = wrongPassword });
        using var wrong = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = "alice", password = wrongPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(unknown.Content.Headers.ContentType?.MediaType, wrong.Content.Headers.ContentType?.MediaType);
        Assert.Empty(unknown.Headers.WwwAuthenticate);
        Assert.Empty(wrong.Headers.WwwAuthenticate);
        Assert.Equal(
            StableProblemFields(await unknown.Content.ReadAsStringAsync()),
            StableProblemFields(await wrong.Content.ReadAsStringAsync()));
    }

   

    [Fact]
    public async Task CookieFlags_LoginOverHttps_SetsHttpOnlySecureLaxSessionCookie()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        using var client = AuthTestSupport.Anonymous(host);

        using var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = "alice", password = AuthTestSupport.StudyPassword });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        const string cookieName = ".AspNetCore.Identity.Application=";
        var matching = response.Headers.GetValues("Set-Cookie")
            .Where(value => value.StartsWith(cookieName, StringComparison.Ordinal)).ToArray();
        // Лише кількість: значення cookie не потрапить у failure.
        Assert.True(matching.Length == 1);

      
        var attributes = matching[0].Split(';').Skip(1)
            .Select(part => part.Trim().ToLowerInvariant()).ToArray();
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.Contains("path=/", attributes);
       
        Assert.DoesNotContain(attributes,
            a => a.StartsWith("expires=", StringComparison.Ordinal) || a.StartsWith("max-age=", StringComparison.Ordinal));
    }
}


public sealed class LoginRateLimitTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    // УВАГА: підставте реальне вікно з вашого Program.cs (політика "login").
    private static readonly TimeSpan LoginWindow = TimeSpan.FromSeconds(10);
    private const int MaxAttempts = 200;

    [Fact]
    public async Task Login_ExceedingLimit_Returns429_ThenRecoversAfterWindow()
    {
        using var client = factory.CreateClient(AuthTestSupport.HttpsOptions());
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < MaxAttempts; i++)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/auth/login", new { userName = "rate-limit-probe", password = "Wrong-Pass-1!" });
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) break;
        }

   
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.True(statuses.Count > 1);
        Assert.All(statuses.SkipLast(1), status => Assert.Equal(HttpStatusCode.Unauthorized, status));

      
        await Task.Delay(LoginWindow + TimeSpan.FromSeconds(2));
        using var recovered = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = "rate-limit-probe", password = "Wrong-Pass-1!" });
        Assert.Equal(HttpStatusCode.Unauthorized, recovered.StatusCode);
    }
}