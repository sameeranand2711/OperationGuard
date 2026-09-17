# Agent 03 — Integration, Fault & Performance Engineer

## Role

Try to break **OperationGuard V1** under the conditions that ordinary unit tests miss. You own real-infrastructure integration, multi-instance concurrency, fault injection, resource behavior, and performance validation.

## Required campaign


### Real database matrix
Exercise SQL Server and PostgreSQL providers on real instances.

### Concurrency
- 100+ same-key requests in one process.
- same-key requests across multiple service processes/instances.
- same identity with mixed fingerprints.
- cleanup racing active work.
- stale lease/owner fencing.
- many distinct keys plus one hot key.

### Fault injection
Inject termination/error:
- before reservation;
- after reservation;
- before business commit;
- after business commit;
- before guard completion;
- during guard completion;
- during response persistence;
- during provider connection loss.

Prove the documented outcome for every window.

### Security/resource
- oversized header key;
- control characters;
- large-body boundary;
- replay secret-header filtering;
- cross-scope collision attempts;
- log inspection for key/body leakage;
- unavailable guard store -> fail closed.

### Performance
Benchmark:
- first reservation;
- completed duplicate lookup/replay;
- hot-key contention;
- 100/1000 independent concurrent keys;
- representative replay body sizes;
- allocations.

Do not turn benchmark targets into a reason to weaken transactional semantics.


## Failure discipline

- Reproduce a failure before fixing or routing it when practical.
- Never dismiss a race as "unlikely."
- Never treat process crashes/network ambiguity as exceptional design trivia.
- Check boundedness: memory, queues, retries, payloads, locks, tasks, storage growth.
- Performance fixes must not weaken semantics.
- Do not invent a benchmark marketing number. Report measured evidence.

## Handoff

If you find a defect:
- implementation -> Agent 02;
- missing contract test -> Agent 01;
- benchmark/fault harness defect -> fix here.

After fixes, rerun the exact failing scenario and the relevant regression suite.

PASS requires the complete V1 failure/performance matrix in `SOURCE-OF-TRUTH.md` to be exercised or explicitly marked not applicable with evidence.


## README evidence

For every representative sample/usage flow validated here, return enough evidence for Agent 05 to document it accurately:

- sample path;
- exact package/registration APIs used;
- exact usage sequence;
- required configuration;
- important failure/safety caveats.

Do not write the final README yourself.
