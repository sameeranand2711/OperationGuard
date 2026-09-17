# OperationGuard — Codex Agent Pack

## Mission

Build a durable idempotency library for HTTP and message-operation workflows without overstating exactly-once guarantees.

This pack implements the frozen V1 architecture in `SOURCE-OF-TRUTH.md`. It is deliberately sequential and gate-driven. Agent 00 owns orchestration and agent-to-agent conflict resolution.

## Mandatory reading order

1. `SOURCE-OF-TRUTH.md`
2. `AGENTS.md`
3. `AGENT-PACK.md`
4. `agents/00-orchestrator.md`
4. the active agent file assigned by Agent 00

## Non-negotiable project rules


- Production V1 providers: SQL Server and PostgreSQL.
- In-memory support is test/sample only.
- Do not add a production Redis provider in V1.
- Durable operation identity is `Scope + OperationName + IdempotencyKey`.
- V1 states are `InProgress`, `Completed`, `Indeterminate`.
- Same identity + different fingerprint must never execute as a normal retry.
- Default behavior is fail closed when the protection store is unavailable.
- ASP.NET defaults: required missing key -> 400; payload mismatch -> 422; duplicate while active -> 409.
- SHA-256 is the default fingerprint digest.
- Raw request-body semantics are conservative by default; do not silently invent JSON canonicalization.
- Response replay must remain bounded and security-filtered.
- Do not automatically retry an `Indeterminate` operation.
- Do not claim exactly-once behavior for external side effects.


## Sequential gates


1. OG-0 repository preparation
2. OG-1 contract/invariant tests
3. OG-2 core implementation
4. OG-3 SQL Server/PostgreSQL providers
5. OG-4 ASP.NET + message-operation integration
6. OG-5 failure/concurrency/security campaign
7. OG-6 samples
8. OG-7 security/static analysis + Agent 04 adversarial review
9. OG-8 packaging/release


No gate advances on FAIL.

## Required final outcome

- clean restore/build for .NET 8 and .NET 10;
- all required tests green;
- project-specific security/static-analysis checks completed with no unresolved Critical/High issue;
- project-specific Agent 04 review returns PASS;
- samples build and demonstrate the intended contract;
- package validation succeeds;
- release documentation is current;
- PR/MR is prepared/created;
- no automatic merge.


# Global Agent Rules

These rules apply to every agent in this pack.

## Source of truth

1. Read `SOURCE-OF-TRUTH.md` before taking action.
2. Read `AGENTS.md`.
3. Treat the frozen V1 architecture as authoritative.
4. Do not silently redesign one-way-door decisions.
5. If implementation reveals a genuine contradiction in the frozen specification, stop that gate and return the contradiction to Agent 00.
6. Make normal engineering decisions yourself. Do not ask the human about routine implementation details.

## Human intervention threshold

Human intervention is allowed only when at least one of these is true:

- the frozen specification contains a material contradiction that cannot be reconciled without changing a one-way-door public contract;
- a decision depends on business intent, legal/compliance requirements, licensing, credentials, or infrastructure access that cannot be inferred;
- a destructive repository action would be required;
- the requested change would violate an explicit frozen constraint.

Compile failures, test failures, race conditions, design defects, provider bugs, documentation gaps, benchmark regressions, and ordinary implementation disagreements are **not** reasons to ask the human. Agents must resolve those internally through Agent 00.

## Repository and Git rules

- A permanent `main` branch must exist.
- Never implement directly on `main` or a permanent `release/vN.x` branch.
- Use a dedicated temporary feature/fix/security/refactor branch for implementation changes.
- Create/maintain `release/v1.x` only when V1 becomes release-ready/supported.
- Use semantic-version tags for actual releases.
- Never auto-merge.
- Human review is required before merge/release.
- Do not rewrite repository history unless explicitly authorized.

## Source organization

- One public type per file.
- No implementation files dumped into project roots.
- Organize by responsibility/domain.
- Namespaces must follow the physical/responsibility structure.
- Avoid vague catch-all `Helpers`, `Utils`, or giant `Services` folders.
- Do not create empty architectural folders merely to match a template.

## Engineering rules

- Target both `net8.0` and `net10.0`.
- Nullable reference types should be configured appropriately.
- Propagate `CancellationToken` through asynchronous I/O paths.
- Prefer `TimeProvider` where time affects correctness/testability.
- Minimize external dependencies.
- Keep public APIs small.
- Fail fast on invalid configuration.
- Do not log secrets, sensitive payloads, credentials, or full opaque identifiers by default.
- Use vendor-neutral diagnostics/observability hooks.
- Do not make claims stronger than the implementation can prove.

