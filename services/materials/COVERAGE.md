# Проверка покрытия

Локальное измерение 9 ноября 2026: **866/927 строк — 93,42%** по Domain, Application, Infrastructure и API. Все 35 тестов xUnit проходят; HTTP-тест дополнительно выполняет 24 исходных сценария приёмки против настоящего приложения. Тестовые сборки и JavaScript в процент не входят.

Исходные 68,68% в таблице main отражали прежний набор .NET-тестов: отдельный процесс API из `npm run test:service` не попадал в сбор покрытия. Новый `HttpAcceptanceTests` выполняет неизменённую приёмку через настоящее приложение WebApplicationFactory, SQLite, фоновые PDF-обработчики и внешние HTTP-зависимости. Параллельные запросы проходят одновременно. Подмена бизнес-сценариев не используется.

```sh
npm ci
dotnet test Olympiad.Materials.sln -c Release
dotnet test Olympiad.Materials.sln -c Release --no-build --collect:"XPlat Code Coverage" --results-directory TestResults/coverage -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Olympiad.*]*"
```

Проверьте наличие всех четырёх `Olympiad.Materials.*` модулей в XML. Продуктовые сборки и файлы не исключаются из отчёта. Node.js 22 и системный Unicode-шрифт нужны и для .NET HTTP-теста; `npm ci` выполняется заранее. На Linux используются fonts-dejavu-core.

Новый регрессионный тест проверяет явные значения `Pooling=True/False`: адаптер обязан сохранять настройку SQLite из connection string. Исправлено принудительное включение pooling, мешавшее освобождению временной базы на Windows. Прежнее значение по умолчанию и нормализация строки подключения сохранены.
