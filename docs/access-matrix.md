# Матриця контролю доступу (baseline 2-A, рівень Добре)

**Політика видимості:** для чужого ресурсу Reporter одержує 404, ідентичний відповіді для неіснуючого id
(однакові status, Content-Type і тіло; відрізняється лише traceId). Якщо ресурс користувачеві видимий,
але дія заборонена, повертається 403. Роль і owner беруться лише з server-issued principal;
body їх не визначає. Administrator не має додаткових прав: предметне правило baseline 2-A
(docs/variant-02-a.md) їх не називає, тому за замовчуванням deny.

| Дія | Рівень | Анонім | Reporter-власник | Інший Reporter | Analyst | Administrator | Власник має значення? | Реалізація / тест |
|---|---|---|---|---|---|---|---|---|
| List `GET /api/incidents` | добрий | 401 | 200, лише власні | 200, лише власні | 200, усі | 200, лише власні (їх немає) | так, для Reporter | query scoping; A-05 |
| Details `GET /api/incidents/{id}` | достатній | 401 | 200 | 404 | 200 (з internal) | 404 | так | handler Read; A-02, A-03, A-03R, A-10 |
| Create `POST /api/incidents` | успадковано | 401 | 201/200, owner з principal | те саме | allow | allow | сервер призначає owner | ЛР 3 |
| Update `PUT /api/incidents/{id}` (Title, Description) | добрий | 401 | 204 | 404 | 403 | 404 | так | handler Update; A-04, A-08a |
| Delete `DELETE /api/incidents/{id}` | добрий | 401 | 204 | 404 | 403 | 404 | так | handler Delete; A-04 |
| Triage `POST /api/incidents/{id}/triage` (New→Triaged) | добрий | 401 | 403 + audit | 404 | 204 якщо New, інакше 409 | 404 | ні (роль + status) | handler Triage; A-06, A-07, A-09 |
| Child `GET /api/incident-comments/{id}` | добрий | 401 | 200 для не-internal коментаря свого інциденту; інакше 404 | 404 | 200 | 404 | доступ через parent | handler Read(parent); A-12 |
| Internal comment у details | добрий | 401 | не повертається | 404 | повертається | 404 | ні | проєкція details; A-12 |
| Export/audit | відмінний | — | — | — | — | — | flow відсутній | не застосовується |
