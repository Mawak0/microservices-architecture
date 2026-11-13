# Олимпиадная платформа Materials

Реализация сервиса бланков и печатных материалов: C# 12 / .NET 8 / ASP.NET Core,
PostgreSQL, PDFsharp и QRCoder. Исходный контракт 0.3 и 24 приёмочных сценария сохранены.
Сервис готовит комплекты, выпускает неизменяемые бланки с подписанным QR,
проверяет подлинность и создаёт реальные PDF из снимка выбранного шаблона.

## Архитектура

Domain ← Application ← Infrastructure; API связывает реализации только в composition root.
Доменные агрегаты не знают HTTP, SQL, JSON, PDF-библиотек и контейнера зависимостей.
Решения и SOLID описаны в [ARCHITECTURE.md](ARCHITECTURE.md).
Контракт: [SERVICE.md](SERVICE.md), [CONTRACTS.md](CONTRACTS.md),
[OpenAPI](contracts/openapi.json), [трассировка ТЗ](contracts/requirements.md).

## Проверка

Нужны SDK .NET 8, Node.js 22+ и Poppler (`pdftoppm` в PATH либо полный путь
в `PDF_RENDER_COMMAND`). На Linux установите `fonts-dejavu-core poppler-utils`.
На Windows используется установленный Arial; файл шрифта не распространяется.

`dotnet test Olympiad.Materials.sln` включает HTTP-приёмку настоящего API в процессе тестов;
перед ним нужен `npm ci`. Покрытие всех четырёх сборок — 93,42%; методика: [COVERAGE.md](COVERAGE.md).

```sh
npm ci
npm test
npm run test:service
```

Последняя команда собирает настоящий сервис, выполняет xUnit-тесты, запускает
изолированные внешние зависимости и HTTP-сервис с временной PostgreSQL, выполняет
неизменённые приёмочные сценарии и независимо считывает QR с растрированной
страницы фактически созданного PDF. Дополнительно выполняется настоящий рестарт
процесса: сохраняются QR/key, комплект, replay и ранее недоставленные события.
Процессы завершаются в finally.
`PDF_ARTIFACT_DIR` сохраняет PDF/PNG доказательства в выбранном каталоге;
без него временные файлы удаляются вместе с тестовой БД.

```sh
dotnet test Olympiad.Materials.sln --configuration Release
```

Проверяет доменные ограничения, replay при недоступных зависимостях, rollback/outbox,
повторное открытие БД, конкурентный выпуск, контракты взаимозаменяемых repository
adapters, архитектурные границы и извлечение кириллицы из настоящего PDF.
`npm test` продолжает проверять JSON Schema и тестовый стенд.

## Локальный запуск

В одном терминале `npm run fixtures`; в другом:

```powershell
$env:Dependencies__BaseUrl = 'http://127.0.0.1:19100'
$env:Dependencies__AccessToken = 'test-system'
$env:ASPNETCORE_ENVIRONMENT = 'ContractTests'
$env:Dependencies__ClockBaseUrl = 'http://127.0.0.1:19100'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8081'
dotnet run --project src/Olympiad.Materials.Api
```

Заглушки и test-* токены предназначены для локального тестирования.
GET /health не требует авторизации; остальные маршруты используют настроенный
провайдер introspection и capabilities внутри application use cases.

## Конфигурация

| Переменная | Назначение |
|---|---|
| ASPNETCORE_URLS | Адрес HTTP |
| Storage__ConnectionString | Required: Host=localhost;Port=55432;Database=materials;Username=materials;Password=... |
| Dependencies__BaseUrl | Общий адрес внешних adapters для локальной настройки |
| Dependencies__IdentityBaseUrl, GamesBaseUrl, TeamsBaseUrl, TasksBaseUrl, EventsBaseUrl, TemplatesBaseUrl | Отдельные адреса под префиксом Dependencies__ |
| Dependencies__AccessToken | Необязательная техническая Bearer identity; production default отсутствует |
| Templates__Directory | Приоритетный собственный файловый каталог, файлы <key>.json |
| Pdf__FontPath | TrueType-шрифт с нужными Unicode-глифами |
| Security__QrSigningKeyBase64 | Внешний 256-bit+ секрет; иначе случайный ключ создаётся и сохраняется в PostgreSQL |
| Runtime__MaxBlanksPerBatch | Технический предел одного HTTP-выпуска, default 10000; не правило игры |
| Dependencies__ClockBaseUrl | Управляемые часы; разрешены только в ContractTests |

В production без настройки внешнего каталога используются собственные декларативные
файлы templates/, поставляемые с API. Новый профиль добавляет ключ/возможности
и JSON-шаблон; имена игр не участвуют в коде ядра. Для контрактного стенда используется
HTTP-адаптер каталога. Сам PDF всегда создаёт сервис материалов; отдельный
микросервис шаблонов не требуется.

## Развёртывание

Нужны реальные провайдеры identity, игр/допуска, неизменяемых задач и событий.
Настройте HTTPS, техническую identity, права на БД/секрет, резервное копирование
PostgreSQL вместе с QR-ключом и собственный каталог шаблонов. Потеря или замена ключа
сделает выданные QR непроверяемыми; несогласованная смена настроенного ключа
отклоняется при запуске и требует миграции. Схема БД имеет версию 1;
дальнейшие изменения требуют миграций. Events допускают повторы по стабильному eventId.
Jobs/snapshots, replay, PDF и pending events переживают перезапуск.

declarative-v1 печатает Unicode-текст и QR, но не является LaTeX-движком.
Редактор шаблонов, сертификаты, приём ответов, вердикты и баллы относятся к другим
частям продукта. Ветки contracts/intake-service и contracts/review-service содержат
соответствующие сервисы.

Фотографии и PDF хранятся в закрытом S3. Настройки S3, проверка файлов и резервное копирование: [deploy/README.md](../../deploy/README.md).

Изменяющие запросы сохраняются до выполнения и повторяются после временного сбоя. `Operation-Id`, `Operation-Status`, `Operation-Location` связывают HTTP-ответ со статусом `GET /operations/{id}`. Ответ `202` означает долговременное принятие в обработку; окончание контролируется статусом операции. Подробности: [саги и контрольные точки](../../docs/architecture/durable-workflows.md).
