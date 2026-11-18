using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SecureLab.Api.Tests;

public sealed class CsrfRegressionTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    private sealed record TokenResponse(string Token);

    private static string Pwd =>
        Environment.GetEnvironmentVariable("SeedUsers__Password")
        ?? throw new InvalidOperationException("Set local SeedUsers__Password outside Git.");

    [Fact] // REG-CSRF-01
    public async Task PutIncident_WithoutOrInvalidToken_IsRejectedAndUnchanged_ThenValidPairSucceeds()
    {
        using var alice = await factory.LoginAsync("alice", Pwd);

        // Створюємо ізольований тестовий інцидент із валідним токеном
        var tokenInit = await alice.GetFromJsonAsync<TokenResponse>("/api/auth/csrf-token");
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/incidents")
        {
            Content = JsonContent.Create(new
            {
                title = $"CSRF-Initial-{Guid.NewGuid():N}",
                description = "Початковий опис до перевірки CSRF.",
                severity = "Low",
                occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
            })
        };
        createReq.Headers.Add("X-CSRF-TOKEN", tokenInit!.Token);
        using var createdResp = await alice.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createdResp.StatusCode);

        using var createdDoc = JsonDocument.Parse(await createdResp.Content.ReadAsStringAsync());
        var id = createdDoc.RootElement.GetProperty("id").GetGuid();

        try
        {
            var before = await alice.GetFromJsonAsync<JsonObject>($"/api/incidents/{id}");
            var initialTitle = before!["title"]!.GetValue<string>();

            // 1. Негативна перевірка (без request token): очікуємо 400 і незмінний стан
            using var rejectedMissing = await alice.PutAsJsonAsync($"/api/incidents/{id}", new
            {
                title = "CSRF-Hacked-Title",
                description = "Має бути відхилено."
            });
            Assert.Equal(HttpStatusCode.BadRequest, rejectedMissing.StatusCode);

            var afterMissing = await alice.GetFromJsonAsync<JsonObject>($"/api/incidents/{id}");
            Assert.Equal(initialTitle, afterMissing!["title"]!.GetValue<string>());

            // 2. Негативна перевірка (невалідний request token): очікуємо 400 і незмінний стан
            using var invalidReq = new HttpRequestMessage(HttpMethod.Put, $"/api/incidents/{id}")
            {
                Content = JsonContent.Create(new
                {
                    title = "CSRF-Invalid-Token-Title",
                    description = "Має бути відхилено."
                })
            };
            invalidReq.Headers.Add("X-CSRF-TOKEN", "invalid-synthetic-token");
            using var rejectedInvalid = await alice.SendAsync(invalidReq);
            Assert.Equal(HttpStatusCode.BadRequest, rejectedInvalid.StatusCode);

            var afterInvalid = await alice.GetFromJsonAsync<JsonObject>($"/api/incidents/{id}");
            Assert.Equal(initialTitle, afterInvalid!["title"]!.GetValue<string>());

            // 3. Позитивна перевірка (валідна пара cookie + request token): очікуємо 204 і зміну стану
            var validToken = await alice.GetFromJsonAsync<TokenResponse>("/api/auth/csrf-token");
            using var validReq = new HttpRequestMessage(HttpMethod.Put, $"/api/incidents/{id}")
            {
                Content = JsonContent.Create(new
                {
                    title = "CSRF-Updated-Valid-Title",
                    description = "Легітимне оновлення з валідним токеном."
                })
            };
            validReq.Headers.Add("X-CSRF-TOKEN", validToken!.Token);
            using var accepted = await alice.SendAsync(validReq);
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);

            var afterAccepted = await alice.GetFromJsonAsync<JsonObject>($"/api/incidents/{id}");
            Assert.Equal("CSRF-Updated-Valid-Title", afterAccepted!["title"]!.GetValue<string>());
        }
        finally
        {
            // Очищення тестового запису
            var cleanupToken = await alice.GetFromJsonAsync<TokenResponse>("/api/auth/csrf-token");
            using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/incidents/{id}");
            delReq.Headers.Add("X-CSRF-TOKEN", cleanupToken!.Token);
            using var _ = await alice.SendAsync(delReq);
        }
    }
}