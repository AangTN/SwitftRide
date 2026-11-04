# Repository Guidelines

## Project Structure & Modules

`src/` contains the .NET 10 backend. Each service is split into `Domain`, `Application`, `Infrastructure`, and `Api`; `ApiGateway` hosts the YARP gateway, while `Contracts` and `ServiceDefaults` contain shared contracts and infrastructure defaults. Keep business entities and database ownership within their service. `tests/` contains xUnit projects for Auth, Gateway, Trip, Matching, and Payment. The Flutter rider and driver apps share `apps/mobile/lib/` and `apps/mobile/test/`. Docker and database initialization live in `deploy/`; local scripts are in `scripts/`; architecture and migration notes are in `docs/`.

## Build, Test & Local Development

- `dotnet build SwiftRide.sln` — build all .NET projects (warnings are errors).
- `dotnet test SwiftRide.sln` — run the xUnit suite. PostgreSQL, MongoDB, and Redis integration tests require the isolated dependencies described in [README.md](README.md#kiểm-thử).
- `flutter test` and `flutter analyze` from `apps/mobile/` — run mobile tests and static analysis.
- `pwsh -File scripts/Initialize-Dev.ps1` then `docker compose up -d --build --wait --wait-timeout 300` — initialize secrets and start the local stack.
- `pwsh -File scripts/Start-Mobile.ps1` — build and serve the two web app variants locally.

## Service Boundaries

- Trip owns the trip state machine, proposals, confirmed fares, and SignalR. Preserve the 90-second rider deadline, the 30-second driver deadline, and the limit of three proposals unless the task changes those rules. Rider timeout cancels the trip. Rider rejection, driver rejection, and driver timeout can advance to the next proposal.
- Matching owns driver availability and GPS in MongoDB and the route cache in Redis. Keep polyline and pickup ETA in Matching. Trip accesses routes through the authorized API and message contracts. Keep confirmed fares and proposal pricing snapshots in TripDB.
- Keep Trip and Payment outbox records in the same database transaction as the business change. Preserve idempotency and verify trip, proposal, and driver IDs before processing delayed messages.
- Flutter apps call the Gateway. Use `/api/trips/realtime` for SignalR, and reload the snapshot and route after reconnecting. The demo payment flow uses the internal wallet. Keep payment credentials out of app and service payloads.

## Style & Naming

Follow the existing C# conventions: four-space indentation, nullable reference types, `PascalCase` for types/methods, and `camelCase` for locals/parameters. Preserve the service layer boundaries and keep EF migrations with their owning Infrastructure project. Flutter/Dart follows `flutter_lints`; use `lower_snake_case.dart` filenames and `UpperCamelCase` types. Use descriptive test names (for example, `AuthFlowTests.cs` and `auth_layout_test.dart`).

## Testing

Add xUnit tests under the corresponding `tests/<Area>.Tests/` project and Flutter tests under `apps/mobile/test/`. Prefer focused tests for behavior and API integration. Run the full solution tests and mobile checks for changes in those areas. Use `pwsh -File scripts/Test-Postgres.ps1` for Auth on PostgreSQL. Use `SWIFTRIDE_TEST_MONGO` and `SWIFTRIDE_TEST_REDIS` with isolated instances for Matching route and cache tests. Use `pwsh -File scripts/Smoke-Test.ps1` to verify readiness and Auth through the Gateway.

## Commits & Pull Requests

Use concise imperative subjects (for example, `Add rider profile validation`). PRs should explain the behavior change, list relevant test commands/results, link an issue when available, and include screenshots for visible mobile changes. Call out migrations, configuration changes, or development setup impacts.

## Configuration & Secrets

Use `.env.example` as the local configuration reference. Keep real `.env` values and `.secrets/` keys out of commits, and never include credentials or tokens in logs, issues, or PRs. Keep `GOONG_API_KEY` on the Matching backend and use `GOONG_MAPTILES_KEY` for the Flutter map build. Compose is configured for Development and the demo deployment.
