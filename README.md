# OperationGuard

OperationGuard is a .NET library for coordinating durable, idempotent HTTP and message operations across retries and concurrent deliveries. It records a logical operation's state in SQL Server or PostgreSQL, rejects unsafe key reuse, and can replay a bounded HTTP response without claiming universal exactly-once execution.

## Status

- V1 release candidate: `1.0.0` has not been published.
- Supported target frameworks: .NET 8 (`net8.0`) and .NET 10 (`net10.0`).
- SQL Server and PostgreSQL are the V1 production stores. The in-memory store is only for tests and local development.
- Publication is blocked until the repository owner selects and adds a license.

## What This Library Does

- Coordinates callers around the durable identity `Scope + OperationName + IdempotencyKey`.
- Ensures at most one current owner can acquire a logical operation.
- Detects reuse of an identity with a different request fingerprint.
- Records `InProgress`, `Completed`, and `Indeterminate` outcomes.
- Replays completed HTTP outcomes within configured body and header limits.
- Lets relational business work and guard state share the same local database transaction.
- Supports broker-neutral message processing without coupling the core package to Kafka or RabbitMQ.

## What This Library Does Not Do

- It does not provide universal exactly-once execution.
- It cannot atomically commit a remote payment, email, blockchain transaction, broker acknowledgement, or another external side effect with its database record.
- It does not replace authentication, authorization, reconciliation, or rate limiting.
- It does not infer tenant scope from an untrusted header.
- It does not automatically canonicalize JSON or retry an `Indeterminate` operation.
- V1 does not include a production Redis provider, broker adapter, distributed transaction coordinator, or indefinite duplicate-request waiting.

## Installation

Install the integration package, one production provider, and that database's ADO.NET driver:

```shell
dotnet add package OperationGuard.AspNetCore --version 1.0.0
dotnet add package OperationGuard.PostgreSql --version 1.0.0
dotnet add package Npgsql
```

or:

```shell
dotnet add package OperationGuard.AspNetCore --version 1.0.0
dotnet add package OperationGuard.SqlServer --version 1.0.0
dotnet add package Microsoft.Data.SqlClient
```

`OperationGuard.AspNetCore`, `OperationGuard.SqlServer`, and `OperationGuard.PostgreSql` depend on `OperationGuard.Core`. Install `OperationGuard.Core` directly for non-HTTP coordination and message operations.

These package IDs are release candidates and are not yet published. The final availability and ownership check must be repeated immediately before publication.

## Quick Start

This minimal API protects one single-tenant endpoint with PostgreSQL. The scope is fixed server-side; a multi-tenant application should resolve it from trusted authenticated identity instead.

```csharp
using Npgsql;
using OperationGuard.AspNetCore.Extensions;
using OperationGuard.AspNetCore.Registration;
using OperationGuard.PostgreSql.Registration;
using OperationGuard.PostgreSql.Stores;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("OperationGuard")
    ?? throw new InvalidOperationException("Missing OperationGuard connection string.");

builder.Services.AddOperationGuard(options =>
{
    options.ScopeResolver = static (_, _) => ValueTask.FromResult("orders-api");
});
builder.Services.AddPostgreSqlOperationGuardStore(
    () => new NpgsqlConnection(connectionString));

var app = builder.Build();

var store = app.Services.GetRequiredService<PostgreSqlOperationStore>();
await store.EnsureCreatedAsync();

app.UseOperationGuard();

app.MapPost("/orders", (CreateOrder request) =>
        Results.Created($"/orders/{request.OrderId}", request))
    .RequireOperationGuard("Orders.Create");

app.Run();

internal sealed record CreateOrder(Guid OrderId, decimal Amount);
```

For applications with authentication and authorization, run those middleware components before `UseOperationGuard()`. This allows `ScopeResolver` to derive tenant or partner scope from the application's trusted identity context before the operation is reserved.

The equivalent SQL Server registration is:

