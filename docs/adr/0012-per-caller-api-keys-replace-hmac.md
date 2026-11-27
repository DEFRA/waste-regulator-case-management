# ADR-0012: Per-caller API keys replace HMAC signing

**Date:** 2026-11-27
**Status:** Accepted
**Supersedes:** ADR-0003, ADR-0007 and ADR-0008. Keeps ADR-0006's one credential per caller.

## Context

Callers proved their client id by signing the trust headers with
HMAC-SHA256 over a canonical payload, with a timestamp and a single-use
nonce (ADR-0003). The signature has two payload forms (ADR-0007), and the
nonces are kept in a Mongo collection shared by every instance (ADR-0008).

HMAC gives two things a plain API key does not:

- The secret never goes over the wire, so a recorded request does not leak
  a reusable credential.
- The timestamp and nonce stop a captured request being sent again.

It does not make the user identity any more trustworthy. The signature
proves only that the caller asserted the user id and nation, which is the
same trust an API key gives: whoever holds the credential says who the user
is. Protection against tampering in transit is TLS's job.

Its costs:

- A Mongo nonce store and clock-skew handling.
- Two payload forms, which every caller has to reproduce exactly: the
  regulator frontend, the regulator tests and the Bruno collection.
- Any change to a signed field has to ship on both sides at once, or every
  request gets `401`.

The regulator frontend is the only production caller, and CDP may later
provide Cognito, which would replace either scheme.

## Decision

Each caller sends the API key registered for its client id in
`x-cdp-auth-key`, alongside `x-cdp-client-id`.

- One key per caller, from `AUTH_API_KEY__MANAGEMENT_FE` and
  `AUTH_API_KEY__CASE_INTAKE`, so one can be revoked without breaking the
  other.
- The key is compared in constant time. An unknown client id and a wrong
  key fail with the same message.
- The header is not `x-api-key`, which the CDP API gateway uses for
  developer API keys.
- The key is never logged, propagated to outbound calls or allowed in a
  CORS preflight.
- Development with no keys configured keeps header-trust mode; any other
  environment with no keys fails closed.

## Consequences

- The signing code, the nonce store and its tests are removed. The
  `clientIdAuthNonces` collection is no longer written; its TTL index
  empties it.
- A key captured from a log or trace can be reused until it is rotated, so
  callers must keep it out of their logs.
- Rotating a key means changing it in this service and its caller together.
- Each caller has to switch from signing to sending its key when this
  service deploys, and the `AUTH_API_KEY__*` secrets have to exist in each
  environment first.
