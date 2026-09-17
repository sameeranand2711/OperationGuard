# OperationGuard V1 — Frozen Source of Truth

> This file is copied from the Stage-0 Architecture Package and is authoritative for this agent pack.
> If this file conflicts with general agent habits, this file wins.

---

# OperationGuard V1 — Architecture Specification

## 1. Purpose

`OperationGuard` protects business operations from unintended duplicate execution caused by:

- HTTP retries;
- client timeouts;
- network ambiguity;
- repeated webhooks;
- duplicate message delivery;
- user double-submission;
- multiple application instances racing the same logical operation.

V1 targets **.NET 8 and .NET 10**.

The design is intentionally conservative for money-like or state-changing operations: when the guard cannot safely determine whether an operation may execute, it prefers **fail closed** over silently bypassing protection.

---

## 2. Core semantic decision

### 2.1 What OperationGuard guarantees

OperationGuard provides a durable coordination record for a logical operation identity:

```text
Scope + OperationName + IdempotencyKey
```

It can prevent concurrent duplicate execution and replay a completed outcome.

However, the strength of the final business guarantee depends on where the business side effect occurs.

### 2.2 Guarantee levels

#### Level A — Transaction-coupled

The protected business mutation and OperationGuard state transition participate in the **same local database transaction**.

This is the strongest supported V1 model for database-backed business changes.

Examples:

- create payment instruction row + complete idempotency record;
- update wallet command state + complete idempotency record;
- process message + insert inbox completion in the same database.

#### Level B — Coordinated but not transaction-coupled

The guard store is durable and distributed, but the protected business side effect commits elsewhere.

OperationGuard still prevents normal concurrency duplicates, but a crash can occur after the external side effect succeeds and before the guard record becomes completed.

The result may be **indeterminate**.

#### Level C — External side effect

The handler invokes a remote payment/provider API, email gateway, blockchain transaction or other external effect.

OperationGuard cannot create exactly-once semantics unless the downstream system also supports an idempotency/reconciliation contract.

**V1 must never describe Level B/C as exactly-once execution.**

---

## 3. Research basis

Key external facts influencing V1:

- RFC 9110 defines PUT, DELETE and safe methods as idempotent by HTTP semantics, while POST is not automatically idempotent.
- The IETF `Idempotency-Key` work reached draft `-07`, but the draft expired in April 2026 and is not an RFC. It remains useful guidance, not a standard we claim conformance to.
- Draft `-07` recommends:
  - `400` when a required key is missing;
  - `422` when the same key is reused with a different payload;
  - `409` when the same operation/key is still outstanding.
- Stripe demonstrates replay-oriented API semantics for duplicate idempotent requests.
- AWS emphasizes explicit client request identity rather than attempting to infer caller intent from parameters alone.

See `05-Research-Sources.md`.

---

## 4. Naming decision

Project/package family:

```text
OperationGuard.Core
OperationGuard.AspNetCore
OperationGuard.SqlServer
OperationGuard.PostgreSql
```

### Reversibility

**One-way door after package publication.**

A final package-ID availability check is required immediately before V1 publication. If the bare IDs cannot be owned, retain the project/namespace name and use an owner prefix.

---

## 5. Operation identity

A raw idempotency key is insufficient.

The durable identity is:

```text
Scope
OperationName
IdempotencyKey
```

### 5.1 Scope

Scope prevents cross-tenant/client collisions.

Default core concept:

```text
Scope = application-defined opaque string
```

For single-tenant applications a configured `"global"` scope is acceptable.

For multi-tenant iGaming applications, tenant/partner identity must be part of the scope.

Do **not** automatically use a user ID as the only scope. A retry may legitimately occur under a refreshed authentication session.

### 5.2 OperationName

Operation identity must be stable across deployments.

Recommended examples:

```text
Payments.Create
Wallet.Withdraw
Sportsbook.PlaceBet
ProviderCallback.Settle
```

ASP.NET integration should require an explicit operation name or a stable named endpoint. Raw path text is not the preferred identity because route refactoring can accidentally change semantics.

### 5.3 IdempotencyKey

HTTP integration uses:

```text
Idempotency-Key
```

V1 convention:

- opaque value;
- exact ordinal semantics;
- non-empty;
- control characters rejected;
- default maximum: 255 characters;
- hard library maximum: 1024 characters;
- no logging by default.

For storage indexing, providers may store a fixed SHA-256 identity hash while retaining enough original identity data to detect an astronomically unlikely hash collision.

---

## 6. State model

V1 states:

```text
InProgress
Completed
Indeterminate
```

Avoid a large generic state machine.

### 6.1 InProgress

The operation was successfully reserved and may be executing.

Stored metadata should include:

- identity hash/components;
- request fingerprint;
- created time;
- lease/owner token where applicable;
- expiry/retention metadata;
- attempt/recovery metadata where needed.

