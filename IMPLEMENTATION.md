# Implementation standard: Sapientum services

All three implementations use C# 12 / .NET 8 / ASP.NET Core, SQLite via
Microsoft.Data.Sqlite, and xUnit. Existing Node acceptance tests remain the
language-neutral contract. Implement their real behavior without weakening tests.

## Dependency rule

- src/Sapientum.<Service>.Domain: BCL only, zero project/package references.
  Rich aggregates/value objects own invariants. No HTTP status, JSON, SQL, DI,
  filesystem, clocks obtained through globals, or network operations.
- src/Sapientum.<Service>.Application: references Domain only; owns small,
  capability-specific ports, commands/queries and orchestration. No frameworks,
  SQL, HttpClient, JsonElement/dynamic business state, service locator, or globals.
- src/Sapientum.<Service>.Infrastructure: references Domain/Application; implements
  SQLite repositories/transactions, external HTTP gateways, clock, outbox and
  QR/PDF/file adapters. Serialization models remain adapter-local.
- src/Sapientum.<Service>.Api: HTTP binding, authorization, error mapping and
  composition root. Business decisions belong inward, not to endpoint handlers.
  Infrastructure reference is permitted only for composition.
- tests/Sapientum.<Service>.Tests: xUnit domain/application/infrastructure tests
  and architecture checks for actual project/assembly dependency boundaries.

Choose explicit aggregate repositories and transaction boundaries; never a giant
dictionary/JsonObject store, a universal action dispatcher, or a service with all
domain behavior in one method. Interfaces describe real substitutable boundaries;
do not add interface-per-class ceremony. Constructor injection, cohesive types,
private domain mutation, immutable inputs/read models, cancellation propagation.
No catch-all success, no unused scaffolding, no TODO domain behavior.

## Reliability and extensibility

Persist aggregates, idempotency results and pending events in SQLite. Atomically
save aggregate changes and outgoing events; publish from a retrying outbox with
stable event IDs. Handle uniqueness/concurrent commands through database
transactions/constraints. Avoid a single process-wide lock as the consistency
mechanism. Restart must retain state and pending events. External calls must not
block SQL transactions while possible. Reject unknown request fields, enforce
role unions on server, retain error codes defined by the contract. Auth is a
configured introspection adapter, never hardcoded test-token logic.

Game type is an identifier/configuration. Domain must not switch on names of
current games. Do not recalculate scores. Immutable task ID never follows latest
catalog records. Reference answers do not enter public material/intake models.

## Uniform configuration and launch

Use Dependencies__BaseUrl as the optional default HTTP dependency URL for local
contract tests. Allow overrides with Dependencies__IdentityBaseUrl,
GamesBaseUrl, TeamsBaseUrl, TasksBaseUrl, MaterialsBaseUrl, ImagesBaseUrl,
EventsBaseUrl and TemplatesBaseUrl as relevant. These keys are under Dependencies.
Storage__ConnectionString selects SQLite; default persistent .data/service.db.
Optional Dependencies__ClockBaseUrl is enabled only in ContractTests environment;
otherwise use server system clock. Runtime__LeaseSeconds controls review TTL.
HTTP URLs use ASPNETCORE_URLS. Default example ports: materials 8081, intake 8082,
review 8083. GET /health supports test readiness without credentials.

Provide scripts/run-acceptance.mjs and npm run test:service. The launcher builds
the real service, starts an isolated fixture process and real service with fresh
temp SQLite, runs unchanged acceptance tests, and cleans up child processes in
finally. It must reject startup failures and leave no hidden servers. Keep normal
npm test checks. Provide dotnet test and architecture tests in CI, plus actual
contract acceptance against this real process.

Document architecture/dependency direction and SOLID decisions, configuration,
run/test instructions and remaining concrete deployment considerations.
Do not claim deployment-ready security if an external provider is still needed.

## Delivery

Work only in the assigned worktree; commit implementation there, never switch
or mutate branches in another checkout. Do not push; root will review and publish
all three updated contract branches. Report head SHA, build/test and acceptance
counts, major design choices and any known limitations.
