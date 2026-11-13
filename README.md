# Олимпиадная платформа

<!-- branch-coverage:start -->
## Покрытие по веткам

Покрытие исполняемых строк .NET-кода, включая все сборки сервиса и API. JavaScript-стенд и тестовые сборки не входят в процент.

| Ветка | Покрытие | Строки | Проверка | Коммит |
| --- | --- | ---: | --- | --- |
| [main](https://github.com/Mawak0/microservices-architecture/tree/main) | [![95.06%](.github/badges/branch-0d6e4079e36703eb.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/43433386656) | 2465/2593 | Тесты пройдены | [4345b3b](https://github.com/Mawak0/microservices-architecture/commit/4345b3b7a2025b6ffd3557efb95acb2e74482470) |
| [contracts/intake-service](https://github.com/Mawak0/microservices-architecture/tree/contracts%2Fintake-service) | [![94.30%](.github/badges/branch-3d04dca8c877d9db.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/43433386656) | 629/667 | Тесты пройдены | [018061d](https://github.com/Mawak0/microservices-architecture/commit/018061d8c2004e12af14c956d94fc055e6ee7c8e) |
| [contracts/materials-service](https://github.com/Mawak0/microservices-architecture/tree/contracts%2Fmaterials-service) | [![93.42%](.github/badges/branch-b3464660a74833fc.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/43433386656) | 866/927 | Тесты пройдены | [67437ab](https://github.com/Mawak0/microservices-architecture/commit/67437ab15e912a9a0f0f98ecc03b23da6b0eb943) |
| [contracts/review-service](https://github.com/Mawak0/microservices-architecture/tree/contracts%2Freview-service) | [![96.90%](.github/badges/branch-954fcce506399009.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/43433386656) | 968/999 | Тесты пройдены | [2f687ab](https://github.com/Mawak0/microservices-architecture/commit/2f687ab39c61b0878856f973c800326a883d6714) |
| [feature/durable-sagas](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fdurable-sagas) | [![94.95%](.github/badges/branch-8e53671bdb125cb8.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/43433386656) | 2990/3149 | Тесты пройдены | [6298e66](https://github.com/Mawak0/microservices-architecture/commit/6298e664a8d6e88d7dbec57046f394ec15e6a7f5) |
| [feature/integrated-storage-stand](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fintegrated-storage-stand) | [![94.58%](.github/badges/branch-9dbe287464c35be2.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/43433386656) | 3018/3191 | Тесты пройдены | [07276a9](https://github.com/Mawak0/microservices-architecture/commit/07276a95ba76d10a29eb7d29a023e26a83c050aa) |
| [feature/postgres-storage](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fpostgres-storage) | [![94.79%](.github/badges/branch-5dddf7ddd83ec86e.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/43433386656) | 2383/2514 | Тесты пройдены | [061b437](https://github.com/Mawak0/microservices-architecture/commit/061b4373e198810ea599f7793f31ed0078b1c295) |
| [feature/s3-storage](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fs3-storage) | [![94.25%](.github/badges/branch-a2967582acd4fc5d.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/43433386656) | 2476/2627 | Тесты пройдены | [3df5b3f](https://github.com/Mawak0/microservices-architecture/commit/3df5b3fa2ff38c3ceaff874fbeec54f5608f39d9) |
| [fix/coverage-test-dependencies](https://github.com/Mawak0/microservices-architecture/tree/fix%2Fcoverage-test-dependencies) | [![94.99%](.github/badges/branch-b8719cbfe0381ca0.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/43433386656) | 2463/2593 | Тесты пройдены | [e0fa441](https://github.com/Mawak0/microservices-architecture/commit/e0fa441a5659e21c9e077d38ff606f1029b8fc1d) |
| [review-layout](https://github.com/Mawak0/microservices-architecture/tree/review-layout) | — | — | Нет .NET-проектов | [4bcef00](https://github.com/Mawak0/microservices-architecture/commit/4bcef0017d3cf16826090a8c03bdddf67babcfc6) |

Проценты разных веток не суммируются: ветки могут содержать одинаковый код. Таблица обновляется после CI; по нажатию на бейдж открывается запуск измерения.
<!-- branch-coverage:end -->

## Сервисы

| Каталог | Назначение |
|---|---|
| services/materials/ | Комплекты, бланки с QR, печатные PDF |
| services/intake/ | Приём фотографий бланков и доставка ответов на проверку |
| services/review/ | Назначения проверяющим, вердикты, апелляции и квота |

В каждом каталоге находятся исходники, тесты, контракты и собственный README.
Сервисы используют .NET 8, C# 12, ASP.NET Core и SQLite.
Снимки бизнес-правил и источник Figma находятся в docs/event-storming/.
Первоначальные общие договорённости сохранены в legacy/contract-baseline/.

## Проверка

Из корня репозитория для каждого сервиса выполните npm ci --prefix services/<service>,
затем node tools/review-ci.mjs <service>, где <service> — materials, intake или review.
Команды запуска API и подробная конфигурация приведены в README соответствующего сервиса.
HTTP-контракты и события находятся в services/<service>/contracts/;
предметные правила — в SERVICE.md и CONTRACTS.md.