### 6.2 Completed

The operation reached a durable terminal outcome that may be replayed or acknowledged without re-executing the business handler.

A normal handler-returned error response can still be completed when re-execution would be unsafe.

### 6.3 Indeterminate

Used when the library cannot safely prove whether side effects occurred.

Examples:

- process crash/exception after a remote provider call;
- non-transactional business mutation may have committed;
- response persistence failed after business work.

**Default:** do not automatically re-execute an `Indeterminate` operation.

Recovery is application/provider policy, not guesswork.

---

## 7. State transitions

Nominal:

```text
Missing
  -> InProgress
  -> Completed
```

Ambiguous:

```text
InProgress
  -> Indeterminate
```

Recovery can later resolve:

```text
Indeterminate
  -> Completed
```

or explicitly authorize a retry through a recovery API/policy.

There is no automatic "delete the record on any exception" behavior in the safe default profile.

---

## 8. Concurrent request behavior

### Decision

When request B arrives while request A with the same identity/fingerprint is `InProgress`:

```text
HTTP default -> 409 Conflict
```

Do not wait by default.

### Why

- prevents thread/socket occupancy under retry storms;
- avoids hidden long-polling semantics;
- avoids thundering-herd replay waits;
- matches the behavior suggested by the expired IETF draft;
- simpler V1 recovery model.

A future version may add bounded wait/poll behavior without changing the core store contract.

---

## 9. Same key, different request

### Decision

When identity matches but the request fingerprint differs:

```text
HTTP default -> 422 Unprocessable Content
```

Return a problem-details response.

Never:

- execute the new payload;
- silently overwrite the fingerprint;
- treat it as a new operation.

This is a caller contract violation.

---

## 10. Missing key

For an endpoint explicitly protected by OperationGuard:

```text
missing Idempotency-Key -> 400 Bad Request
```

Protection must not silently become optional because the caller omitted the header.

Applications that want optional idempotency should opt into a separate explicit policy rather than using a required protected endpoint.

---

## 11. Fingerprinting

### 11.1 Algorithm

Default cryptographic digest:

```text
SHA-256
```

Fingerprint input must be versioned so future changes to fingerprint semantics do not reinterpret old records.

### 11.2 HTTP default fingerprint

Use a conservative representation of the request semantics:

- operation name;
- HTTP method;
- canonicalized query representation;
- selected configured headers if business-relevant;
- raw request-body bytes;
- content type/version marker.

### 11.3 JSON decision

V1 does **not** silently canonicalize JSON by property order.

Therefore these can be treated as different raw fingerprints:

```json
{"amount":100,"currency":"USD"}
```

```json
{"currency":"USD","amount":100}
```

This can create a **false conflict**, but not a false equivalence.

That is safer.

Applications requiring semantic JSON equivalence can supply:

```text
IRequestFingerprintProvider
```

A later version may add an explicit canonical-JSON provider.

### 11.4 Request-size safety

Do not buffer unbounded bodies.

ASP.NET integration must enforce a configurable fingerprintable-body limit.

Recommended defaults:

```text
Default fingerprint body limit: 1 MiB
Hard library ceiling: 16 MiB
```

Large-body endpoints should provide a custom fingerprint based on stable business fields rather than forcing the library to buffer huge payloads.

---

## 12. Response replay

### 12.1 Decision

V1 supports replay, but response persistence is bounded and security-aware.

Default:

```text
status + safe selected headers + body up to 64 KiB
```

Configurable ceiling:

```text
hard maximum 1 MiB
```

Do not persist by default:

- `Set-Cookie`;
- `Authorization`;
- authentication/session headers;
- arbitrary hop-by-hop headers;
- secrets.

### 12.2 Oversized response

If business execution is known to be complete but the replay body exceeds the configured storage limit:

- mark the operation `Completed`;
- store status/metadata and a response digest;
- mark replay body unavailable;
- on duplicate, **do not re-execute**;
- return a documented conflict/replay-unavailable problem response.

Safety wins over perfect response convenience.

### 12.3 Exceptions

An unhandled exception does not automatically prove no side effect occurred.

Default transition:

```text
InProgress -> Indeterminate
```

Applications with a known same-database transaction can configure transaction-coupled integration that safely rolls back both business state and the in-progress record.

---

## 13. Expiration and cleanup

### 13.1 Defaults

Suggested defaults:

```text
Completed retention: 24 hours
InProgress stale threshold: 15 minutes
Indeterminate: no automatic deletion by default
```

These are configurable.

### 13.2 Cleanup rules

Cleanup may delete only records that are eligible under explicit state-aware rules.

Never blindly delete:

- currently owned `InProgress` records;
- `Indeterminate` records;
- records still needed by a documented replay window.

