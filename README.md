# Олимпиадная платформа

<!-- branch-coverage:start -->
## Покрытие по веткам

Покрытие исполняемых строк .NET-кода, включая все сборки сервиса и API. JavaScript-стенд и тестовые сборки не входят в процент.

| Ветка | Покрытие | Строки | Проверка | Коммит |
| --- | --- | ---: | --- | --- |
| [main](https://github.com/Mawak0/microservices-architecture/tree/main) | [![94.99%](.github/badges/branch-0d6e4079e36703eb.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/45119300972) | 2463/2593 | Тесты пройдены | [cf7d203](https://github.com/Mawak0/microservices-architecture/commit/cf7d2032fbccc220b393ab3f94f14aa42f85c271) |
| [feature/durable-sagas](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fdurable-sagas) | [![94.79%](.github/badges/branch-8e53671bdb125cb8.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/45119300972) | 2985/3149 | Тесты пройдены | [6298e66](https://github.com/Mawak0/microservices-architecture/commit/6298e664a8d6e88d7dbec57046f394ec15e6a7f5) |
| [feature/integrated-storage-stand](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fintegrated-storage-stand) | [![94.58%](.github/badges/branch-9dbe287464c35be2.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/45119300972) | 3018/3191 | Тесты пройдены | [07276a9](https://github.com/Mawak0/microservices-architecture/commit/07276a95ba76d10a29eb7d29a023e26a83c050aa) |
| [feature/postgres-storage](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fpostgres-storage) | [![94.75%](.github/badges/branch-5dddf7ddd83ec86e.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/45119300972) | 2382/2514 | Тесты пройдены | [061b437](https://github.com/Mawak0/microservices-architecture/commit/061b4373e198810ea599f7793f31ed0078b1c295) |
| [feature/s3-storage](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fs3-storage) | [![94.02%](.github/badges/branch-a2967582acd4fc5d.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/45119300972) | 2470/2627 | Тесты пройдены | [3df5b3f](https://github.com/Mawak0/microservices-architecture/commit/3df5b3fa2ff38c3ceaff874fbeec54f5608f39d9) |
| [feature/staff-api-cache](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fstaff-api-cache) | [![94.83%](.github/badges/branch-d894d940fa30638d.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/45119300972) | 3286/3465 | Тесты пройдены | [d7c5102](https://github.com/Mawak0/microservices-architecture/commit/d7c51022bf73a7348f0ff105ffb84cc033ee7946) |
| [feature/staff-cabinet](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fstaff-cabinet) | [![94.06%](.github/badges/branch-a068980200c36dd4.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/45119300972) | 3565/3790 | Тесты пройдены | [128ebce](https://github.com/Mawak0/microservices-architecture/commit/128ebce454a06ac7084307f798fa66ff73f660a0) |
| [feature/staff-observer](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fstaff-observer) | [![93.99%](.github/badges/branch-504a5ac9e7b88be5.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/45119300972) | 3583/3812 | Тесты пройдены | [f84d8ff](https://github.com/Mawak0/microservices-architecture/commit/f84d8fff8dddecb22ff0bcf46c6801445f1e1b9b) |
| [feature/staff-retakes](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fstaff-retakes) | [![94.14%](.github/badges/branch-e9b6432fbbd7a248.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/45119300972) | 3568/3790 | Тесты пройдены | [9b385c7](https://github.com/Mawak0/microservices-architecture/commit/9b385c71f9b54fe829267e7f5e637d777e02c74a) |
| [feature/staff-reviewer](https://github.com/Mawak0/microservices-architecture/tree/feature%2Fstaff-reviewer) | [![94.15%](.github/badges/branch-7068740f025b61bd.svg)](https://github.com/Mawak0/microservices-architecture/actions/runs/45119300972) | 3636/3862 | Есть ошибки тестов/сборки | [b83e8b0](https://github.com/Mawak0/microservices-architecture/commit/b83e8b057b34d15c40db3bc18701c59f9c9ddebe) |

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
