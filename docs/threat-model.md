# Модель загроз SecureLab, ЛР 6

Рівень: добрий («Добре»). Baseline 2-A, ресурс Incident.

| Елемент | Що зафіксувати | Ваш запис |
|---|---|---|
| Версія та середовище | review-start, профіль, seed/reset, SDK | review-start commit: `c0e69185123f67ebead71bad6f77ee8de72ff6bf`; гілка `lab/6-hardening-review` (від тегу `v0.5.0`); SDK `10.0.302`; локальний профіль `http` (Development, `http://localhost:5080`); production-like запуск без launch profile (`--no-launch-profile -c Release`); reset лише в Development (`--reset-database`); лише тестові акаунти зі штучними даними |
| Компоненти в межах огляду | клієнт, API, БД, конфігурація, журнали | Браузерний клієнт (`src/SecureLab.Api/Client`: `index.html`, `auth-lab.html`, `browser-lab.html`, `app.js`, `auth-lab.js`, `browser-lab.js`, `csrf-helper.js`); ASP.NET Core API (minimal API: `Program.cs`, `Presentation/Endpoints/IncidentEndpoints.cs`, `Scaffolding/Lab02Endpoints.cs`, `Scaffolding/Lab03Identity.cs`, `Security/AntiforgeryFilter.cs`); PostgreSQL у Docker Compose (`127.0.0.1:54329`); конфігурація (`appsettings.json`, `appsettings.Development.json`, `infra/compose.yaml`, `infra/.env.example`); консольні журнали; тести (`SecureLab.Api.Tests`). Redis не використовується |
| Активи | облікові дані, записи, переходи, аудит, секрети | Облікові дані користувачів (хеші паролів ASP.NET Core Identity, authentication cookie `.AspNetCore.Identity.Application` та antiforgery cookie `SecureLab.Antiforgery`); інциденти з власником `OwnerUserId` (`Title`, `Description`, `Severity`, `Status`); привілейовані дії: зміна/видалення інциденту, перехід статусу `triage` (`New → Triaged`); журнали подій автентифікації (`AuthEvents`) та аудиту відмов (`authorization.denied`); connection string і `SeedUsers__Password` (лише поза Git) |
| Суб'єкти | ролі | Анонімний відвідувач; автентифікований користувач із роллю `Reporter` (власник записів: `alice`, `bob`, `DbSeeder.ReporterRole`); автентифікований аналітик із роллю `Analyst` (`morgan`, `DbSeeder.AnalystRole`); локальний розробник; зловмисна сторінка в тому ж браузері (CSRF) |
| Entry points | маршрути, форми, конфігурація | Читання: `GET /api/incidents/search` (анонімний, з rate limiter `"search"`), `GET /api/incidents`, `GET /api/incidents/{id}`, `GET /api/incidents/severity-summary`, `GET /api/incident-comments/{id}`, `GET /api/me`, `GET /api/auth/session-check`, `GET /api/auth/csrf-token`, `GET /health`. Змінні: `POST /api/incidents`, `PUT /api/incidents/{id}`, `DELETE /api/incidents/{id}`, `POST /api/incidents/{id}/triage`, `POST /api/auth/login`, `POST /api/auth/register`, `POST /api/auth/logout`. Форми й `fetch` у `auth-lab.js`, `browser-lab.js`, `csrf-helper.js`; завантаження конфігурації; restore NuGet-пакетів; консольні журнали |
| Межі довіри | | `browser → API` (текст, заголовки та cookie користувача недовірені); `API → PostgreSQL` (EF Core / Npgsql, параметризовані запити); `repository → build` (tracked files, NuGet audit); `environment → configuration` (`Development` vs `Production`) |
| Наявні controls | з посиланням | Validation: `Lab02Endpoints.cs` (`POST /api/incidents`, тест `Lab02ContractTests.Post_WithSeverity7_Returns400WithSeverityKey`); SQLi: параметризований пошук, `EscapeLike` + `ILike` (`Lab02Endpoints.cs`); Authentication: Identity cookie, `HttpOnly`, `SameSite=Lax`, `Secure=Always` поза Development (`Lab03Identity.cs`; `AuthSecurityTests.CookieFlags_LoginOverHttps_SetsHttpOnlySecureLaxSessionCookie`); Authorization/IDOR: `IncidentAuthorizationHandler` (`IncidentAuthorizationTests` `A-01`..`A-12`, `IncidentOwnerTests` `A-04`..`A-05`); XSS: безпечний вивід через `createSafeTextElement` у `browser-lab.js` (`REG-XSS-01`); CORS: політика `Lab05BrowserProbe` лише в `Development`, origin `http://localhost:4173`, метод `GET` (`REG-CORS-01`); Заголовки: `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, CSP `default-src 'self'; frame-ancestors 'none'` через `OnStarting` (`Program.cs`); Rate limit: політика `"login"` (10 запитів / 10 с за IP) та політика `"search"` (3 запити / 10 с за `NameIdentifier`/IP); Безпечні помилки: `AddProblemDetails` + `UseExceptionHandler`; Журнали: `AuthEvents` та `authorization.denied` без паролів, із `traceId`. Antiforgery: `AntiforgeryFilter` + `GET /api/auth/csrf-token` + `csrf-helper.js` (`REG-CSRF-01`) |
| Виключення | що не перевірялося | HTTPS/TLS і HSTS (лише HTTP `localhost:5080`); історія Git, `git stash`, інші клони, зовнішній CI; зовнішній penetration test; production host; Redis і розподілений rate limit; `AllowedHosts: "*"` у `appsettings.json` (не обмежується на локальному стенді) |

## Схема меж довіри

```text
browser client (недовірений ввід, автоматична auth cookie)
   │  HTTP (локально, без TLS)
   ▼
ASP.NET Core API (validation, authn/authz, antiforgery, rate limit, logging)
   │  connection string / EF Core
   ▼
PostgreSQL (Docker Compose, 127.0.0.1:54329)

repository + NuGet ──► build/restore (dotnet package list --vulnerable)
environment variables ──► запуск API (Development / Production)