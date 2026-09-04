# Repository Guidelines

## Project Structure & Module Organization

`OrleansGameService.slnx` groups the .NET 10 projects. Keep cross-project dependencies flowing inward:

- `src/GameServer.Contracts/` contains public DTOs, Grain interfaces, and the versioned realtime protocol.
- `src/GameServer.Domain/` holds deterministic gameplay rules and attribute calculations, without infrastructure concerns.
- `src/GameServer.Grains/` implements Orleans account, character, zone, and monster stateful behavior.
- `src/GameServer.Infrastructure/` provides EF Core persistence, Redis caching, content loading, and DI registration.
- `src/GameServer.Gateway/` is the ASP.NET Core HTTP/WebSocket host; its `Content/game-content.v1.json` is the initial content package.
- `tests/GameServer.Tests/` contains xUnit tests. Place tests near the behavior they exercise, not near deployment code.

## Build, Test, and Development Commands

Run these from the repository root:

```powershell
dotnet restore OrleansGameService.slnx        # restore packages
dotnet build OrleansGameService.slnx          # compile all projects
dotnet test OrleansGameService.slnx           # run xUnit tests
dotnet run --project src/GameServer.Gateway   # run locally with Development settings
docker compose up --build                     # run Gateway, PostgreSQL, Redis, and Orleans schema setup
```

Use Docker Compose when validating PostgreSQL/Redis or production-style Orleans persistence. Never commit generated `bin/`, `obj/`, `.dotnet-cli/`, or `TestResults/` files.

## Coding Style & Naming Conventions

Use C# with nullable reference types enabled and implicit usings. Follow the existing four-space indentation, file-scoped namespaces, PascalCase for public types/members, camelCase for locals and parameters, and `I`-prefixed interfaces. Keep domain rules pure where possible; put durable Grain state in `State/` and persist changes with `WriteStateAsync()`. Preserve protocol compatibility: add versioned MessagePack contracts rather than changing an existing wire shape casually.

## Testing Guidelines

Tests use xUnit (`[Fact]`) and Coverlet collection. Name test classes after the unit under test and use behavior-focused method names, e.g. `Movement_rejects_teleport`. Add focused tests for gameplay validation, serialization, and Grain-side idempotency when changing them. Run `dotnet test OrleansGameService.slnx` before opening a PR; there is no repository-wide coverage threshold configured.

## Commit & Pull Request Guidelines

This checkout has no Git history to infer an established message format. Use short, imperative, scoped subjects such as `Add quest reward idempotency`. Keep commits cohesive. PRs should describe the gameplay/API impact, list validation commands, link the relevant issue, and include request/response examples or screenshots for gateway/UI-visible changes. Call out content-package, schema, environment-variable, or protocol-version changes explicitly.

## Security & Configuration

Do not commit credentials or production JWT signing keys. Supply `Jwt__SigningKey` and connection strings through environment variables in production; the Compose values are local-development defaults only.
