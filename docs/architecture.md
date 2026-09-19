# Карта архітектури

Документ описує змінений у ЛР 1 маршрут `GET /api/incidents/severity-summary` (варіант 2-A «Трекер інцидентів»).

## Компоненти

| Компонент | Розташування | Відповідальність |
|---|---|---|
| Browser client | `src/SecureLab.Api/Client/` | Надсилає HTTP-запити, безпечно показує відповідь через DOM API |
| Presentation | `Presentation/` | Описує endpoints, читає зовнішні параметри, формує HTTP-відповідь |
| Application | `Application/` | Виконує сценарій отримання списку, деталей або зведення інцидентів |
| Data | `Data/` | Відображає C#-сутності на PostgreSQL через EF Core/Npgsql |
| PostgreSQL | `infra/compose.yaml` | Зберігає навчальні дані у локальному контейнері Docker Compose |

## Змінений маршрут (підсумок за severity)

```text
кнопка «Оновити» у Client/index.html
  → обробник click у Client/app.js (apiFetch)
  → GET /api/incidents/severity-summary
  → IncidentEndpoints.GetSeveritySummaryAsync
  → IncidentQueries.GetSeveritySummaryAsync
  → SecureLabDbContext.Incidents / таблиця incidents (GROUP BY у PostgreSQL)
  → IncidentSeveritySummaryResponse
  → JSON
  → createElement + textContent у списку зведення
```

Для порівняння, підготовлений маршрут деталей: `GET /api/incidents/{id}` → `IncidentEndpoints.GetDetailsAsync` → `IncidentQueries.GetDetailsAsync` → `incidents` → `IncidentDetailsResponse` → `renderIncidentDetails`.

## Ключові файли

- Client: `src/SecureLab.Api/Client/index.html`, `Client/app.js`
- Endpoint: `src/SecureLab.Api/Presentation/Endpoints/IncidentEndpoints.cs`
- Application layer: `src/SecureLab.Api/Application/Incidents/IncidentQueries.cs`
- DTO: `src/SecureLab.Api/Presentation/Contracts/IncidentResponses.cs`
- DbContext: `src/SecureLab.Api/Data/SecureLabDbContext.cs`
- Таблиця: `incidents`
- Ручні сценарії: `tests/http/incidents.http`

## Контракт summary

- Відповідь: `200 OK`, JSON-масив елементів `{ severity, count }`, без description, owner ID, email і коментарів.
- Необов'язковий параметр `status`: перевіряється через `Enum.TryParse` і `Enum.IsDefined`; некоректне значення дає `400` Validation Problem Details.
- Політика нульових груп: лише наявні групи. На baseline seed це Low, Medium, High по одному (`count: 1`), Critical відсутній.
- Порядок: лексикографічний за `severity` (High, Low, Medium), бо enum зберігається в PostgreSQL як текст.
- Групування й підрахунок виконує PostgreSQL (`GROUP BY`), запит використовує `AsNoTracking()`.
- Журналювання: структурований log із кількістю груп, без чутливих даних.

## Межі довіри

| Межа | Дані, що її перетинають | Чому даним ще не можна довіряти | Де перевіряємо або обмежуємо |
|---|---|---|---|
| Користувач → Browser client | значення форми, натискання кнопки | Користувач контролює введення | Клієнт лише допомагає сформувати запит; він не є серверним контролем |
| Browser client → API | method, URL, query `status`, path `id` | Клієнт і HTTP-запит можна змінити поза UI | `Enum.TryParse` + `Enum.IsDefined` → 400; маршрутне обмеження `:guid`; відсутній ресурс → 404 |
| API → PostgreSQL | умова фільтра за status, групування | Збережений текст не є автоматично безпечним | Параметризація EF Core, `AsNoTracking()`, явна проєкція в DTO |
| API → Browser | JSON зведення | Право читати entity не означає право отримати всі поля | Окремий DTO лише з `severity` і `count` |
| Response → DOM | текст severity і count | Текст не можна інтерпретувати як HTML | `createElement` + `textContent`, без `innerHTML` |

## Конфігураційні входи

- `global.json` — версія .NET SDK;
- `src/SecureLab.Api/appsettings*.json` — режим міграцій і локальний connection string;
- `infra/compose.yaml` — версія PostgreSQL, порт і локальні навчальні облікові дані;
- змінна середовища `ConnectionStrings__SecureLab` — безпечний спосіб перевизначити connection string поза репозиторієм.

Реальні значення секретів у документі не наводяться.

## Повернення до відомого seed-стану

Зупинити API (Ctrl+C), потім:

```text
dotnet run --project src/SecureLab.Api -- --reset-database
```

Команда застосовує migrations, очищує лише відомі навчальні таблиці й повторно заповнює seed. Працює лише в Development.
