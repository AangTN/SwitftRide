# Repository Guidelines

## Project Structure & Modules

`src/` contains the .NET 10 backend. Each service is split into `Domain`, `Application`, `Infrastructure`, and `Api`; `ApiGateway` hosts the YARP gateway, while `Contracts` and `ServiceDefaults` contain shared contracts and infrastructure defaults. Keep business entities and database ownership within their service. `tests/` contains xUnit projects for Auth, Gateway, and Matching. The Flutter rider and driver apps share `apps/mobile/lib/` and `apps/mobile/test/`. Docker and database initialization live in `deploy/`; local scripts are in `scripts/`; architecture and migration notes are in `docs/`.

## Build, Test & Local Development

- `dotnet build SwiftRide.sln` — build all .NET projects (warnings are errors).
- `dotnet test SwiftRide.sln` — run the xUnit suite; PostgreSQL concurrency cases require the setup described in `README.md`.
- `flutter test` and `flutter analyze` from `apps/mobile/` — run mobile tests and static analysis.
- `pwsh -File scripts/Initialize-Dev.ps1` then `docker compose up -d --build --wait --wait-timeout 300` — initialize secrets and start the local stack.
- `pwsh -File scripts/Start-Mobile.ps1` — build and serve the two web app variants locally.

## Style & Naming

Follow the existing C# conventions: four-space indentation, nullable reference types, `PascalCase` for types/methods, and `camelCase` for locals/parameters. Preserve the service layer boundaries and keep EF migrations with their owning Infrastructure project. Flutter/Dart follows `flutter_lints`; use `lower_snake_case.dart` filenames and `UpperCamelCase` types. Use descriptive test names (for example, `AuthFlowTests.cs` and `auth_layout_test.dart`).

## Testing

Add xUnit tests under the corresponding `tests/<Area>.Tests/` project and Flutter tests under `apps/mobile/test/`. Prefer focused tests for behavior and API integration. Run the full solution tests and mobile checks for changes in those areas. For PostgreSQL-specific coverage, use `pwsh -File scripts/Test-Postgres.ps1`; use `pwsh -File scripts/Smoke-Test.ps1` to exercise a running stack through the gateway.

## Commits & Pull Requests

The repository has no commit history yet, so no established commit message pattern can be inferred. Use concise imperative subjects (for example, `Add rider profile validation`). PRs should explain the behavior change, list relevant test commands/results, link an issue when available, and include screenshots for visible mobile changes. Call out migrations, configuration changes, or development setup impacts.

## Configuration & Secrets

Use `.env.example` as the local configuration reference. Keep real `.env` values and `.secrets/` keys out of commits, and never include credentials or tokens in logs, issues, or PRs. Compose is configured for Development and is not a production deployment template.
