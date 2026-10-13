# Sapientum: сервис приёма бланков

Реализованный backend на C# 12 / .NET 8 / ASP.NET Core и SQLite.
Контракт 0.3 и исходные 30 приёмочных сценариев сохранены. Domain, Application,
Infrastructure и Api — отдельные проекты с проверяемым направлением зависимостей.
Подробности решений SOLID, сохранения и восстановления: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Проверка

Требуются .NET SDK 8 и Node.js 22+. Пакеты закреплены в csproj/package-lock.json.

```sh
npm ci
npm test
dotnet test Sapientum.Intake.sln
npm run test:service
dotnet build Sapientum.Intake.sln --configuration Release
```

npm test проверяет схемы и тестовый стенд. dotnet test проверяет агрегаты,
сценарии, фактические архитектурные границы, SQLite rollback/restart/outbox,
отмену, конкурентные запросы и реальные PNG/JPEG/неоднозначные QR.
npm run test:service собирает настоящий API, запускает отдельный процесс внешних
заглушек, создаёт временную SQLite, выполняет неизменённые I01–I30 и дополнительные
проверки реального перезапуска, затем останавливает процессы и удаляет временные данные.
Заглушки не выполняют логику сервиса приёма.

## Локальный запуск

Сначала запустите внешние зависимости или npm run fixtures в другом терминале.

```powershell
$env:Dependencies__BaseUrl = 'http://127.0.0.1:19100'
$env:Dependencies__AccessToken = 'test-system' # только локальный стенд
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8082'
dotnet run --project src/Sapientum.Intake.Api
```

По умолчанию используется .data/intake.db. GET /health не требует credentials.
В обычном окружении сервис использует системное UTC-время.
Для управляемых часов стенда задайте ASPNETCORE_ENVIRONMENT=ContractTests
и Dependencies__ClockBaseUrl; в другом окружении эта настройка запрещена.

## Конфигурация

| Параметр | Назначение |
|---|---|
| Dependencies__BaseUrl | Необязательный общий базовый URL зависимостей |
| Dependencies__IdentityBaseUrl | POST /auth/introspect |
| Dependencies__GamesBaseUrl | GET /games/{id} |
| Dependencies__TeamsBaseUrl | GET /teams/{id} |
| Dependencies__MaterialsBaseUrl | POST /blank-verifications |
| Dependencies__ImagesBaseUrl | Идемпотентное защищённое POST /images |
| Dependencies__EventsBaseUrl | HTTP-приёмник /events |
| Dependencies__AccessToken | Необязательный Bearer для доверенных интеграций |
| Dependencies__ClockBaseUrl | GET /clock, исключительно ContractTests |
| Storage__ConnectionString | SQLite, например Data Source=.data/intake.db;Default Timeout=30 |
| Runtime__MaximumImageBytes | Лимит снимка, 1..15728640 байтов, по умолчанию 15 MiB |
| ASPNETCORE_URLS | Адрес API, пример http://127.0.0.1:8082 |

Отдельный URL приоритетнее общего. HTTP-клиенты и соединения освобождаются;
пул HTTP-соединений обновляется каждые пять минут. Приём ограничивает снимки
также 25 миллионами пикселей и проверяет заявленный PNG/JPEG против фактического
формата. Лимит HTTP body — 24 MiB для JSON с base64.
Для production нужны реальные доверенные поставщики, TLS, политика хранения и
уборка незавершённых объектов внешнего хранилища. Детали — в архитектурном документе.

## Документы и API

- SERVICE.md — бизнес-правила и границы ответственности.
- CONTRACTS.md — согласованные решения.
- contracts/openapi.json — OpenAPI 3.1, contracts/events.schema.json — события.
- contracts/requirements.md — трассировка исходных сценариев.
- contracts/ui-requirements.md — отдельные требования будущего интерфейса камеры.
- test-support/README.md — протокол внешних зависимостей для локального стенда.
- IMPLEMENTATION.md — единый стандарт трёх реализаций.

Сервис не получает эталоны, не ставит вердикты и не пересчитывает баллы.
Роли приходят от настроенного провайдера идентификации. Администратор наследует
возможности остальных ролей; комбинации глобальных ролей суммируются.
Не запускайте приёмочные тесты против production: они намеренно создают записи.

Соседние ветки: contracts/materials-service и contracts/review-service.
Все три backend-реализации используют один стек; их external gateways позволяют
подключить отдельные экземпляры сервисов вместо заглушек.