```csharp
using Microsoft.Data.SqlClient;
using OperationGuard.SqlServer.Registration;

builder.Services.AddSqlServerOperationGuardStore(
    () => new SqlConnection(connectionString));
```

Resolve `SqlServerOperationStore` and call `EnsureCreatedAsync()` during controlled database setup in the same way. Both providers use fixed V1 schema/table names; production applications should coordinate schema creation permissions and rollout policy rather than granting unnecessary DDL rights permanently.

## Core Concepts

### Operation identity

An idempotency key alone is not an operation identity. OperationGuard uses all three exact, ordinal components:

```text
Scope + OperationName + IdempotencyKey
```

- `Scope` separates applications, tenants, or partners. A single-tenant application can use a fixed value such as `global` or `orders-api`.
- `OperationName` is a stable business name such as `Payments.Create`; do not derive it from a route that may be renamed.
- `IdempotencyKey` is opaque, non-empty, rejects control characters, and is limited to 255 characters by default.

Do not use a raw user ID as the only multi-tenant scope, and do not trust a client-provided tenant header unless the application has authenticated and authorized it.

### State model

- `InProgress`: an owner reserved the operation and may be executing it.
- `Completed`: the terminal outcome was durably recorded and duplicates must not execute the handler again during retention.
- `Indeterminate`: the library cannot safely prove whether side effects occurred. It is not retried automatically; the application must reconcile it and use the explicit recovery APIs deliberately.

### Guarantee/topology levels

1. **Transaction-coupled:** the business mutation and guard transition commit in the same local database transaction. This is the strongest V1 model for database-backed work.
2. **Coordinated but not transaction-coupled:** the guard is durable, but the business mutation commits elsewhere. A crash between the two commits can leave the outcome `Indeterminate`.
3. **External side effect:** the handler invokes a remote system. Safe retry also requires that downstream system's idempotency or reconciliation contract; OperationGuard alone cannot make the remote effect exactly once.

## Configuration

`AddOperationGuard` validates configuration at registration time.

| Setting | Default | Hard ceiling / behavior |
| --- | ---: | --- |
| `MaximumKeyLength` | 255 characters | 1,024 characters |
| `FingerprintBodyLimitBytes` | 1 MiB | 16 MiB |
| `ReplayBodyLimitBytes` | 64 KiB | 1 MiB |
| `MaximumReplayHeaderCount` | 32 | 128 |
| `MaximumReplayHeaderValueBytes` | 8 KiB | 64 KiB |
| `MaximumReplayHeadersTotalBytes` | 32 KiB | 256 KiB |
| `CompletedRetention` | 24 hours | Must be positive |
| `InProgressStaleAfter` | 15 minutes | Must be positive; expiry does not prove side effects did not occur |
| `FingerprintHeaders` | none | Explicit allowlist |
| `ReplayHeaders` | `Content-Type`, `ETag`, `Location` | Explicit allowlist; sensitive and hop-by-hop names are rejected |
| `ScopeResolver` | `global` | Replace with a trusted server-side resolver for multi-tenant systems |

The total replay-header limit must be at least the per-value limit. Header counts, individual UTF-8 values, and the serialized aggregate are all bounded. `Authorization`, `Cookie`, `Set-Cookie`, proxy-authentication headers, and hop-by-hop headers cannot be persisted for replay even if configured.

The default HTTP fingerprint uses the operation name, method, canonicalized query representation, content type, selected headers, and raw body bytes with SHA-256. JSON property reordering is therefore different by default. This conservative behavior can cause a safe conflict, but does not silently invent semantic equivalence. Implement and register `IRequestFingerprintProvider` only when the application can define a stable business-semantic fingerprint.

## Usage Scenarios

### Protected Minimal API or MVC endpoint

Call `UseOperationGuard()` before protected endpoints execute, then add `.RequireOperationGuard("Stable.OperationName")` to a Minimal API endpoint. MVC actions and controllers can use `[OperationGuard("Stable.OperationName")]`; both surfaces use the same endpoint metadata and middleware behavior.

