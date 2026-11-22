# AGENTS.md

Business rules and the endpoint list live in `Requirements.md`. Read it before adding or changing a use case. This file describes how the code is written.

## Stack
- .NET 11, ASP.NET Core Web API with **Controllers**
- Clean Architecture + CQRS
- **No MediatR/Mediator. No AutoMapper** (manual mapping only)
- FluentValidation, EF Core (SQLite), Scrutor, Serilog

## Layers (dependencies point inward only)
- `Domain`: entities, enums, domain exceptions. References nothing.
- `Application`: commands, queries, handlers, validators, mappings, interfaces (e.g. `IApplicationDbContext`). References Domain.
- `Infrastructure`: EF Core, migrations, external services. Implements Application interfaces.
- `WebAPI`: controllers, `Program.cs`. Composition root.

Handlers depend on interfaces, never on concrete `DbContext` or Infrastructure types.

## Data Access (`IApplicationDbContext`)
- Reads go through the `IQueryable<TEntity>` properties (`Students`, `Courses`, `Enrollments`).
- Writes go through explicit methods on the interface (`AddStudent`, `AddCourse`, `RemoveEnrollment`, ...). When a new entity or write operation is needed, add a matching `Add[Entity]` / `Remove[Entity]` method to the interface and implement it in `SchoolDbContext`. Do not expose `DbSet<T>` or the `DbContext` itself.
- Database-provider details (e.g. `SqliteException` error codes for unique or primary-key violations) are handled inside Infrastructure and surfaced to Application as a `bool` or a plain return value, as `TrySaveEnrollmentAsync` does. Application code never references provider-specific types.

## EF Core Entity Configuration
- Each persisted entity must define its EF Core mapping in `[EntityName]Configuration.cs` under `Infrastructure/Persistence/Configurations` using `IEntityTypeConfiguration<TEntity>`.
- `OnModelCreating` must apply these configurations and call `SchoolSeed.Configure(modelBuilder)`.
- Column lengths come from the entity's constants (e.g. `Course.MaximumTitleLength`), never from literal numbers.

## Migrations
- Create migrations with the EF Core CLI only, from the repository root:
  `dotnet ef migrations add <Name> --project Infrastructure --startup-project WebAPI --output-dir Persistence/Migrations`
- Do not hand-write new migrations and do not edit a migration that has already been applied. Change the model, then add a new migration.
- Seed data lives in `SchoolSeed`. Changing it requires a new migration.
- `20261120061000_InitialSchool` was written by hand and has no `.Designer.cs` file. This is a known exception; do not copy that style.
- The database is migrated automatically at startup (`MigrateDatabaseAsync` in `Program.cs`).

## Application Structure
```
Application/
  Common/
    Abstractions/   (ICommandHandler, IQueryHandler, IApplicationDbContext)
    Decorators/     (ValidationDecorator, LoggingDecorator)
    Pagination/     (Pagination, PageQueryValidator)
    Results/        (Result, Error, PagedResult)
  [Module]/
    [Entity]Response.cs        (only when shared by 2+ use cases)
    [Entity]Mappings.cs        (only when a projection is shared by 2+ use cases)
    Commands/[UseCaseName]/
    Queries/[UseCaseName]/
  Shared/
    Errors/[Module]Errors.cs
  DependencyInjection.cs
```

## File Naming
- `[UseCaseName]Command.cs` / `[UseCaseName]Query.cs`
- `[UseCaseName]CommandHandler.cs` / `[UseCaseName]QueryHandler.cs`
- `[UseCaseName]CommandValidator.cs` / `[UseCaseName]QueryValidator.cs`
- `[ResponseName]Response.cs`: inside the use-case folder when only that use case returns it; in the module root when 2+ use cases return it.
- `[UseCaseName]Mapping.cs` only if the use case has 2+ mappings; otherwise map inside the handler.

## Rules
- Commands change state and return `Result<T>` when there is a response payload (e.g. `Result<Guid>` for a created entity ID), or `Result` when there is no payload. Queries are read-only and return `Result<TResponse>`.
- Commands and queries are plain records. No marker interfaces.
- Handlers: `internal sealed`, primary constructor, return `Result` or `Result<T>` according to the command or query contract, always pass `CancellationToken`.
- For expected failures (NotFound, Validation, Conflict), return a failed `Result` or `Result<T>` containing an `Error`.
- Use `ValidationDecorator` to validate commands and queries that have a registered validator. Use a logging decorator for handler execution. Follow the Transactions section for transaction handling.
- DI: `Application` exposes `AddApplication()`, and `Infrastructure` exposes `AddInfrastructure()`. In `AddApplication()`, use Scrutor to register command and query handlers as scoped services under their handler interfaces. Set `publicOnly: false` in `AddClasses` to include `internal sealed` handlers.

## Mapping
- Commands: build entities through domain factory methods (`Course.Create(...)`), not object initializers. Use a `ToEntity` extension only when the factory call needs non-trivial argument mapping.
- Queries: use `AsNoTracking()` and project directly to the response with `Select`. Never load the entity and map it in memory.
- When the same projection is used by 2+ handlers, define it once as a static `Expression<Func<TEntity, TResponse>>` in `[Module]/[Entity]Mappings.cs` (e.g. `CourseMappings.ToResponse`) and pass it to `Select`. Do not duplicate the `new XResponse(...)` expression.

