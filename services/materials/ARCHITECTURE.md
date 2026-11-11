# Архитектура материалов

Domain → BCL; Application → Domain; Infrastructure → Application/Domain;
Api → Application и Infrastructure только в CompositionRoot.cs. Каждый слой —
отдельный проект. Core не содержит HTTP, JSON, SQL, DI, файловых операций или
библиотек PDF. Архитектурные xUnit-тесты проверяют реальные assembly references,
project references, public mutation и imports API.

## Агрегаты и ответственность

- AnswerBlank — неизменяемая регистрация с уникальными ID/номером/QR, точной игрой,
  командой, taskId/taskNumber и модификатором. EnsurePrintableIn защищает scope.
- TeamKit — выделенные команде бланки, количества и общие карточки. Домен требует
  ровно настроенное число уникальных бланков. Один комплект на игру/команду защищён
  constraint. При подготовке бланки циклически распределяются по назначенным задачам
  по порядку до настроенного количества; это стратегия подготовки, не выбор проверяющего.
- PrintJob — неизменяемый PrintSnapshot и инкапсулированные переходы
  Pending → Running → Completed/Failed. Аренда обработки восстанавливает очередь
  после падения. После передачи задания старый token не может записать PDF.
- GameMaterials — capabilities и точные назначения задач от внешнего владельца.
  Правила не зависят от названия игры. MaterialTemplate допускает только
  декларативные поля публичной print projection и требует идентифицирующий QR.

У PrintableTask нет поля эталона. HTTP-adapter задач не считывает referenceAnswer.
Условие сохраняется под точным ID в snapshot. Исправление эталона/запрет замены
задач во время игры обеспечивает внешний владелец; материалы не выбирают «последний ID».

## SOLID

| Принцип | Конкретное решение |
|---|---|
| SRP | PrepareKit, IssueBlanks, CreatePrintJob, ProcessPrintJobs и PublishOutbox — отдельные use cases. Каждый SQL repository отвечает за свой агрегат. Renderer не ходит в HTTP/БД. |
| OCP | Новые имена игр/шаблоны задаются данными. ITemplateRepository заменяется file/HTTP adapters; IPdfRenderer заменяется при новом формате. Ввод новых визуальных примитивов может требовать нового renderer/формата, не ветки по имени игры. |
| LSP | Один repository contract suite исполняется против memory test adapter и настоящей PostgreSQL: lookup, metadata, rollback/outbox, snapshot/artifact. Один template contract проверяет file/HTTP adapters. |
| ISP | Узкие порты для конфигурации игры, допуска, условия задачи, шаблонов, подписи, PDF, часов и событий. Repositories не содержат network/PDF методов. |
| DIP | Application зависит от портов/доменной модели. Реализации передаются конструкторами в composition root. Нет service locator в core или API handlers. |

Интерфейсы выделены для substitutable boundaries; use cases не обёрнуты интерфейсом
на каждый класс. IMaterialsSession группирует repositories одной транзакции:
explicit unit of work, не универсальный dispatcher. PostgreSQL имеет отдельные таблицы
агрегатов, связей, PDF, replay и outbox. JSON хранит закрытый сложный snapshot,
typed event payload и список неизменяемых replay references; не заменяет всю БД
бизнес-словарём.

## Надёжность

Команда сначала проверяет capabilities, затем committed idempotency результат
по user/operation/key. Повтор не зависит от нового допуска, смены шаблона или
недоступности gateways. После внешних чтений в короткой BEGIN IMMEDIATE-транзакции
replay проверяется повторно, закрывая race. Результат восстанавливается по
неизменяемым ID. Повтор создания PrintJob возвращает исходное pending-представление;
текущий статус читается отдельным query.

Запись бланков/комплекта, replay и outbox атомарна. Уникальность ID, QR, номера,
комплекта защищена SQL constraints. Общего process-wide lock нет. HTTP и PDF CPU
выполняются вне SQL transactions. Готовые BLOB, статус и событие сохраняются
атомарно. Outbox помечается delivered только после подтверждения; отказ оставляет
событие pending с прежним eventId. Crash/несколько экземпляров могут повторно
публиковать событие: потребитель выполняет dedup, как предусмотрено контрактом.

PostgreSQL WAL сохраняет registrations, replay, snapshots, PDFs и pending events
после рестарта. Running jobs возвращаются после истечения технической аренды
обработки. Это не назначение жюри и не новый цикл игры. Схема имеет версию 1;
дальнейшие изменения требуют миграций.

## QR, шаблоны, PDF

Versioned QR содержит ID/номер бланка, игру/команду, taskId/taskNumber и модификатор.
HMAC-SHA256 использует constant-time сравнение и полное сопоставление с реестром.
Ключ — случайные 32 bytes в persisted PostgreSQL либо внешняя конфигурация, не тестовая
константа. БД должна быть защищена: persisted secret не является шифрованием.
Несогласованная смена настроенного ключа отклоняется; нужна явная миграция.

Шаблоны принадлежат сервису: FileTemplateRepository читает собственный каталог.
HTTPTemplateRepository — заменяемый adapter, включая контрактные тесты, а не
обязательный отдельный бизнес-сервис. Созданный PrintJob фиксирует содержимое
шаблона, его параметры и выбранные материалы; последующие изменения не влияют.

QRCoder строит реальную матрицу QR с quiet zone; PDFsharp рисует её векторно.
Unicode-шрифт встраивается. PdfPig независимо извлекает кириллицу/длинные условия.
scripts/verify-pdf.mjs растрирует настоящий HTTP-artifact Poppler и декодирует
QR независимым jsQR. PDFsharp/QRCoder — MIT; PdfPig в тестах — Apache-2.0.
Кириллический TrueType-шрифт выбирается установленным/configured, не копируется
из Windows в репозиторий. declarative-v1 — Unicode plain text, не LaTeX.

Runtime__MaxBlanksPerBatch — настраиваемый технический лимит партии HTTP.
Настроенные внешние identity/TLS/service credentials необходимы для production;
тестовые токены используются только launcher/fixtures. Никаких тестовых identity
или псевдо-QR в production реализации нет.