The HTTP integration requires one non-empty `Idempotency-Key` request header. It buffers only up to the configured fingerprint-body limit and resets the request stream before calling the handler.

### Transaction-coupled payment-style operation

Do not wrap an endpoint in the ordinary HTTP middleware when the endpoint itself uses a provider transaction session: the middleware owns a separate transaction boundary. Instead, reserve the operation, perform the local business mutation, complete the guard record, and commit once:

```csharp
await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync(cancellationToken);
await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

var session = store.CreateSession(connection, transaction);
var begin = await session.TryBeginAsync(
    identity,
    fingerprint,
    timeProvider.GetUtcNow(),
    options.InProgressStaleAfter,
    cancellationToken);

if (begin.Kind == OperationBeginKind.Acquired)
{
    await InsertPaymentAsync(connection, transaction, cancellationToken);

    var completion = await session.CompleteAsync(
        identity,
        begin.OwnerToken!,
        replayResponse,
        replayBodyAvailable: true,
        responseDigest,
        timeProvider.GetUtcNow().Add(options.CompletedRetention),
        cancellationToken);

    if (completion != ConditionalWriteKind.Applied)
    {
        throw new InvalidOperationException($"Completion was rejected: {completion}.");
    }

    await transaction.CommitAsync(cancellationToken);
}
```

Real code must handle every `OperationBeginKind`: replay `Completed`, reject `FingerprintMismatch`, avoid re-executing `AlreadyInProgress` or `Indeterminate`, and execute only `Acquired`. The complete, compiled flow is in [`samples/PaymentApi`](samples/PaymentApi).

### Message operation

For a relational inbox-style consumer, use the same provider session inside the business database transaction. Use the broker's stable message ID as the idempotency key and fingerprint the stable business content. A completed record is a duplicate acknowledgement; a fingerprint mismatch or indeterminate result must not execute business work.

```csharp
var identity = new OperationIdentity(
    message.TenantScope,
    "AccountCredit.Apply",
    message.MessageId);

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync(cancellationToken);
await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
var session = store.CreateSession(connection, transaction);

var begin = await session.TryBeginAsync(
    identity, fingerprint, timeProvider.GetUtcNow(), leaseDuration, cancellationToken);

if (begin.Kind == OperationBeginKind.Acquired)
{
    await InsertBusinessMutationAsync(connection, transaction, message, cancellationToken);
    var completion = await session.CompleteAsync(
        identity,
        begin.OwnerToken!,
        response: null,
        replayBodyAvailable: false,
        responseDigest: null,
        timeProvider.GetUtcNow().Add(retention),
        cancellationToken);

    if (completion != ConditionalWriteKind.Applied)
    {
        throw new InvalidOperationException($"Completion was rejected: {completion}.");
    }

    await transaction.CommitAsync(cancellationToken);
}
```

The broker should acknowledge only after the local transaction succeeds. Broker acknowledgement is not part of that database transaction, so redelivery must still be expected. See the complete broker-neutral [`samples/MessageConsumer`](samples/MessageConsumer).

`MessageOperationExecutor` is also available for coordinated non-HTTP handlers, but its handler and guard completion are not automatically transaction-coupled. Use a provider transaction session when atomic local database coupling is required.

## Reliability / Safety Semantics