## Validation
- Every command has a validator.
- Every query that takes an ID or is paginated has a validator. IDs are checked with `NotEmpty()` (route constraint `{id:guid}` still accepts `Guid.Empty`).
- Paginated query validators inherit from `PageQueryValidator<T>`.
- Validators mirror domain constraints using the entity's constants (`Student.MaximumFullNameLength`), so invalid user input is rejected before reaching the domain.
- Domain factory methods still guard their invariants with `ArgumentException` / `ArgumentOutOfRangeException`. These only fire on programming errors and end up in the global exception handler as a 500.

## Errors
- Each module has an `internal static class [Module]Errors` in `Application/Shared/Errors`, with `public static readonly Error` fields.
- Error codes use the form `[Module].[Reason]` with a plural module name, e.g. `Courses.NotFound`, `Enrollments.CourseFull`.
- `ErrorType` maps to HTTP status in `ResultExtensions.ToProblemDetails`: `Validation` → 400, `NotFound` → 404, `Conflict` → 409. The error code is returned in the `code` extension of the `ProblemDetails`.

## Controllers
- No business logic. Inject handlers per action with `[FromServices]`, convert a failed `Result` with `result.ToProblemDetails(HttpContext)`.
- Request body:
  - When the body maps 1:1 to the command, bind the command record directly (e.g. `CreateCourseCommand`).
  - When the command also needs route values, define a `[Action]Request` record at the bottom of the controller file (e.g. `EnrollStudentRequest`) and build the command in the action.
- Responses:
  - Create command returning an ID → `201 Created` via `CreatedAtAction(nameof(Get), new { id }, id)`.
  - Command without payload → `204 No Content`.
  - Query → `200 OK` with the response.
- Every action has `[EndpointSummary]` and a `[ProducesResponseType]` for the success status and for every error status it can return (`ValidationProblemDetails` for 400, `ProblemDetails` for 404/409).
- List endpoints take `page` and `pageSize` from the query string with defaults from `Pagination`.

## Cross-Cutting Rules

**Global error handling**
- Unexpected exceptions are handled by a single `IExceptionHandler` registered in WebAPI that returns a uniform `ProblemDetails`.
- Never return stack traces or exception messages to the client. Log the exception server-side.

**Transactions**
- One `SaveChangesAsync` per command is the default and is already atomic.
- If a command needs multiple saves/operations in one transaction, open an explicit transaction inside that handler. No global transaction decorator.

**Concurrency**
- Use a concurrency token (`RowVersion`, a `Guid` regenerated by the entity on every change) on entities that support concurrent edits.
- Include `rowVersion` in read responses and require it in update requests.
- Use the client-supplied `rowVersion` as the expected version when saving the update.
- Return a `Conflict` error (HTTP 409) if the version is stale or EF Core raises `DbUpdateConcurrencyException`.
- Operations that change an entity as a side effect (e.g. enrolling changes `Course.EnrolledCount`) do not take a client `rowVersion`, but still catch `DbUpdateConcurrencyException` and return a `Conflict` error.

**Pagination and filtering**
- Every list query returns `Result<PagedResult<T>>` (items, page, pageSize, totalCount) using `ToPageAsync`.
- Page size has a default (`Pagination.DefaultPageSize`) and a hard maximum (`Pagination.MaximumPageSize`) enforced in the query validator.
- Every list query has a deterministic order: a business field first, then `Id` as a tie-breaker.
- No endpoint supports client-side filtering or sorting yet. When the first one is needed, design one shared pattern in `Application/Common/Pagination`, document it here, and only then use it. Do not add ad-hoc filter parameters to a single endpoint.

**Domain entities**
- Entities are not anemic: business rules live inside them (`Order.Create(...)`, `order.Cancel()`), with private setters and a private parameterless constructor for EF Core.
- Handlers orchestrate (load, call domain methods, save). They do not contain business rules.
- Checks that need the database (an entity exists, a duplicate exists) are done in the handler before calling the domain method, and return the matching `Error`.

**Observability**
- Use **Serilog** for structured logging (inject `ILogger<T>`, no static `Log` calls in application code).
- Every request has a correlation id set by `CorrelationIdMiddleware` from `HttpContext.TraceIdentifier`. It is server-generated (an incoming header is not trusted), returned in the `X-Correlation-Id` response header, added to the log scope, and included as `correlationId` in every `ProblemDetails`.
- Expose health check endpoints (`/health`) from the start.

## Testing
- There is no test project yet. Until one exists, verify changes with `dotnet build CleanArchitectureTemplate.slnx` and by calling the affected endpoints.

## New Use Case Checklist
1. Check `Requirements.md` for the business rules; add the new rule there if it is missing.
2. Create the folder under `[Module]/Commands` or `[Module]/Queries`.
3. Add the command or query record, its handler, and its validator (see Validation). Add a response record and a shared projection where the Mapping section requires it.
4. Add errors to `[Module]Errors.cs` if needed.
5. If the model changed, add a migration with the EF Core CLI (see Migrations).
6. Add the controller action following the Controllers section. No manual DI registration needed.
7. Add the endpoint to `Requirements.md`.
8. Run `dotnet build CleanArchitectureTemplate.slnx` from the repository root.
