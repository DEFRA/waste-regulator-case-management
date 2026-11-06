# ADR-0011: Call the source system before concluding a case

**Date:** 2026-11-05
**Status:** Accepted
**Issue:** <https://eaflood.atlassian.net/browse/AMCR-622>

## Context

A CSoC case is concluded when a caseworker accepts or cancels it. The
decision belongs to the Waste Obligations API, which holds the compliance
declaration, so this service PATCHes it to the case's `callbackUrl`.

That PATCH is not idempotent: the Waste Obligations API answers a repeated
decision with `422`, and it still lets an `Accepted` declaration be
`Cancelled`. It also
sends the producer's cancellation email, so a decision sent twice in
different forms is visible outside both services.

The callback could be sent:

- **A. Before the case changes**, failing the action when it fails.
- **B. After the case changes**, from an outbox, retried until it succeeds.

## Decision

Option A. When an action moves a case into a state its case type maps in
`callbackMappings`, the service renders the mapping and PATCHes it first.

- The action is applied only once the source system has accepted it.
- When the source system returns `422`, the service reads the resource
  back and treats the decision as applied if the resource already carries
  the payload's top-level string values. This covers a PATCH that succeeded
  before this service's own write failed, which a retry then repeats.
- Any other failure returns `502`, leaves the case in its state, and
  records a `callback-failed` timeline entry with the reason.

## Consequences

- A caseworker learns straight away that a decision did not reach the
  source system and can retry, and a case never shows an outcome its source
  system does not hold.
- A caseworker cannot conclude a case while the source system is down.
- Once the callback has succeeded, the case's write ignores the caller
  disconnecting, and a note or assignment saved during the callback is
  reloaded and kept. A write that still fails leaves the case in its
  earlier state until the caseworker retries; the `422` read-back then
  completes it.
- The action's request waits for the source system, up to a 30-second
  timeout.
