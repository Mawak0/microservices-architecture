# Микросервисы олимпиадной платформы

<!-- intake-coverage:start -->
[![Покрытие сервиса приёма: 94.30%](.github/badges/intake-coverage.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/42570045845)

**Приём бланков — покрытие .NET-кода: 94.30%** (629/667 исполняемых строк).
Все четыре слоя, включая API; ветка `contracts/intake-service`, коммит `018061d`. [Отчёт CI](https://github.com/Mawak0/microservices-architecture/actions/runs/42570045845). Это показатель сервиса приёма, а не всех трёх сервисов.
<!-- intake-coverage:end -->


Репозиторий содержит три сервиса: бланки и печатные материалы, приём бланков,
проверка ответов и апелляции.

## Структура

В main находятся общие договорённости (CONTRACTS.md), схемы (contracts/common.json)
и базовый тестовый стенд (test-support/, tests/).
Сервисные реализации размещены в ветках соответствующих PR в каталогах
services/materials/, services/intake/ и services/review/.

## Проверка

Для сервисов используются .NET 8, C# 12 и SQLite; тесты — xUnit и Node.js 22.
Команды сборки и проверок приведены в README каждого сервиса.
Контракты и предметные правила находятся в его SERVICE.md,
CONTRACTS.md и contracts/.
