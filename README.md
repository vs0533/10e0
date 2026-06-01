# 10E0 (TenE0)

Next-generation enterprise low-code framework.

**Target**: .NET 10 / C# 14

## Architecture

- Clean Architecture + DDD + CQRS (custom dispatcher, no MediatR)
- EF Core 10 + `IDbContextFactory` scoped factory pattern
- Outbox pattern for domain events
- Pipeline behaviors: Logging → Transaction → Permission → Handler
- Named query filters for row-level security

## Getting Started

```bash
dotnet restore 10e0.sln
dotnet build 10e0.sln
dotnet run --project src/10E0.Api
```

## Project Structure

```
src/
├── 10E0.Api/    — HTTP API layer (Minimal API, demo entry point)
└── 10E0.Core/   — Framework core (NuGet package: TenE0.Core)

tests/
├── 10E0.Api.Tests/
└── 10E0.Core.Tests/
```

## NuGet

```bash
dotnet add package TenE0.Core
```

## License

MIT
