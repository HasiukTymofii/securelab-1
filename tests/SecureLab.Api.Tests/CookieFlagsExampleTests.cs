using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SecureLab.Api.Tests;

public sealed class CookieFlagsExampleTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    [Fact]
    public async Task Login_OverHttps_SetsHttpOnlyCookie()
    {
        var password = Environment.GetEnvironmentVariable("SeedUsers__Password")
            ?? throw new InvalidOperationException(
            "Set local SeedUsers__Password outside Git.");
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = "alice", password });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        const string cookieName = ".AspNetCore.Identity.Application=";
        var matching = response.Headers.GetValues("Set-Cookie")
            .Where(value => value.StartsWith(cookieName)).ToArray();
        // Assert лише за кількістю: значення cookie не потрапить у failure.
        Assert.True(matching.Length == 1);
        var setCookie = matching[0];
        // Перша частина — «назва=значення»; її не читаємо й не друкуємо.
        var attributes = setCookie.Split(';').Skip(1)
            .Select(part => part.Trim().ToLowerInvariant())
            .ToArray();
        Assert.Contains("httponly", attributes);
                Assert.Contains("samesite=lax", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("path=/", attributes);
    }
}
