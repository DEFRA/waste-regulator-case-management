# ADR-0013: The actor's name and email travel in request bodies

**Date:** 2026-11-27
**Status:** Accepted

## Context

The regulator frontend sent the caseworker's display name and email as the
`x-cdp-user-name` and `x-cdp-user-email` headers on every request. HTTP
header values are bytes, so a name with a character above U+00FF, such as
"Gwŷn", makes the frontend's `fetch` throw, and that caseworker cannot use
any Case Management page.

Nothing that reads cases uses either value. They are used only by the
requests that change a case:

- Actions, assignment, unassignment and notes record the name on their
  timeline entry as `PerformedByName`.
- An action that concludes a case sends the name and email to the source
  system in its callback, as `{{actor.name}}` and `{{actor.email}}`.

## Decision

Remove the two headers. Each request that changes a case carries the
caseworker's name in its JSON body as `actorName`, and an action also
carries `actorEmail`. The headers carry only ASCII identifiers: client id,
user id, role and nation.

Both fields are optional, as the headers were. An action whose callback
needs a value that is missing still fails with `502` and a
`callback-failed` timeline entry.

## Consequences

- Names outside Latin-1 work, with no encoding step on either side.
- The unassign endpoint takes an optional body.
- The regulator frontend, the regulator tests and the Bruno collection
  send the values in bodies instead of headers.
- Like the assignee's name, the actor's name and email are trusted because
  the caller presented its API key (ADR-0012), not because they are
  separately verified.
