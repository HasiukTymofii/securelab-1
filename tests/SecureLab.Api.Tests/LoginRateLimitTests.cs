using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SecureLab.Api.Tests;

public sealed class LoginRateLimitTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    private static string Pwd =>
        Environment.GetEnvironmentVariable("SeedUsers__Password")
        ?? throw new InvalidOperationException("Set local SeedUsers__Password outside Git.");

    [Fact]
    public async Task Login_TooManyAttempts_Returns429_ThenRecovers()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        using var client = host.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var i = 0; i < 10; i++)
        {
            using var attempt = await client.PostAsJsonAsync("/api/auth/login",
                new { userName = "alice", password = Pwd + "x" });
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        using var limited = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "alice", password = Pwd + "x" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        using var limitedUnknown = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "nobody" + Guid.NewGuid().ToString("N")[..8], password = Pwd + "x" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedUnknown.StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(10.5));
        using var recovered = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "alice", password = Pwd });
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
    }
}