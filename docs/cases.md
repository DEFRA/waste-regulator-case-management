# Case framework (backend)

The case management system manages **cases**. Each case type is a state
machine held as configuration in the `caseModuleConfig` collection, so the
engine serves every case type with the same endpoints, rules and timeline.

## Goals

- **Adding a case type is one configuration file.** No code changes.
- It is obvious from reading a case type's configuration which
  **transitions** are allowed from each **state** of the case.

## Building blocks

Defined in `WasteRegulatorCaseManagement/Cases/`:

| Type | Purpose |
| --- | --- |
| `CaseState` | Identifier + display name for a state. `IsTerminal` marks completion states (e.g. approved/rejected). |
| `CaseTask` | Identifier + display name for a unit of work to be completed in a state. |
| `CaseTransition` | A named action (`approve`, `reject`, `withdraw`) that moves a case from one state to another. |
| `CaseModuleConfig` | A case type: `CaseType`, `DisplayName`, `TemplateVersion`, `InitialStateId`, `States` and `Transitions`. Stored in the `caseModuleConfig` collection with the case type as its id. |
| `CaseModuleConfigLoader` | Startup hosted service: writes the configurations embedded under `Cases/CaseModuleConfigs/` to the database, then loads every case type in the database into the registry. |
| `CaseRegistry` | Lookup of every loaded case type. |
| `ICaseService` | Framework service object that drives state transitions. Resolves the case, validates the request against the type, persists the change and writes an timeline. |
| `Case` | The persisted case: id, case type, state id, terminal flag, submitted-at, last-modified-at, submitted-by (CDP client id), the intake fields (source system id, callback URL, nation, org id, producer type, display data) and the timeline. |
| `ICasePersistence` | Framework-owned MongoDB persistence for `Case`s. |
| `CaseModuleExtensions` | `AddCaseFramework()`. |

> The **state engine** validates progressions and handles the
> corresponding HTTP requests.

## Ingestion API

