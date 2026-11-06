# ADR-0010: Audit events written in the same transaction as each case change

**Date:** 2026-11-04
**Status:** Accepted
**Supersedes:** [ADR-0004](0004-work-item-audit-sink-routing.md)
**Issue:** <https://eaflood.atlassian.net/browse/AMCR-622>

## Context

ADR-0004 made the timeline embedded in each case document the only audit
record and rejected a durable second sink (its option C) because no
consumer needed one.

CSoC iteration 2 adds that consumer. Every case change must reach the
platform's analytics topic on SNS, the same way the Waste Obligations API
publishes its changes. A background processor that finds "every change
not yet published, across all cases" cannot query the timelines embedded
in each case document efficiently, so the changes need a collection of
their own. ADR-0004 named this as its trigger for supersession: audit
events centralised across services.

The Waste Obligations API already solves this with a transactional
outbox: each change writes an `auditEvents` document in the same MongoDB
transaction as the entity, numbered from a counter, and a processor
publishes them in order.

## Decision

Adopt the Waste Obligations API outbox (option C of ADR-0004).

- `CasePersistence` writes every case create and replace, and an
  `auditEvents` document for it, inside one MongoDB transaction, so an
  audit event exists if and only if its case change was committed.
- The audit event describes the case's newest timeline entry: its action
  is the event type and its performer the actor (`user:<id>`,
  `service:<client id>`, or `system:waste-regulator-case-management` when
  no performer is recorded). It carries the whole case before and after the
  change, the case version, entity `case`, entity id `case_<id>` and schema
  version `case.v1.0`.
- Sequence numbers come from the `auditEventCounters` collection, in the
  same transaction.
- The embedded timeline stays: it serves the caseworker UI, read with the
  case.
- Transactions run with a five-second budget and retry transient
  transaction errors and write conflicts with backoff, as the Waste
  Obligations API does.

## Consequences

- MongoDB must run as a replica set everywhere, including local
  development; the Docker Compose stack starts a single-node replica set.
- Each case write costs a transaction, a counter update and an insert.
- Publishing the audit events to SNS is a separate processor reading the
  `auditEvents` collection.
- A case write path that does not append a timeline entry fails, because
  there is nothing to record. Every engine write appends one.
