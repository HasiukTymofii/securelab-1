using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;

namespace SecureLab.Api.Scaffolding;

public static class Lab03Identity
{
    private static readonly Regex UserNameRx = new("^[A-Za-z0-9._@+-]+$");

    public static void AddLab03Identity(this WebApplicationBuilder builder)
    {
        builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 10;
        }).AddEntityFrameworkStores<SecureLabDbContext>().AddDefaultTokenProviders();
        builder.Services.AddAuthorization();
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
    }

    public static void MapLab03Identity(this WebApplication app)
    {
        app.MapPost("/api/auth/register", async (RegistrationInput input, UserManager<ApplicationUser> users) =>
        {
            var userName = input.UserName?.Trim();
            var email = input.Email?.Trim();
            var displayName = input.DisplayName?.Trim();

            if (!IsRegisterInputValid(userName, email, displayName, input.Password))
                return Results.BadRequest(new { message = "Registration failed" });

            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(), UserName = userName, Email = email,
                DisplayName = displayName!
            };
            var result = await users.CreateAsync(user, input.Password!);
            if (!result.Succeeded)
            {
                var codes = result.Errors.Select(error => error.Code).ToArray();
                if (codes.Contains("DuplicateUserName") || codes.Contains("DuplicateEmail"))
                    return Results.Conflict(new { message = "Registration failed" });
                return Results.BadRequest(new { message = "Registration failed" });
            }
            var role = await users.AddToRoleAsync(user, DbSeeder.ReporterRole);
            if (!role.Succeeded) throw new InvalidOperationException("Default role assignment failed.");
            return Results.Created("/api/me", new RegisterResponse(user.Id, user.UserName!, user.DisplayName));
        });
        app.MapPost("/api/auth/login", async (LoginInput input, SignInManager<ApplicationUser> signIn) =>
        {
            if (string.IsNullOrWhiteSpace(input.UserName) || string.IsNullOrEmpty(input.Password))
                return Results.Unauthorized();
            var result = await signIn.PasswordSignInAsync(input.UserName, input.Password, false, false);
            return result.Succeeded ? Results.NoContent() : Results.Unauthorized();
        });
        app.MapPost("/api/auth/logout", async (SignInManager<ApplicationUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        });
        app.MapGet("/api/me", GetCurrentUser).RequireAuthorization();
        // Приклад лише mechanics: не повертає identity і не реалізує оцінювану operation.
        app.MapGet("/api/auth/session-check", () => Results.NoContent()).RequireAuthorization();
    }

    private static bool IsRegisterInputValid(string? userName, string? email, string? displayName, string? password) =>
        !string.IsNullOrWhiteSpace(userName) && userName.Length <= 256 && UserNameRx.IsMatch(userName)
        && !string.IsNullOrWhiteSpace(email) && email.Length <= 256
        && !string.IsNullOrWhiteSpace(displayName) && displayName.Length <= 120
        && !string.IsNullOrEmpty(password);

    private static async Task<IResult> GetCurrentUser(HttpContext httpContext, UserManager<ApplicationUser> users)
    {
        var idClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(idClaim, out var verifiedUserId))
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);

        var user = await users.FindByIdAsync(verifiedUserId.ToString());
        if (user is null)
            return Results.Unauthorized();

        return Results.Ok(new { id = user.Id, userName = user.UserName, displayName = user.DisplayName });
    }
}

public sealed record RegistrationInput(string? UserName, string? Email, string? DisplayName, string? Password);
public sealed record RegisterResponse(Guid Id, string UserName, string DisplayName);
public sealed record LoginInput(string? UserName, string? Password);