1. ПЕРВИННА ПЕРЕВІРКА, NOSNIFF ТА ВІДОКРЕМЛЕННЯ ВІД CSP/CORS
- Первинна перевірка: у Network дивимось Request URL, Content-Type та початок тіла (Response). Статус 200 не гарантує, що повернувся JS (може бути HTML-заглушка).
- Роль nosniff: забороняє браузеру «вгадувати» тип файла (MIME sniffing) і змушує вірити заголовку Content-Type: text/html, тому JS блокується.

2. РОЗБІР URL (для уявного http://localhost:5080/tools/page.html)
- src="browser-lab.js"  -> http://localhost:5080/tools/browser-lab.js (відносно папки /tools/)
- src="/browser-lab.js" -> http://localhost:5080/browser-lab.js (від кореня сайту)

3. ТРИ МОДЕЛІ ВІДПОВІДІ
- 200 / JavaScript + JS-тіло -> Норма, скрипт працює.
- 200 / text/html + HTML-тіло -> Помилка маршрутизації (наприклад, SPA fallback повернув HTML замість JS); nosniff блокує виконання.
- 404 / text/html + HTML-тіло -> Файл не знайдено за цим URL, сервер віддав HTML-сторінку помилки 404.

4. ФАКТИЧНЕ ТРАСУВАННЯ ВЛАСНОГО SCRIPT
- src у HTML: browser-lab.js
- Кінцевий URL: http://localhost:5080/browser-lab.js
- Initiator: browser-lab.html
- Status: 200
- MIME (Content-Type): [text/javascript]
- Початок тіла (Response): [

5. ВИПРАВЛЕННЯ ДЛЯ ДЕФЕКТНОЇ МОДЕЛІ (без зміни nosniff і CSP)
Було:  <script src="browser-lab.js" defer></script>
Стало: <script src="/browser-lab.js" defer></script>


20000000-0000-0000-0000-000000000001