When a completed key is purged, reuse after expiry is treated as a **new operation**. This must be documented to clients.

---

## 14. Leasing/recovery

A lease can detect abandoned workers, but **lease expiry does not prove that side effects did not happen**.

Therefore:

- lease expiry may permit automatic retry only for a policy explicitly marked safe for takeover;
- default HTTP policy does not automatically take over ambiguous operations;
- recovery APIs may allow an operator/application reconciler to resolve `Indeterminate`.

Recommended record fields:

```text
OwnerToken
LeaseExpiresAt
RecoveryVersion
```

Updates must be conditional on the owner/fencing token so a stale worker cannot complete a record claimed by a newer recovery process.

---

## 15. Storage architecture

Core contract must expose atomic operations, not CRUD.

Do **not** design:

```text
Get()
Insert()
Update()
```

as the primary correctness API.

Prefer semantic commands such as:

```text
TryBegin
ReadOutcome
Complete
MarkIndeterminate
ResolveIndeterminate
DeleteExpiredBatch
```

The provider owns the transaction/locking details needed to implement each command correctly.

---

## 16. V1 stores

### 16.1 Official production providers

V1:

```text
OperationGuard.SqlServer
OperationGuard.PostgreSql
```

### 16.2 Why no Redis production provider in V1

Redis can perform excellent atomic key coordination, but using a separate Redis store cannot atomically commit arbitrary relational business mutations with the guard record.

Shipping Redis first would encourage applications to infer stronger payment/wallet guarantees than the topology actually provides.

A Redis provider can be added later with an explicitly documented guarantee level.

### 16.3 In-memory provider

Allowed only for:

- tests;
- samples;
- local development.

It must visibly document that it is not safe for multi-instance production.

---

## 17. Transaction-coupled integration

For the strongest model, providers need a way to enlist guard completion with the business database transaction.

Support both:

- EF Core applications;
- non-EF applications using `DbConnection`/`DbTransaction`.

Do not require all consumers to use EF Core.

EF integration must test manual transaction behavior and provider specifics rather than assume all providers behave identically.

---

## 18. ASP.NET Core integration

Recommended surfaces:

```text
services.AddOperationGuard(...)
endpoint.RequireOperationGuard("Payments.Create")
```

Controller support may use an attribute backed by the same endpoint metadata.

Avoid two separate semantic implementations for Minimal APIs and MVC.

### Pipeline order

Authentication/authorization should normally run before the business operation is reserved, while the idempotency layer must run before side effects.

The integration package should document required middleware/filter ordering.

---

## 19. Message-operation integration

Core should support non-HTTP usage:

```text
OperationIdentity(
    scope,
    operationName,
    idempotencyKey/messageId,
    fingerprint?)
```

Message consumers usually do not need HTTP response replay.

They need:

- duplicate recognition;
- transactional completion with business mutation where possible;
- conflict detection if a message ID is reused with different content.

Do not couple the core library to Kafka or RabbitMQ.

---

## 20. Fail-closed policy

For a protected high-value operation:

```text
guard store unavailable
    -> do not silently run the handler
```

Return/propagate an availability error.

Applications may explicitly choose a weaker fail-open policy for low-risk operations, but it must never be the production default.

---

## 21. Threat model

### Abuse and accidental risks

- key-flood storage exhaustion;
- extremely long headers;
- malicious payload mismatch;
- cross-tenant key collision;
- raw-key injection into logs/metrics;
- response-body secret persistence;
- replay of stale authorization-dependent response;
- lock contention;
- cleanup deleting active records;
- hash/fingerprint algorithm misuse.

### Safety controls

- key/body length limits;
- scope-aware identity;
- SHA-256;
- bounded replay storage;
- header allowlist;
- no sensitive logging;
- state-aware cleanup;
- application-level rate limiting;
- store unique constraint;
- fencing token for state ownership;
- security tests.

OperationGuard itself does not replace API authentication, authorization or rate limiting.

---

## 22. Core invariants

V1 tests must prove:

1. At most one active owner can successfully reserve the same logical operation at a time.
2. Same identity + different fingerprint never executes as a normal retry.
3. Completed records never re-enter business execution during the retention window.
4. A stale owner cannot complete another owner's recovery claim.
5. Store outage never silently disables protection in the default profile.
6. Cleanup cannot remove an actively owned operation.
7. Tenant/scope A cannot collide with tenant/scope B solely because they use the same client key.
8. Replay storage never exceeds configured bounds.
9. Unhandled ambiguous failure does not automatically become "safe to retry".
10. Public documentation never claims a stronger guarantee than the configured topology can provide.

---

## 23. Required test campaign

### Unit

- identity validation;
- key limits;
- fingerprint construction;
- state transitions;
- replay header filtering;
- option validation;
- expiry rules.

### Integration

Run against real SQL Server and PostgreSQL.

Cases:

