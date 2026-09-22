# Звіт до лабораторної роботи № 1

## 1. Ідентифікація стану

- Варіант: 2-A «Трекер інцидентів».
- Гілка: робоча `lab/1-system`, основна `master`.
- Фінальний тег: `v0.1.0` (annotated).
- Commit hash: ca1f977a5abf8e207b230f930d70427a9ae124a1 Хеш функціонального коміту в `lab/1-system`: `6989e30`.

## 2. Змінений маршрут

```text
кнопка «Оновити» (Client/index.html)
  → обробник click (Client/app.js, apiFetch)
  → GET /api/incidents/severity-summary
  → IncidentEndpoints.GetSeveritySummaryAsync
  → IncidentQueries.GetSeveritySummaryAsync
  → SecureLabDbContext.Incidents / таблиця incidents (GROUP BY у PostgreSQL)
  → IncidentSeveritySummaryResponse
  → JSON
  → createElement + textContent
```

Ключовий фрагмент (`Application/Incidents/IncidentQueries.cs`, скорочено):

```csharp
var query = dbContext.Incidents.AsNoTracking();
if (status != null)
    query = query.Where(x => x.Status == status);

var data = await query
    .GroupBy(x => x.Severity)
    .Select(g => new IncidentSeveritySummaryResponse(/* severity, count */))
    .OrderBy(x => x.Severity)
    .ToListAsync(ct);
```

Endpoint перевіряє необов'язковий параметр `status` через `Enum.TryParse` + `Enum.IsDefined`. Некоректне значення дає 400 Validation Problem Details. Групування й підрахунок виконує PostgreSQL, тому вся таблиця в пам'ять не завантажується.

## 3. Виконані зміни

- `Presentation/Contracts/IncidentResponses.cs`: DTO `IncidentSeveritySummaryResponse(string Severity, int Count)` без зайвих полів entity.
- `Application/Incidents/IncidentQueries.cs`: метод `GetSeveritySummaryAsync` (`AsNoTracking`, `GroupBy`, `Count`, `ToListAsync`) і структурований log з параметром status і traceId.
- `Presentation/Endpoints/IncidentEndpoints.cs`: замість baseline 501 endpoint з DI, `CancellationToken`, `Results.Ok(...)` і metadata 200/400.
- `Client/index.html`, `Client/app.js`: кнопка «Оновити», стани «Завантаження...», «Немає даних.» і безпечна помилка «не вдалося завантажити зведення». Вивід через `createElement` + `textContent`, без `innerHTML`.
- `tests/http/incidents.http`: коментар оновлено з очікування 501 на 200.
- `docs/architecture.md`: карта архітектури, межі довіри, конфігураційні входи, reset.

Політика нульових груп: лише наявні групи (Critical відсутній, бо немає такого інциденту).
Порядок: лексикографічний за severity (High, Low, Medium), бо enum зберігається в PostgreSQL як текст.

## 4. Перевірка

| ID | Передумови | Дія | Очікувано | Фактично | Доказ |
|---|---|---|---|---|---|
| T-01 | PostgreSQL healthy, API запущено | GET /health | 200 | 200, `{"status":"ready"}` | PDF Рис. 6, 7 |
| T-02 | Відновлений seed | GET /api/incidents?status=Triaged | 200, список за фільтром | 200, один інцидент «Підозрілий лист із вкладенням» (Medium, Triaged) | PDF Рис. 31 |
| T-03 | Відновлений seed | GET /api/incidents?status=Resolved | 200, `[]` | 200, `[]` | PDF Рис. 28 |
| T-04 | Відновлений seed | GET /api/incidents/99999999-9999-9999-9999-999999999999 | 404 Problem Details | 404, `application/problem+json`, «Інцидент не знайдено», є traceId | PDF Рис. 28 |
| T-05 | Відновлений seed | GET /api/incidents?status=Unknown | 400 Validation Problem Details | 400, `application/problem+json`, перелік допустимих значень статусу | PDF Рис. 31 |
| T-06 | Реалізовано етап 3, seed відновлено | GET /api/incidents/severity-summary | 200; High, Low, Medium по 1; Critical відсутній | 200; `[{"severity":"High","count":1},{"severity":"Low","count":1},{"severity":"Medium","count":1}]` | PDF Рис. 28 |
| T-07 | API і клієнт запущено | Натиснути «Оновити» | UI безпечно показує результат | Список High: 1, Low: 1, Medium: 1; запит GET 200 `application/json` | PDF Рис. 25 |
| T-08 | Після зміни даних | `--reset-database`, повторити T-02 і T-06 | Seed відновлено | Дані очищено й заповнено наново; T-02 і T-06 повернули ті самі результати | PDF Рис. 33, 34|

Додатково: до реалізації endpoint повертав 501 (PDF Рис. 8). Некоректний `status` для summary дає 400 (PDF Рис. 26).

Автоматична перевірка: `dotnet test tests/SecureLab.Api.Tests/SecureLab.Api.Tests.csproj --configuration Release` → `total: 4, failed: 0, succeeded: 4` (PDF Рис. 32, після злиття в `master`).

Перевірка на секрети: перед кожним commit переглянуто `git status`, `git diff` і `git diff --staged`. Файли `.env`, паролі, токени, cookies, дампи БД і журнали в коміти не потрапили.

## 5. Security-сценарій

Для ЛР 1 навмисно вразливий стан не передбачено, тому окремий PoC не виконувався. Перевірено безпечну поведінку:

- недовірений параметр `status` перевіряється на сервері (`Enum.TryParse` + `Enum.IsDefined`) → 400 для некоректного значення;
- відсутній ресурс дає 404 (коректний UUID без запису), а некоректний параметр дає 400: це різна семантика;
- відповідь містить лише `severity` і `count`, без description, owner ID, email і коментарів;
- дані з відповіді потрапляють у DOM через `textContent`; seed-текст `<script>` в описі інциденту відображається як звичайний текст;
- у повідомленні про помилку клієнта для summary немає stack trace, SQL чи внутрішніх деталей.

Залишковий ризик: `GET /api/incidents/severity-summary` не має автентифікації та авторизації (їх немає в starter, вони належать наступним ЛР).

## 6. Висновок

Запущено PostgreSQL, API та клієнт, простежено маршрут браузер → API → PostgreSQL → JSON → DOM і реалізовано endpoint `GET /api/incidents/severity-summary`. Він повертає 200 з підрахунком за severity (High, Low, Medium по 1), а некоректний параметр дає 400. Групування виконує СУБД, використано `AsNoTracking`, клієнт показує результат через безпечні DOM API. Усі 4 автоматичні тести пройшли, стан зафіксовано в Git із тегом `v0.1.0` на `master`.