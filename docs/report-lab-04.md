# Звіт ЛР04. Авторизація, ролі, володіння ресурсом та IDOR/BOLA

## Контекст і межа виконання

- **Рівень:** добрий («Добре»). Реалізовано матрицю доступу, централізований захист ресурсів від IDOR/BOLA на читання та зміну, query scoping для списку, привілейований перехід статусу (triage) з перевіркою ролі, захист дочірніх ресурсів (incident comments) з урахуванням internal-коментарів (`IsInternal`), структурований audit відхилених привілейованих дій та захист від overposting.
- **Baseline:** 2-A. Базовий тег: `v0.3.0` (commit `c0704fa`). Гілка: `lab/4-authorization`.
- **Головна action:** `GET /api/incidents/{id}` (details/read). Охоплений периметр: `GET /api/incidents`, `PUT /api/incidents/{id}`, `DELETE /api/incidents/{id}`, `POST /api/incidents/{id}/triage`, `GET /api/incident-comments/{id}`.
- **Політика HTTP-відмов:** для чужого ресурсу повертається `404 Not Found`, ідентичний відповіді для неіснуючого id (щоб не розкривати існування чужих даних). Якщо ресурс користувачеві видимий, але дія заборонена, повертається `403 Forbidden`. Неавтентифікований запит дає `401`.
- **Середовище:** Windows 11, .NET 10 SDK, PostgreSQL у Docker Compose. Усі перевірки виконано лише локально, на штучних даних (Alice, Bob, Morgan, Admin).
- **Стан ЛР 3:** після тегу `v0.3.0` код ЛР 3 не змінювався; стартові перевірки (login Alice/Bob/Morgan, `/api/me`, захищений `POST /api/incidents`) пройшли.

## Матриця і server-side рішення

Матрицю зафіксовано у `docs/access-matrix.md`. Рішення ухвалює сервер за роллю з автентифікованого principal і за власником ресурсу; role, owner і status з тіла запиту не приймаються.

| Дія | Анонім | Reporter-власник | Інший Reporter | Analyst | Administrator |
|---|---|---|---|---|---|
| Список | 401 | лише власні | лише власні | усі | лише власні |
| Деталі | 401 | 200 | 404 | 200 (з internal) | 404 |
| Update (Title, Description) | 401 | 204 | 404 | 403 | 404 |
| Delete | 401 | 204 | 404 | 403 | 404 |
| Triage New→Triaged | 401 | 403 + audit | 404 | 204 (лише з New), інакше 409 | 404 |
| Коментар за id | 401 | 200 (не-internal свого інциденту) | 404 | 200 | 404 |

Administrator не отримує додаткових прав, оскільки картка 2-A їх не називає (принцип найменших привілеїв).

**Місця рішень у коді:**

- **Автентифікація периметра:** `.RequireAuthorization()` на групі `/api/incidents` і на маршруті `/api/incident-comments/{id}` (`IncidentEndpoints.cs`).
- **Єдине місце рішення:** `IncidentAuthorizationHandler` (`IncidentAuthorization.cs`) з операціями Read, Update, Delete, Triage. Owner береться з `ClaimTypes.NameIdentifier`, роль з server-issued claim.
- **Query scoping:** `IncidentQueries.GetListAsync` обмежує вибірку: Reporter бачить лише `OwnerUserId == ownerId`, Analyst бачить усе.
- **Details:** перед викликом `GetDetailsAsync` виконується рішення Read; internal-коментарі повертаються лише Analyst.
- **DTO:** `UpdateIncidentRequest` містить лише `Title` і `Description`. `OwnerUserId`, `Status`, `Role` сервер із body не читає.
- **Єдиний helper відмов:** `IncidentNotFound()` повертає однаковий Problem Details зі статусом 404.

## Вразливий стан і PoC

- **Vulnerable commit:** `482b92626f870f5793d9caf6fbdf9e5ff76d4892`.

На цьому commit запит за id виконувався без порівняння з principal. Alice (Reporter) надсилає `GET /api/incidents/20000000-0000-0000-0000-000000000002` (інцидент Bob) зі своєю cookie, змінивши лише identifier.

- **Очікувано за матрицею:** 404.
- **Фактично:** 200 OK з даними Bob («Невідома спроба входу», «Боб Мельник») — IDOR/BOLA на читання (Рис. 1).

