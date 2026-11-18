# Security review SecureLab, ЛР 6

Рівень: добрий («Добре»); рівні накопичувальні.  
Commit початку огляду (review-start): `c0e69185123f67ebead71bad6f77ee8de72ff6bf` (гілка `lab/6-hardening-review` від тегу `v0.5.0`).  
Хеші прийнятого merge commit і тегу `v1.0.0` додає скрипт експорту в PDF.  
Дата: 17–18 листопада 2026. Версія SDK: `.NET 10.0.302`. Профіль запуску: `Development` (`http://localhost:5080`) та `Production` (`--no-launch-profile -c Release`). Клієнт: браузер Chrome/Edge (DevTools Network/Application), VS Code REST Client (`.http`) та інтеграційні тести `xUnit` (`SecureLabApiFactory` / `CookieClient`).

## 1. Executive summary

Перевірено застосунок SecureLab (baseline 2-A) починаючи зі стану `v0.5.0` (review-start commit `c0e69185123f67ebead71bad6f77ee8de72ff6bf`) у локальному середовищі (ASP.NET Core API + статичний браузерний клієнт + PostgreSQL у Docker Compose). У межах огляду перевірено конфігурацію та відстежувані файли Git, поведінку `Production`-профілю й обробку неочікуваних помилок, HTTP-заголовки безпеки, CORS, периметр cookie-authenticated змінювальних маршрутів, обмеження частоти запитів (rate limiting), залежності NuGet та журнали подій. Зафіксовано та виправлено дві власні знахідки: `F-01` (втрата заголовків безпеки на відповідях `500 Internal Server Error`, P2) та `F-02` (відсутність antiforgery-валідації на browser-accessible змінювальних маршрутах з cookie-автентифікацією, P1). Фінальний регресійний набір рівня «Добре» виконано після всіх змін (`Passed: 47, Failed: 0`). Головні залишкові ризики: відсутність реального TLS-термінування та HSTS на локальному HTTP-стенді, можливий доступ до request token у разі наявності XSS на власному origin, а також відсутність antiforgery-захисту на анонімних ендпоінтах входу та реєстрації (login-CSRF).

## 2. Scope та exclusions

- **У межах огляду:**
  - усі поточні відстежувані файли репозиторію (`git ls-files`) на гілці `lab/6-hardening-review`;
  - конфігураційні файли (`infra/.env.example`, `infra/compose.yaml`, `src/SecureLab.Api/appsettings.json`, `src/SecureLab.Api/appsettings.Development.json`) та правила `.gitignore`;
  - серверні маршрути ASP.NET Core API (`src/SecureLab.Api/Program.cs`, `Presentation/Endpoints/IncidentEndpoints.cs`, `Scaffolding/Lab02Endpoints.cs`, `Scaffolding/Lab03Identity.cs`, `Security/AntiforgeryFilter.cs`) і клієнтські скрипти (`src/SecureLab.Api/Client/*`);
  - прямі та транзитивні залежності NuGet (`SecureLab.Api`, `SecureLab.Api.Tests`);
  - консольні діагностичні логи та audit-події у локальному стенді.
- **Виключення (поза межами огляду):**
  - повна історія комітів Git, віддалені форки, `git stash` та зовнішні CI/CD-середовища — не перевірялися;
  - реальний production-хостинг, зовнішній reverse proxy, TLS-сертифікати та навантажувальне тестування.

## 3. System overview і trust boundaries

Детальну модель активів, точок входу та діаграму меж довіри наведено у `docs/threat-model.md`. Зв’язок меж довіри з фактичними тестами:

| Межа довіри | Очікувана властивість безпеки | Фактична перевірка / тест |
|---|---|---|
| `Browser Client → API` (недовірений ввід та автоматичні cookie) | Змінювальні запити з auth-cookie без валідного request token відхиляються (`400`) без зміни стану; текст не виконується як скрипт; дозволено лише явні origins | `REG-CSRF-01` (`CsrfRegressionTests`), `REG-XSS-01`, `REG-CORS-01` |
| `API → PostgreSQL` (запити до БД через EF Core) | Користувацькі параметри пошуку та фільтрації не змінюють структуру SQL-запиту; дотримується перевірка власника/ролі | `Lab02Endpoints.cs` (`EscapeLike` + `ILike`), `IncidentAuthorizationTests` (`A-*`), `IncidentOwnerTests` |
| `Repository → Build / Restore` | Відстежувані файли не містять реальних секретів; залежності перевіряються на відомі вразливості | `git grep` (94 збіги класифіковано), `dotnet package list --vulnerable --include-transitive` |
| `Environment → Configuration` | У `Production`-профілі вимкнено OpenAPI/Scalar та заборонено `--reset-database`; помилки не розкривають stack trace або connection string | Запуск `Production.ps1 -Mode normal` та `-Mode broken` (`Рис. 4–7`, `Рис. 9`) |

## 4. Methodology та test environment

- **Початковий стан:** чисте робоче дерево на тегу `v0.5.0` (`c0e69185123f67ebead71bad6f77ee8de72ff6bf`), 46/46 успішних тестів у `SecureLab.Api.Tests` (код завершення `0`).
- **Локальна БД:** PostgreSQL у Docker Compose (`infra/compose.yaml`, контейнер `secure-lab-postgres-1` у стані `Healthy`, прив’язка до `127.0.0.1:54329`, база даних `securelab`). Скидання та заповнення тестових даних виконувалося лише у `Development` через `dotnet run --project src/SecureLab.Api --reset-database` зі змінною середовища `SeedUsers__Password` поза Git.
- **Профілі та клієнти:**
  - `Development`: профіль `http` (`http://localhost:5080`), клієнт — браузер Chrome/Edge та `WebApplicationFactory<Program>` / `CookieClient`.
  - `Production`: запуск без `launchSettings.json` через `Production.ps1` (`--no-launch-profile -c Release`, `ASPNETCORE_ENVIRONMENT=Production`, `Logging__Console__IncludeScopes=true`).
- **Підтвердження активності Production-профілю та відсутності публічної документації API:**
  - У консолі зафіксовано `Hosting environment: Production` та `Now listening on: http://localhost:5080`.
  - Команда `dotnet run --project src/SecureLab.Api --no-launch-profile -c Release --reset-database` завершилася очікуваною відмовою: `System.InvalidOperationException: Reset даних дозволено лише в явно налаштованому Development environment.`
  - Запит `GET http://localhost:5080/openapi/v1.json` повернув `404 Not Found` (`application/problem+json`), а `GET http://localhost:5080/scalar/v1` повернув `200 OK` з `Content-Type: text/html` (спрацював SPA fallback `index.html`, інтерактивна документація Scalar недоступна):

```http
HTTP/1.1 404 Not Found
Content-Type: application/problem+json
Date: Tue, 17 Nov 2026 21:33:20 GMT
Server: Kestrel
Transfer-Encoding: chunked
X-Content-Type-Options: nosniff
Referrer-Policy: no-referrer
X-Frame-Options: DENY
Content-Security-Policy: default-src 'self'; frame-ancestors 'none'

{"type":"[https://tools.ietf.org/html/rfc9110#section-15.5.5](https://tools.ietf.org/html/rfc9110#section-15.5.5)","title":"Not Found","status":404,"traceId":"00-33832b8e16dbfcb428ecf1b7317318a4-537cf8eb74c61bc9-00"}