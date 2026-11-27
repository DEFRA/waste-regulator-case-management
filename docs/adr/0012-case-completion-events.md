# ADR-0012: Publish case completion events from an outbox

**Date:** 2026-11-26
**Status:** Accepted
**Issue:** <https://eaflood.atlassian.net/browse/AMCR-679>
**Supersedes:** [ADR-0011](0011-gated-source-system-callback.md)

## Context

When a caseworker accepts or cancels a CSoC case, the source system (Waste
Obligations) must learn the outcome. ADR-0011 gated the terminal transition on
a synchronous HTTP PATCH to a per-case `callbackUrl`. That made caseworkers
wait on WO availability and complicated concurrency when another change landed
during the callback.

Case Management now owns an SNS topic for completion. The Case Intake Processor
supplies a per-case `completionEvent` template at `POST /cases` (event type plus
JSON payload with placeholders). Source systems consume the published envelope.

## Decision

1. **Intake** requires `completionEvent` and rejects `callbackUrl`.
2. **Terminal transitions** render the stored template (actor, decision and
   terminal-state placeholders) and persist a **completion envelope** on the
   `state-changed` audit event in the **same Mongo transaction** as the case
   replace ([ADR-0010](0010-transactional-audit-events.md)).
3. A background processor publishes unsent completion envelopes to SNS
   (`dispatches.completion`), with the same lease, batch, retry and dead-letter
   pattern as analytics. SNS message attributes include `caseType` and
   `eventType`.
4. Missing `decision.*` placeholders render as JSON `null` (e.g. `reason` on
   accept). Missing `actor.*` or `state.*` placeholders fail the action with
   `400` — the caseworker identity must be complete.
5. Bulk-loaded cases that arrive already terminal do **not** publish at create
   time (parity with the old callback behaviour).

## Consequences

- Caseworkers conclude cases immediately; WO (or an interim bridge) learns the
  outcome asynchronously.
- SNS publish failures do not roll back the terminal case; retries continue until
  `MaxDispatchAttempts`, then dead-letter.
- CIP must send `completionEvent` before strict intake validation ships.
- WO dedupes on `eventId` (outbox document id), stable across retries.