![Рис. 1]

**Рис. 1.** Стан до виправлення: Alice читає інцидент Bob (IDOR/BOLA на читання).

Далі перевірено зміну чужого ресурсу і привілейовану дію: Alice виконала `PUT` для інциденту Bob (204), після чого Bob побачив змінений title; також Alice (Reporter) виконала `POST .../triage` (204) без ролі Analyst. Це і горизонтальне, і вертикальне підвищення привілеїв (Рис. 2).

![Рис. 2]

**Рис. 2.** Стан до виправлення: Alice змінила інцидент Bob і виконала triage без ролі Analyst.

**Додаткові спостереження до PoC:**

- Анонімний `GET /api/incidents/{id}` повернув 200 і дані інциденту Alice, тобто маршрут не вимагав автентифікації.
- Alice (Reporter) отримала 200 на `GET /api/incident-comments/{id}`, і відповідь містила internal-коментар аналітика: відсутня перевірка батьківського інциденту та ознаки `IsInternal`.
- Під час `PUT` Alice передала поля `role`, `status`, `ownerUserId` разом з `title` і `description`. Сервер повернув 204, але повторне читання показало, що змінилися лише title і description, а status, severity і власник лишилися попередніми: додаткові поля не входять до контракту `UpdateIncidentRequest`, тому ігноруються.

## Першопричина

Запити шукали ресурс лише за первинним ключем (`Where(i => i.Id == id)`) і не порівнювали його з власником із claims. Маршрут деталей не вимагав автентифікації. Операція triage не перевіряла роль Analyst, а дочірній маршрут коментарів не перевіряв право на батьківський інцидент та ознаку `IsInternal`. Сам факт передачі id в URL дефектом не є: дефектом була відсутність серверного рішення «суб'єкт + дія + ресурс».

## Виправлення

1. Групу `/api/incidents` і маршрут `/api/incident-comments/{id}` закрито `RequireAuthorization()`.
2. Створено централізований `IncidentAuthorizationHandler` (Read, Update, Delete, Triage).
3. Для списку застосовано query scoping за суб'єктом.
4. Єдиний helper `IncidentNotFound()` дає однакову відповідь для чужого й неіснуючого id.
5. `UpdateIncidentRequest` звужено до `Title` і `Description`.
6. Triage дозволено лише Analyst і лише зі статусу New; наступний статус встановлює сервер і додає запис до історії статусів.
7. Дочірній коментар перевіряється через доступ до батьківського інциденту; internal-коментарі бачить лише Analyst.
8. При відмові triage записується структурована подія `authorization.denied` з `traceId`.

## Автоматичні тести

Три старі HTTP-тести з ЛР 1 переведено на автентифікованого клієнта, оскільки після захисту групи анонімні запити отримують 401. Додано `IncidentAuthorizationTests` для сценаріїв A-01…A-12. Результат: `total: 46, failed: 0, succeeded: 46`.

![Рис. 3]

**Рис. 3.** Автоматичні тести авторизації A-01…A-12 пройдено.

## Retest

Той самий запит Alice → Bob після виправлення повертає `404 Not Found` з Problem Details «Інцидент не знайдено», без даних Bob.

![Рис. 4]

**Рис. 4.** Стан після виправлення: запит Alice → Bob повертає 404, даних Bob немає.

- **Для mutation:** `PUT` і `DELETE` Alice для інциденту Bob повертають 404, стан Bob не змінюється (тест A-04). Triage від Alice повертає 403, статус лишається New (A-06).
- **Для read:** `IncidentDetailsResponse` не містить `ownerUserId` та email. Internal-коментар не розкривається Reporter (A-12).
- **Нерозрізнюваність 404 (A-10):** чужий інцидент і випадковий неіснуючий GUID дають однакові status, `Content-Type: application/problem+json` і тіло (після вилучення `traceId`).

## Доказова таблиця

