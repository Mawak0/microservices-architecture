# Проверка покрытия

Локальное измерение 9 ноября 2026: **968/999 строк — 96,90%** по Domain, Application, Infrastructure и API. Тестовые сборки и Node.js-стенд не входят в знаменатель. Все 25 тестов xUnit проходят; один из них дополнительно выполняет 46 исходных HTTP-сценариев и 7 runtime-сценариев Node.js против настоящего приложения.

Исходные 44,34% в таблице main корректно описывали только прежний сбор покрытия: отдельный процесс API из `npm run test:service` не инструментировался. Новый `HttpAcceptanceTests` запускает настоящий API через WebApplicationFactory, PostgreSQL и внешние HTTP-зависимости. HTTP-мост не сериализует параллельные запросы. Существующие приёмочные утверждения сохранены.

```sh
npm ci
dotnet test Olympiad.Review.sln -c Release
dotnet test Olympiad.Review.sln -c Release --no-build --filter FullyQualifiedName!~ArchitectureTests.CompiledCoreAssembliesDependOnlyInwards --collect:"XPlat Code Coverage" --results-directory TestResults/coverage -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Olympiad.*]*"
```

Первый прогон проверяет архитектуру на исходных сборках. Во втором исключается только повторный тест списка ссылок сборок: Coverlet добавляет `netstandard` при инструментировании. Продуктовый код не исключается. В отчёте должны присутствовать все четыре `Olympiad.Review.*` модуля.

Прогоны изоляционные: случайные порты, временная PostgreSQL, временная конфигурация окружения с восстановлением, остановка дочерних процессов. Перед `dotnet test` необходимо выполнить `npm ci`, так как HTTP-тест использует неизменённую Node.js-приёмку. Локальный демонстрационный стенд не затрагивается.
