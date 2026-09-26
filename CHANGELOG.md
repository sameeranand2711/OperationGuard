# Changelog

All notable changes to OperationGuard are documented here.

## [1.0.0-rc.1] - Unreleased

### Added

- Durable operation coordination for .NET 8 and .NET 10.
- SQL Server and PostgreSQL production providers with transaction-session APIs.
- ASP.NET Core endpoint protection with bounded, security-filtered response replay.
- Broker-neutral message-operation execution and transaction-coupled message sample.
- Explicit `InProgress`, `Completed`, and `Indeterminate` state and recovery semantics.
- Concurrency, failure-window, provider, and security contract campaigns.

### Security

- Fail-closed behavior when the protection store is unavailable.
- Bounded keys, fingerprint bodies, replay bodies, replay header counts, individual header values, and aggregate serialized headers.
- Sensitive and hop-by-hop replay headers are rejected.
- Raw idempotency keys and raw scopes are excluded from default diagnostics.

### Known release blockers

- Package ownership must be confirmed and package IDs rechecked immediately before publication.