| ID | Рівень | Очікувано | Фактично | Доказ |
|---|---|---|---|---|
| A-01 | добрий | 401, не HTML redirect | 401 | тест `Details_Anonymous_Returns401_NotRedirect` |
| A-02 | достатній | allow | 200, дані Alice | тест `Details_AsOwner_ReturnsOwnIncident`; Рис. 5 |
| A-03 | достатній | 404, дані Bob відсутні | 404, даних Bob немає | тест `Details_AsOtherReader_Returns404_WithoutBobData`; Рис. 4 |
| A-03R | достатній | allow для аналізу | 200 | тест `Details_AsAnalyst_ReturnsForeignIncident` |
| A-04 | добрий | deny, стан без змін | 404, стан Bob не змінився | тест `UpdateAndDelete_AsOtherReader_Return404_AndDoNotChangeIncident` |
| A-05 | добрий | лише дозволена множина | лише інциденти Alice | тест `List_AsReporter_ContainsOnlyOwnIncidents` |
| A-06 | добрий | deny, status без змін | 403, статус New | тест `Triage_AsReporterOwner_Returns403_AndStatusStaysNew` |
| A-07 | добрий | allow | 204, статус Triaged | тест `Triage_AsAnalyst_MovesNewToTriaged` |
| A-08a | добрий | content змінюється, owner/status ні | 204, owner і status не змінилися | тест `Update_WithForgedFields_IgnoresOwnerRoleAndStatus` |
| A-08b | добрий (альтернатива) | — | не виконувалось (обрано A-08a) | — |
| A-09 | добрий | безпечний audit зі спільним traceId | рядок `authorization.denied`, traceId збігається з відповіддю | Рис. 7 |
| A-10 | добрий (політика 404) | однакові status, Content-Type і тіло крім traceId | ідентичні | тест `ForeignAndUnknownId_AreIndistinguishable` |
| A-11 | відмінний | — | не виконувалось — рівень «Відмінно» | — |
| A-12 | добрий | 404 для child і internal; Analyst бачить internal | 404 для Alice, 200 для Morgan | тест `ChildComment_DoesNotLeakToReporter` |

## Browser flow

1. Вхід виконано на `http://localhost:5080/auth-lab.html` як `alice`.
2. У тій самій вкладці відкрито `http://localhost:5080/`, обрано власний інцидент Alice («Підозрілий лист із вкладенням»): деталі відображено, автор «Аліса Коваль» (Рис. 5).

![Рис. 5]

**Рис. 5.** Деталі власного інциденту Alice у browser client.

3. У DevTools Console виконано `loadIncidentDetails("20000000-0000-0000-0000-000000000002")` (id інциденту Bob). Клієнт показав «Помилка: Інцидент не знайдено» через `textContent`, не розкривши чужих даних (Рис. 6). Приховування елементів інтерфейсу не є контролем доступу: рішення ухвалює сервер.

![Рис. 6]
**Рис. 6.** Безпечне відображення відмови в browser client: Alice не отримує інцидент Bob.

## Привілейована дія, child і audit

- **Triage:** `POST /api/incidents/{id}/triage` дозволений лише Analyst і лише для інцидентів зі статусом New. Сервер сам читає поточний статус із БД і встановлює `Triaged`; клієнт статус не передає. Reporter отримує 403, статус не змінюється.
- **Child:** `GET /api/incident-comments/{id}` перевіряє право на батьківський інцидент. Alice отримує 404 для коментаря до інциденту Bob і для internal-коментаря `comment-3`. Morgan (Analyst) отримує 200 і бачить internal-текст.
- **Audit:** при відмові triage записується `authorization.denied` з полями `subject`, `action=incident.triage`, `resource`, `result=denied`, `occurredAtUtc`, `traceId`. Значення `traceId` у логу збігається з `traceId` у відповіді 403. Cookie, пароль і тіло запиту в подію не потрапляють.

[Рис. 7]

**Рис. 7.** Структурована audit-подія при відмові triage для Reporter: subject, action, resource, result; traceId збігається з traceId у відповіді.

## Git: hashes і тег

| Стан | Commit |
|---|---|
| Базовий (тег `v0.3.0`) | `c0704fa` |
| Vulnerable | `482b92626f870f5793d9caf6fbdf9e5ff76d4892` |
| Fixed (тег `v0.4.0`) | `7c4d7da457ca20f6fd9e6485ea98c74e8868e55c` |

![Рис. 8]

**Рис. 8.** Вивід `git log --oneline --decorate -4`: fixed commit «Enforce resource authorization for incidents» з тегом `v0.4.0`, нижче vulnerable commit `482b926`.

## Основний finding

