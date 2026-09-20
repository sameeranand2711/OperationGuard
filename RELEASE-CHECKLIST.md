# OperationGuard V1 Release Checklist

This checklist prepares V1 for human review. It does not authorize publication, tagging, merging, or creation of a permanent release branch.

## Source and branch

- [ ] Feature branch is clean at the reviewed release commit.
- [ ] `main` remains unchanged and no `release/v1.x` branch was used for implementation.
- [ ] Human reviewer approves the final diff; no automatic merge.

## Build and verification

- [x] Restore succeeds.
- [x] Release build succeeds for `net8.0` and `net10.0` with no warnings.
- [x] Core, ASP.NET, concurrency, security, SQL Server, and PostgreSQL test matrices pass on both target frameworks (including Agent 03's 154/154 real-provider matrix: SQL Server 78 and PostgreSQL 76).
- [x] Benchmark smoke succeeds on both target frameworks.
- [x] `PaymentApi` and `MessageConsumer` build from a clean restore and pass concise runtime smoke checks.
- [x] Formatting verification passes.
- [x] NuGet dependency vulnerability audit and available static analysis have no unresolved Critical/High finding.
- [x] Agent 04 audits the final README claims and examples after release documentation changes.

## Package validation

- [x] Version is `1.0.0` and all four package IDs are explicit.
- [ ] Package ID availability is checked again immediately before publication and ownership is confirmed.
- [x] `OperationGuard.Core`, `OperationGuard.AspNetCore`, `OperationGuard.SqlServer`, and `OperationGuard.PostgreSql` pack successfully.
- [x] Each package contains `net8.0` and `net10.0` assets, the README, and expected metadata only.
- [x] Dependency boundaries are correct and no provider is pulled into Core or ASP.NET unintentionally.
- [x] No sample, test, agent-pack, build output, or internal release file leaks into a package.
- [x] Symbol packages are produced and inspected.
- [x] Repeated local pack payloads are byte-identical apart from generated package core-properties metadata and its relationship.

## Legal and publication

- [ ] Repository owner selects a license and adds the license file.
- [ ] NuGet package license metadata matches the selected repository license.
- [ ] Repository URL/project URL are added after the canonical public repository is known.
- [ ] NuGet credentials/owner account and package ownership are confirmed by the human publisher.
- [ ] Final semantic-version tag is created only after human approval.
- [ ] Packages are published only after human approval; this workflow never auto-publishes.

## PR/MR handoff

- [ ] Create a PR/MR from `feature/operationguard-v1` to `main` after Agent 04's final documentation audit.
- [ ] Include build/test/provider/security/package evidence in the PR/MR description.
- [ ] Call out the exactly-once boundary, schema compatibility contract, and remaining human publication steps.
- [ ] Do not merge automatically.
