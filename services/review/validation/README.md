# Повторение независимой проверки

В каждой ветке:

```sh
npm ci
npm test
dotnet test
npm run test:service
node validation/audit-architecture.mjs .
```

`dotnet test` находит solution этой ветки. `test:service` запускает реальное API
с изолированными портами, PostgreSQL и соседними адаптерами; процессы останавливаются
после проверки. Архитектурный аудит проверяет направление проектных ссылок,
пакеты/инфраструктурные типы в ядре, инкапсуляцию и доступ API к Infrastructure.
Его дополняют xUnit-тесты фактических сборок и ручное ревью SOLID.

Для сквозной проверки нужны отдельные checkout трёх веток и .NET 8/Node.js 22+:

```sh
node validation/run-integration.mjs --materials /path/to/materials --intake /path/to/intake --review /path/to/review
```

Сценарий строит три API, проверяет взаимодействие и реальные перезапуски,
доставку события после отказа, сохранённые ответы при недоступной зависимости.
Соседние системы заменены независимыми адаптерами тестирования. Это стенд для
синтетических данных; он не разворачивается как часть продукта.

Чтобы сканировать страницы настоящего PDF, установите Poppler и добавьте:

```sh
node validation/run-integration.mjs --materials /path/to/materials --intake /path/to/intake --review /path/to/review --artifacts output/pdf/integration --render-pdf true
```

`--pdftoppm /absolute/path/to/pdftoppm` задаёт рендерер, если он отсутствует в PATH.
При этом intake получает растрированные страницы, а не отдельно сгенерированный QR.
PDF, страницы и ожидаемые идентификаторы остаются в каталоге `--artifacts`.
Временные базы/объекты и процессы удаляются независимо от результата проверки.

NuGet-проверка всех зависимостей:

```sh
dotnet list package --vulnerable --include-transitive
```

NuGetAuditMode=all и TreatWarningsAsErrors дополнительно делают такую проверку
частью восстановления пакетов. Подробные результаты — ARCHITECTURE_REVIEW.md.
