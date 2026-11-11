# Проверка PostgreSQL, S3 и сохранённых процессов

Локальный прогон выполнен 11 ноября 2026 года: .NET 8, PostgreSQL 17.6 (отдельный тестовый экземпляр), VersityGW 1.11.0. Каждая проверка использует изолированную схему тестовой базы; объектное хранилище настоящее, с закрытыми бакетами.

Покрытие строк по Cobertura, без исключения продуктового кода четырёх сборок сервиса:

| Сервис | API | Application | Domain | Infrastructure |
| --- | ---: | ---: | ---: | ---: |
| Приём | 90,50% | 95,71% | 93,33% | 92,44% |
| Материалы | 92,20% | 94,64% | 98,28% | 93,10% |
| Проверка и апелляции | 92,58% | 98,78% | 97,12% | 96,31% |

Это результат инструментированного запуска реальных HTTP-приложений, предметных и инфраструктурных тестов. Node.js-сценарии проходят через HTTP-мост в тот же процесс .NET и учитываются в покрытии. Общая инфраструктура саг дополнительно проверяется своим тестовым проектом; она не подменяет предметные тесты.

Команды из корня (нужны `TEST_POSTGRES_CONNECTION`, `TEST_S3_ENDPOINT`, `TEST_S3_ACCESS_KEY`, `TEST_S3_SECRET_KEY`):

```sh
node tools/review-ci.mjs intake
node tools/review-ci.mjs materials
node tools/review-ci.mjs review
dotnet test tests/Olympiad.Workflows.Tests --collect:"XPlat Code Coverage"
dotnet test services/intake/Olympiad.Intake.sln --collect:"XPlat Code Coverage"
dotnet test services/materials/Olympiad.Materials.sln --collect:"XPlat Code Coverage"
dotnet test services/review/Olympiad.Review.sln --collect:"XPlat Code Coverage"
```

`TestResults/**/coverage.cobertura.xml` содержит исходные сведения по строкам. CI публикует XML и результаты тестов в artifacts; показатели веток в README обновляются отдельным workflow из main.