- The default policy is fail closed. If the HTTP guard store is unavailable, the middleware returns `503` and does not call the handler. Core/message store failures propagate rather than bypassing protection.
- A missing or invalid required key returns `400 Bad Request`.
- A duplicate with the same fingerprint while the first owner is active returns `409 Conflict`; V1 does not wait.
- Reusing the same identity with a different fingerprint returns `422 Unprocessable Content` and never runs the handler as a normal retry.
- A completed result is replayed only when its stored status, headers, and body pass the configured bounds and safety checks.
- If a completed response body exceeded the replay limit, the operation stays completed, duplicates do not execute it again, and HTTP returns a documented `409` replay-unavailable response.
- An unhandled or ambiguous failure attempts `InProgress -> Indeterminate`. The original exception is not converted into permission to retry.
- Lease expiry alone is not evidence that business effects did not happen. Recovery methods use recovery versions and owner tokens so stale owners cannot complete a newer claim.
- Cleanup removes only expired `Completed` records. Once a completed key is purged, the same identity can represent a new operation; retention must cover the promised client retry window.

For external side effects, pass the same stable idempotency identity downstream where supported and build reconciliation. Do not describe Level B or Level C deployments as exactly once.

## Observability

OperationGuard exposes `ActivitySource` and `Meter` instances under the instrumentation name `OperationGuard`. Current instruments include:

- `operationguard.http.requests`, tagged with a bounded result category;
- `operationguard.message.executions`, tagged with a bounded result category;
- the `message.execute` activity, tagged with operation name and a hash of scope.

Diagnostic logs identify operation names and use a scope hash where needed. Raw idempotency keys, request/response bodies, and raw scope values are not logged by default. Applications remain responsible for filtering their own handler logs and tracing baggage.

## Performance / Scaling Notes

- Correctness under hot-key contention takes priority over a headline throughput number; V1 makes no fixed throughput claim.
- Active duplicates receive `409` instead of consuming resources in an unbounded wait.
- HTTP request fingerprinting and response replay are memory-bounded by configuration.
- Each relational store command opens a connection through the supplied factory unless it is executed through a caller-owned transaction session. Size database pools and timeouts for expected contention.
- Cleanup is an explicit bounded batch operation; schedule `DeleteExpiredBatchAsync` according to retention and storage growth requirements.

The benchmark smoke project under `tests/OperationGuard.Benchmarks` is for regression checks, not a portable capacity promise. Measure the intended provider, schema, network, payload sizes, and contention profile in the deployment environment.

## Samples

- [`samples/PaymentApi`](samples/PaymentApi) demonstrates a minimal single-tenant payment-style API whose business row and guard completion share one PostgreSQL transaction. It deliberately contains no authentication system and no sample tests.
- [`samples/MessageConsumer`](samples/MessageConsumer) demonstrates broker-neutral duplicate recognition and a business mutation committed with guard completion in one PostgreSQL transaction. It contains no broker framework and no sample tests.

The samples are demonstrations, not application templates. They intentionally avoid unrelated product infrastructure.

## Limitations

- Only SQL Server and PostgreSQL are supported as production stores in V1.
- The in-memory store is not safe for multi-instance or durable production use.
- Provider schemas use fixed names and V1 does not ship a migration framework or background cleanup service.
- Raw-body HTTP fingerprinting does not provide semantic JSON equivalence.
- Response replay stores only selected safe headers and a bounded body.
- OperationGuard does not coordinate distributed transactions or make remote effects exactly once.
- Recovery of `Indeterminate` operations is application policy and reconciliation work.

## Versioning / Compatibility

V1 targets `net8.0` and `net10.0`. Package IDs, the three-state model, persisted schemas, identity semantics, and fail-closed defaults are compatibility-sensitive contracts. Review release notes and database rollout requirements before upgrading a production deployment.

## Contributing / Development

Restore, build, and run the non-infrastructure suite with:

```shell
dotnet restore OperationGuard.slnx
dotnet build OperationGuard.slnx --configuration Release --no-restore
dotnet test OperationGuard.slnx --configuration Release --no-build --no-restore
```

SQL Server and PostgreSQL integration suites require their documented test environment variables and real provider instances. Changes must preserve both target frameworks and the frozen V1 safety contracts.

## License

No license has been selected or granted in this repository. Package publication is blocked until the repository owner adds a license file and matching NuGet package metadata. Do not assume permission to redistribute this code merely because the source is visible.