The framework exposes generic, type-agnostic endpoints for accepting,
listing and progressing cases. They live in `CaseEndpoints` and
are mounted by `MapCaseFrameworkEndpoints()`:

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/cases` | Submit a new case. Body: `{ "caseType", "sourceSystemId", "callbackUrl", "nation", "orgId", "producerType"?, "displayData"? }` — see below. The `caseType` must be a case type in `caseModuleConfig`; the server stamps the case with the type's `InitialState`, the caller's CDP client id and a server-side timestamp. Returns `201 Created` with `Location: /cases/{id}`; a `sourceSystemId` that already has a case returns that case with `200 OK` and `X-Idempotent-Replay: true`. |
| `GET` | `/cases/{id}` | Fetch a single case by id, projected with the actions the engine will currently allow. |
| `GET` | `/cases` | List persisted cases (with the same projection), newest first, with filter / search / pagination per RA-93. Query string parameters: `caseType` (repeatable), `stateId` (repeatable), `search` (free-text — matched on id and submitter), `page` (1-based, default 1), `pageSize` (default 20, capped at 100). Returns a paged envelope: `{ items, totalCount, page, pageSize }`. |
| `POST` | `/cases/{id}/actions/{actionId}` | Invoke a named action declared by the type's transitions. `409` if the case is in a terminal state; `400` for unknown actions and transitions whose from-state does not match. |

All three endpoints require authentication via the CDP client id
header (`x-cdp-client-id`) per RA-89/RA-85b.

### Intake fields

Only the Case Intake Processor may create cases: `POST /cases` requires the
`CaseIntake` policy, which matches the HMAC-verified client id against
`Auth__CaseIntakeClientId` (default `case-intake-processor`). The new case's
`case-created` timeline entry is attributed to `service:<client id>`.

| Field | Required | Notes |
| --- | --- | --- |
| `sourceSystemId` | Yes | Immutable reference to the submission in the source system. Unique: one case per submission. |
| `callbackUrl` | Yes | Absolute http(s) URL the outcome is reported to when the case reaches a terminal state. |
| `nation` | Yes | `ENGLAND`, `WALES`, `SCOTLAND` or `N_IRELAND`. |
| `orgId` | Yes | The submitting organisation's id in the source system. |
| `producerType` | No | Stored and returned uninterpreted (for CSoC, `DIRECT_PRODUCER` or `COMPLIANCE_SCHEME`). |
| `displayData` | No | Map of field name to `{ "value": string, "type": "string" \| "number" \| "date" \| "boolean" }`, rendered by the caseworker dashboard. Stored and returned without interpretation. |
| `initialState` | No | Bulk load of existing submissions only: start the case in this state of its case type instead of the initial state, so already-decided cases arrive decided. |
| `createdAt` | No | Bulk load only: ISO 8601 submission time to keep as `submittedAt`, preserving queue order. |

The case management service holds a reference to the submission, not its
content: anything else a caseworker needs is fetched from the source system.

### Nation scoping

A caseworker sees and acts on only the cases of their own nation. Every route
except `POST /cases` reads the signed `x-cdp-user-nation` header (claim
`user:nation`, one of the intake `nation` values): a caller without one gets
`403`, and a case in another nation is `404`, so its existence is not revealed.
The list is filtered to the caller's nation before any other filter applies.

## Adding a new case type

Add a JSON file to `WasteRegulatorCaseManagement/Cases/CaseModuleConfigs/`,
for example `CSoC.json`:

```json
{
  "caseType": "CSoC",
  "displayName": "CSoC",
  "templateVersion": "v1",
  "initialStateId": "SUBMITTED",
  "states": [
    { "id": "SUBMITTED", "displayName": "Submitted" },
    { "id": "ACCEPTED", "displayName": "Accepted", "isTerminal": true },
    { "id": "CANCELLED", "displayName": "Cancelled", "isTerminal": true }
  ],
  "transitions": [
    { "actionId": "accept", "displayName": "Accept", "fromStateId": "SUBMITTED", "toStateId": "ACCEPTED" },
    { "actionId": "cancel", "displayName": "Cancel", "fromStateId": "SUBMITTED", "toStateId": "CANCELLED" }
  ]
}
```

Every file in the folder is embedded in the assembly and written to
`caseModuleConfig` at startup, replacing the stored copy, so a deployment
carries its case types with it. Startup fails if the initial state or a
transition names a state the file does not declare. Change
`templateVersion` whenever a change would render existing cases
inconsistently (see below).

## Template versioning (RA-94)

Cases live for a long time. Their state machine and detail
templates evolve. Once a case has been progressed by an assessor under
v1 of a type, the audit history must continue to make sense even after the
team ships v2. The framework solves this by **freezing the template at
submission**.

### Wiring

`ICaseTemplate` is the slice of `CaseModuleConfig` the engine actually
needs at runtime: `States`, `Transitions` and a `TemplateVersion` string.

`CaseTemplateSnapshot` is a sealed, frozen `ICaseTemplate` copied from a
live `CaseModuleConfig` via `CaseTemplateSnapshot.Capture(type)`.

### Storage

`Case` carries two new fields:

- `TemplateSnapshot` — the captured `ICaseTemplate` for the version of
  the type that submitted the case.
- `TemplateVersion` — a copy of the snapshot's version string for cheap
  filtering and surfaceing on the wire.

Both are populated by `POST /cases` before the envelope is persisted.

### Engine resolution

`CaseService.ResolveTemplate(caseRecord)` returns the case's
`TemplateSnapshot` if present, otherwise it falls back to the live
`CaseModuleConfig` from the registry. Every engine operation — action validation,
projection of available actions — runs
against the resolved template, never the live type. As a result, shipping
v2 of a type cannot retroactively change the rules under which an in-flight
v1 case was being progressed.

### Wire format

`CaseResponse` includes `templateVersion`. Clients use it (together
with `caseType`) to pick the correct detail template for the case.

## Assignment (RA-95)

Cases can be assigned to a user. RA-323 unified caseworker
permissions — every authenticated caseworker can assign or re-assign any
case to any user, and can unassign; there is no role tier that
restricts assignment to self-assign-only any more. This is enforced (or
rather, not restricted) by `CaseService.AssignAsync` / `UnassignAsync`,
which no longer check role membership at all — see ADR-0005: authorization
(including any future assignment-permission tiering) is the frontend BFF's
responsibility, not this backend's.

### Identity from the BFF

The client id auth handler (`ClientIdAuthenticationHandler`)
optionally reads two headers forwarded by the BFF and turns them into
`ClaimsPrincipal` claims:

| Header | Claim |
| --- | --- |
| `x-cdp-user-id` | `user:id` (used by `ResolveActorUserId`) |
| `x-cdp-user-name` | `user:name` |

These are used for audit attribution only (`Case.Timeline.PerformedById` /
`PerformedByName` and similar). Role membership is not part of the trust
contract — see ADR-0005.

### Storage

`Case` gains four nullable fields:

- `AssignedToId` / `AssignedToName` — the current assignee (snapshot of the
  display name so the UI does not have to look the user up again).
- `AssignedAt` — UTC timestamp of the most recent assignment change.
- `AssignedBy` — id of the user who performed the assignment (for audit).

The Mongo collection has an `assigneeAndSubmitted` index for the common
"my work" / "unassigned" list queries.

### List filters

`GET /cases` accepts two extra query parameters:

| Param | Effect |
| --- | --- |
| `assigneeId=<userId>` | only items currently assigned to that user |
| `unassigned=true` | only items with no current assignee |

If both are supplied, `assigneeId` wins.

### Endpoints

```
POST /cases/{id}/assign
  Body: { "assigneeId": "<userId>", "assigneeName": "<display name>" }
  Authorization: any authenticated user; backend enforces the
                 self-assign-only rule for non-`assign` callers.