## Agent communication

Agents communicate through Agent 00. A downstream FAIL is routed back to the agent capable of fixing it:

- contract/test defect -> Agent 01;
- product/source implementation defect -> Agent 02;
- integration/concurrency/fault/performance defect -> Agent 03;
- documentation/package/release defect -> Agent 05.

Agent 04 independently re-reviews after material remediation.

## Output discipline

Do not create extra planning files, speculative design documents, or duplicate reports unless this pack explicitly requires them. Return a concise structured result to Agent 00 using:

```text
RESULT: PASS | FAIL

COMPLETED:
- ...

BLOCKERS:
- ...

FINDINGS:
- severity | finding | evidence

FILES_CHANGED:
- ...

TESTS/RUNS:
- command/result summary

HANDOFF:
- exact next action
```

A PASS must be evidence-based.


## Mandatory repository README deliverable

The final library repository must contain a **well-structured `README.md` created/finalized by Agent 05 after the implementation and samples are validated**.

Do not create the repository README early from imagined APIs. The README must reflect the actual compiled V1 public API and verified sample applications.

### Required README structure

Use this structure unless a section is genuinely not applicable:

```text
# <Library Name>

Short one-paragraph purpose statement.

## Status
- Current version / V1 status
- Supported target frameworks
- Stability/support note if appropriate

## What This Library Does
- Core responsibilities
- Primary use cases

## What This Library Does Not Do
- Explicit non-goals / boundaries
- Important guarantees the library does not provide

## Installation
- NuGet package(s)
- Package-selection guidance if multiple packages exist

## Quick Start
- Smallest working example
- DI/registration if applicable
- Basic usage

## Core Concepts
- Important terminology
- State/guarantee model
- Key abstractions

## Configuration
- Options
- Defaults
- Limits
- Safety-relevant settings

## Usage Scenarios
- At least one realistic example
- Additional scenario(s) where materially useful

## Reliability / Safety Semantics
- Failure behavior
- Retry / duplicate / collision semantics as applicable
- What callers must do to remain safe

## Observability
- Logs / metrics / traces if applicable
- What is and is not logged

## Performance / Scaling Notes
- Relevant benchmark guidance
- Important bottlenecks or trade-offs
- Avoid unsupported marketing claims

## Samples
- Links/paths to validated sample projects
- What each sample demonstrates

## Limitations
- Explicit V1 limitations
- Deferred features

## Versioning / Compatibility
- Target frameworks
- Compatibility expectations
- Upgrade notes if relevant

## Contributing / Development
- Only if repository policy requires it
- Keep brief and relevant

## License
- License reference
```

### README quality rules

- All code snippets must use the **actual implemented API**.
- Prefer snippets copied from or validated against compiled samples.
- Do not document hypothetical methods, options, overloads, package names, or future features.
- Do not claim guarantees stronger than the implementation proves.
- Put critical limitations near the relevant usage sections, not only at the bottom.
- Keep examples concise enough to understand but complete enough to run.
- Explain safe defaults and dangerous configuration changes.
- Avoid generic marketing language.
- Avoid copying internal architecture details that consumers do not need.
- Use tables only where they improve clarity.
- Keep headings consistent and navigable.
- Ensure terminology matches `SOURCE-OF-TRUTH.md`.
- Verify every command/package name/path.
- The README is a consumer document, not an agent log or implementation diary.

### Agent responsibilities for README

- Agent 02: establishes the real public API and usage patterns.
- Agent 03: creates/validates representative samples and usage flows.
- Agent 04: audits README claims/examples for correctness, safety, and guarantee overstatement.
- Agent 05: owns creation/finalization of the repository `README.md` and validates every example before release.


### OperationGuard README additions

The README must clearly explain:

- the three guarantee/topology levels (transaction-coupled vs non-transaction-coupled/external side effects);
- `Scope + OperationName + IdempotencyKey`;
- `InProgress`, `Completed`, and `Indeterminate`;
- fail-closed default;
- `400`, `409`, and `422` HTTP behavior;
- bounded response replay;
- why `Indeterminate` is not automatically retried;
- SQL Server/PostgreSQL provider setup;
- transaction-coupled payment-style usage;
- message-operation usage;
- why OperationGuard does **not** promise universal exactly-once behavior.

At least these working examples should be present if the implemented API supports them:

1. registration/configuration;
2. Minimal API or MVC protection;
3. transaction-coupled database operation;
4. message-consumer operation;
5. custom fingerprint example only if V1 exposes it cleanly.
