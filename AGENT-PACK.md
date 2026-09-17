# OperationGuard V1 Agent Pack Instructions

This is a self-contained Codex agent pack generated from the frozen Stage-0 architecture package.

## Start

Place this pack in or alongside the repository, then give Codex the contents of `InitialPrompt.txt` (or point it to that file).

## Files

- `SOURCE-OF-TRUTH.md` — frozen V1 architecture and constraints.
- `AGENTS.md` — global repository/development/communication rules.
- `InitialPrompt.txt` — entry prompt for Codex.
- `agents/00-orchestrator.md`
- `agents/01-contract-test-architect.md`
- `agents/02-implementation.md`
- `agents/03-integration-fault-performance.md`
- `agents/04-senior-reviewer.md`
- `agents/05-release-packaging.md`

## Operating principle

The orchestrator may send work back and forth between agents until the active gate passes. Normal engineering problems should not be escalated to the human.


> The repository's actual `README.md` is a release deliverable created/finalized by Agent 05 after the API and samples are validated.