1. **Контекст:** ресурс Incident; дії read, list, update, delete, triage і читання дочірніх коментарів. Суб'єкти: Alice і Bob (Reporter), Morgan (Analyst).
2. **Гіпотеза:** Alice зможе читати й змінювати інцидент Bob підстановкою id, а також виконати привілейований triage без ролі Analyst.
3. **Стан до:** commit `482b926…`; `GET` інциденту Bob повертав 200 і чужі дані, `PUT` змінював чужий ресурс, `POST /triage` повертав 204 без перевірки ролі (Рис. 1, 2).
4. **Першопричина:** пошук лише за id без порівняння з principal; відсутні перевірка ролі в triage і перевірка батьківського інциденту в child endpoint.
5. **Виправлення:** `RequireAuthorization()`, централізований `IncidentAuthorizationHandler`, query scoping списку, helper `IncidentNotFound()`, звужений `UpdateIncidentRequest`, triage лише для Analyst зі статусу New, audit при відмові.
6. **Retest:** той самий запит Alice → Bob повертає 404; `PUT`, `DELETE` і triage від Alice блокуються, стан не змінюється (Рис. 3, 4, 6; тести A-04, A-06).
7. **Позитивна регресія:** Alice читає власний інцидент (Рис. 5, A-02) і оновлює дозволені поля (A-08a); Morgan виконує triage для нового інциденту (A-07) і читає інцидент Alice (A-03R).
8. **Залишковий ризик:**
   - CSRF для cookie-authenticated змінювальних маршрутів: задокументований навчальний борг ЛР 3, усувається в ЛР 6.
   - Роль зберігається в cookie, тому зміна ролі в БД діє після повторного входу.
   - `search` і `severity-summary` не обмежені за суб'єктом (вимога рівня «Відмінно», A-11).
   - Batch-ендпойнтів і експорту немає; concurrent transitions не перевірялись.
   - Administrator не має додаткових прав (least privilege).

**Схема рішення:** запит з id → cookie → principal → завантаження entity → handler (owner/role) → 404/403 або дозвіл → зміна стану лише після дозволу.

## Відмінний рівень

Не виконувалось: обрано рівень «Добре».

## Перевірки, відтворення і подання

- **Відновлення стану:** `dotnet run --project src/SecureLab.Api -- --reset-database` очищає й заповнює БД синтетичними даними (включно з `comment-3`).
- **Тести:** `dotnet test tests/SecureLab.Api.Tests/SecureLab.Api.Tests.csproj --configuration Release` — `total: 46, failed: 0, succeeded: 46` (Рис. 3).
- **Git:** гілка `lab/4-authorization`, vulnerable commit `482b926`, fixed commit `7c4d7da` з тегом `v0.4.0`.

## Висновок

У ході лабораторної роботи на локальному стенді зі штучними даними відтворено горизонтальне підвищення привілеїв (IDOR/BOLA): Alice з роллю Reporter, підставивши identifier, читала й змінювала інцидент Bob, а також виконувала привілейований перехід статусу без ролі Analyst. Першопричиною була відсутність серверного рішення «суб'єкт + дія + ресурс + контекст»: запити знаходили ресурс лише за id.

Дефект усунено на сервері: захищено маршрути, створено централізований resource-based handler, обмежено вибірку списку за суб'єктом, звужено DTO оновлення до полів контенту, обмежено triage роллю Analyst і статусом New, а доступ до коментарів прив'язано до батьківського інциденту та ознаки `IsInternal`. Обрано й послідовно застосовано політику 404 для чужих і неіснуючих ресурсів, що підтверджено ідентичністю відповідей (A-10). Відмову привілейованої дії зафіксовано структурованою audit-подією зі спільним `traceId`, без cookie, пароля й тіла запиту.

Результат підтверджено 46 автоматичними тестами (`failed: 0`), повторним retest і перевіркою у browser client, який лише відображає рішення сервера через `textContent`. Vulnerable і fixed стани збережено окремими commit (`482b926`, `7c4d7da`), тег `v0.4.0` поставлено на перевірений fixed commit. Залишаються відкритими CSRF (ЛР 6) та обмеження `search` і `severity-summary` за суб'єктом, що належить до рівня «Відмінно». Головний висновок: приховування кнопок, UUID чи ролі в браузері не замінюють серверну авторизацію, а відмову треба підтверджувати незмінністю стану ресурсу.