POST /cases/{id}/unassign
  Body: (none)
  Authorization: caller must hold the `assign` role.
```

Both endpoints are idempotent: assigning an item to its current assignee
or unassigning an already-unassigned item returns the existing state with
no audit churn.

## Notes (RA-96)

Caseworkers add free-text notes at any point. A note is not a separate
collection: it is a `note-added` entry on the case timeline (below), with
the trimmed text in `Details["text"]`, so notes and every other action
share one append-only, attributed history.

```
POST /cases/{id}/notes
  Body: { "text": "<note body>" }
  Authorization: any authenticated user.

  Validation:
   - `text` required, non-blank, ≤ CaseService.MaxNoteLength (4000)
     characters. Server trims leading/trailing whitespace before storing.

  Returns: the updated CaseResponse, with the new note-added entry first
  in `timeline`.
```

Notes are append-only: there is no edit or delete endpoint.

## Timeline (RA-97)

### Single source of truth

`Case.Timeline` (the per-document, append-only list described below) is
the **single authoritative audit trail** for every case. There is no
parallel database or external audit store. The console-only
`AuditLogger`/`logger.Audit(...)` helper that previously emitted a second,
in-memory-only stream of audit events has been retired — it left no durable
record (lost on log rotation) and produced two divergent histories for the
same mutation. Service operational logs at `LogInformation` describe what
the engine did for support purposes, but the auditable record lives on the
case itself.

### Mechanism

Every state-changing engine call (`SubmitAsync`,
`ApplyActionAsync`, `AssignAsync`, `UnassignAsync`, `AddNoteAsync`)
automatically appends a `CaseTimelineEntry` to `Case.Timeline` on
success. The framework owns this, so every case type has an identical audit
trail. The submission entry
is the case's birth event: it is appended to the in-memory document
before the single `CreateAsync` call so the new document and its first
audit entry land in storage together.

### Storage

`Case.Timeline` is a `List<CaseTimelineEntry>` persisted inline on
the case document. An entry carries:

| Field | Purpose |
| --- | --- |
| `Id` | Server-generated ULID. |
| `Action` | Stable machine id of the action: `case-created`, `state-changed`, `assigned`, `unassigned`, `note-added`. Post-action hooks append their own alongside these (e.g. `notification-sent`). |
| `ActionDisplayName` | Human-readable description (e.g. `Note added`). |
| `Details` | `Dictionary<string, string?>` of contextual fields per action: `caseType`/`stateId`/`templateVersion` (submission); `actionId`/`actionDisplayName`/`fromStateId`/`toStateId`; `assigneeId`/`assigneeName`/`previousAssigneeId`/`previousAssigneeName` (unassigned); `text` (note). |
| `PerformedAt` | UTC timestamp from the injected `TimeProvider`. |
| `PerformedById` | Snapshot of the actor's user id (`user:id` claim, required — mutations without it are rejected with 401). |
| `PerformedByName` | Snapshot of the actor's display name (`user:name`) at write time. |

### Wire format

`CaseResponse.Timeline` is sorted **reverse-chronologically (newest-first,
RA-568)** so a UI renders the most recent activity at the top of the
timeline without re-sorting.

### Conventions

- **Append-only.** No edit / delete / clear endpoints — the log is the
  audit trail.
- **Failures never write.** Idempotent no-ops (e.g. re-assigning to the
  same user, unassigning an already-unassigned item, re-recording a decision
  that has already been made) and validation / authorization rejections do
  **not** append an entry. This keeps the timeline meaningful.
- **No case-type-specific entry shape.** If a case type needs richer
  details, extend the framework's `Details` keys rather than introducing a parallel
  audit channel.
- **Snapshot identity at write time.** `PerformedById` / `PerformedByName` are
  not live foreign keys — the audit narrative survives directory changes.
