# Олимпиадная платформа — сервис проверки и апелляций

Реализация на C# 12 / .NET 8 / ASP.NET Core и SQLite. Контракт 0.3 и все
46 исходных приёмочных сценариев сохранены. Сервис назначает ответы проверяющим,
хранит вердикты и историю, рассматривает апелляции, управляет общей квотой игры
и автоматически публикует подтверждение полноты проверок.

## Быстрый запуск проверок

Нужны .NET SDK 8 и Node.js 22+.

`dotnet test` включает HTTP-приёмку настоящего API в процессе тестов; перед ним нужен `npm ci`.
Методика измерения всех четырёх сборок и результат 96,90%: [COVERAGE.md](COVERAGE.md).

```sh
npm ci
npm test
dotnet test Olympiad.Review.sln
npm run test:service
```

Последняя команда собирает реальный сервис, запускает отдельные процессы сервиса
и внешних заглушек на свободных loopback-портах с новой временной SQLite,
выполняет 46 сценариев контракта и семь runtime-испытаний назначения,
авторизации и автоматического завершения.
В конце процессы и временные данные удаляются, в том числе при ошибке.
`npm test` отдельно проверяет документы и стенд, не бизнес-реализацию.

## Запуск сервиса

Для локального примера сначала запустите `npm run fixtures`, затем:

```powershell
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8083'
$env:Dependencies__BaseUrl = 'http://127.0.0.1:19100'
$env:Dependencies__AccessToken = 'test-system'
dotnet run --project src/Olympiad.Review.Api
```

Этот пример использует тестовый провайдер идентификации. В рабочем окружении
указываются реальные доверенные провайдеры и технический credential.
Управляемые часы доступны только при `ASPNETCORE_ENVIRONMENT=ContractTests`
и явном `Dependencies__ClockBaseUrl`; обычный запуск использует серверное UTC.

| Настройка | Назначение |
|---|---|
| `ASPNETCORE_URLS` | Адрес HTTP |
| `Storage__ConnectionString` | SQLite, по умолчанию `Data Source=.data/review.db` |
| `Dependencies__BaseUrl` | Общий адрес соседних систем, необязателен при отдельных адресах |
| `Dependencies__IdentityBaseUrl` | Introspection аутентификации |
| `Dependencies__GamesBaseUrl` | Актуальные сроки, версия приёма и возможности игры |
| `Dependencies__ReceiptsBaseUrl` | Реальный сервис приёма, `GET /receipts/{id}` |
| `Dependencies__TasksBaseUrl` | Неизменяемые задачи и серверные эталоны |
| `Dependencies__EventsBaseUrl` | Приёмник событий `POST /events` |
| `Dependencies__AccessToken` | Необязательный Bearer для исходящих запросов |
| `Dependencies__ClockBaseUrl` | Только тестовые часы |
| `Runtime__LeaseSeconds` | Положительный срок назначения, по умолчанию 300 секунд |

Проверка готовности: `GET /health`. Закрытые операции требуют Bearer и роли из
настроенного провайдера; администратор обладает всеми возможностями.
`Idempotency-Key` обязателен для команд, `Assignment-Token` — для действий
по текущему назначению.

## Структура и документы

- `src/Olympiad.Review.Domain` — агрегаты и инварианты без фреймворков.
- `src/Olympiad.Review.Application` — сценарии, небольшие порты и транзакционные границы.
- `src/Olympiad.Review.Infrastructure` — SQLite, HTTP-адаптеры, outbox.
- `src/Olympiad.Review.Api` — HTTP, авторизация, композиция и фоновые координаторы.
- `tests/Olympiad.Review.Tests` — домен, подстановка портов, SQL, восстановление и архитектура.
- `ARCHITECTURE.md` — зависимости, SOLID и гарантии.
- `SERVICE.md`, `CONTRACTS.md`, `contracts/` — бизнес-требования и сообщения.
- `test-support/README.md` — контракт внешнего испытательного стенда.

Рабочая интеграция с библиотекой задач, идентификацией и владельцем игры требует
доверенных внешних систем. Сервис не рассчитывает баллы и не завершает сам турнир:
публикует `AllChecksCompleted`, а владелец игры сверяет актуальную
`admissionVersion` перед применением результата.