- first reservation;
- duplicate reservation;
- concurrent inserts;
- completion;
- replay;
- fingerprint mismatch;
- cleanup;
- transaction rollback;
- transaction-coupled business mutation.

### Concurrency

At least:

- 100 same-key concurrent requests in one process;
- 100 same-key concurrent requests across multiple service instances;
- mixed same-key/different-fingerprint race;
- concurrent cleanup vs active request.

### Fault injection

Terminate/fail at:

- before reservation;
- after reservation;
- before business commit;
- after business commit;
- before guard completion;
- during guard completion;
- during response persistence.

### Security

- oversized key;
- control characters;
- huge body;
- malicious header values;
- sensitive response headers;
- cross-scope collision;
- log inspection for leakage.

---

## 24. Performance goals

V1 should not market a fixed throughput number until benchmarked.

Benchmark:

- reservation latency;
- duplicate lookup/replay;
- 100/1000 concurrent keys;
- hot-key contention;
- allocation profile;
- replay body sizes.

Correctness under hot-key contention is more important than maximum synthetic throughput.

---

## 25. Architecture decision review — Five Hats

### White

Distributed retries create ambiguity; local DB transactions are the strongest practical boundary available without 2PC.

### Red

Developers will assume "idempotency library" means "safe to retry." Therefore unsafe topology limitations must be visible in API/docs instead of buried in caveats.

### Black

The worst bug is a library that says "duplicate prevented" while a crash window can charge or mutate twice.

### Yellow

A rigorous shared component can protect payments, wallet commands, provider callbacks, sportsbook commands, webhooks and consumers.

### Green

Instead of pretending all stores give identical guarantees, expose topology/transaction limits and optimize for local transactional coupling first.

---

## 26. Reversibility register

| Decision | Door | Why |
|---|---|---|
| Package/namespace name | One-way after release | Consumer source and package references depend on it |
| Three-state model | One-way-ish | Persisted records and recovery logic depend on state semantics |
| Fail-closed default | One-way behavioral contract | Security/reliability expectation |
| SQL Server/PostgreSQL first | Two-way | More providers can be added later |
| No Redis production provider in V1 | Two-way | Can add later with documented weaker topology |
| Raw-body conservative fingerprint | Two-way | Custom providers already create escape route |
| 409/422 HTTP defaults | Two-way | Configurable mapping can evolve |
| Bounded replay | One-way default principle | Removing the bound would be unsafe; exact sizes remain configurable |

---

## 27. V1 package layout

```text
src/
  OperationGuard.Core/
    Abstractions/
    Models/
    Options/
    Exceptions/
    Validation/
    Diagnostics/
    Internal/

  OperationGuard.AspNetCore/
    Filters/
    Middleware/
    Metadata/
    Fingerprinting/
    ProblemDetails/
    Registration/
    Extensions/
    Internal/

  OperationGuard.SqlServer/
    Stores/
    Schema/
    Registration/
    Internal/

  OperationGuard.PostgreSql/
    Stores/
    Schema/
    Registration/
    Internal/

tests/
  OperationGuard.Core.Tests/
  OperationGuard.AspNetCore.Tests/
  OperationGuard.SqlServer.IntegrationTests/
  OperationGuard.PostgreSql.IntegrationTests/
  OperationGuard.ConcurrencyTests/
  OperationGuard.SecurityTests/
  OperationGuard.Benchmarks/

samples/
  PaymentApi/
  MessageConsumer/
```

---

## 28. V1 exclusions

Do not include:

- automatic distributed transactions;
- broker-specific integration;
- Redis production provider;
- automatic JSON semantic canonicalization;
- indefinite request waiting;
- magic reconciliation of external side effects;
- unbounded replay bodies.

---

## 29. Release gate

OperationGuard V1 may release only when:

- SQL Server and PostgreSQL contract tests pass;
- multi-instance hot-key concurrency tests pass;
- all ambiguous failure windows are documented/tested;
- fail-closed behavior is verified;
- response replay leakage tests pass;
- security/static-analysis checks pass using the repository’s currently available tooling;
- project-specific review agent returns PASS;
- no unresolved Critical/High findings remain;
- API and persisted schema are reviewed as one-way-door contracts;
- sample payment API demonstrates transaction-coupled usage;
- sample message consumer demonstrates duplicate suppression without broker coupling.

---

# Pack-Level Execution Rules

The implementation follows the frozen Stage-0 sequential-gate model:

- Agent 00 — Orchestrator
- Agent 01 — Contract/Test Architect
- Agent 02 — Implementation
- Agent 03 — Integration/Fault/Performance
- Agent 04 — Project-Specific Senior Reviewer
- Agent 05 — Release/Packaging

No gate advances on FAIL. Human review is required before merge/release. Agents should resolve normal engineering problems among themselves through Agent 00.
