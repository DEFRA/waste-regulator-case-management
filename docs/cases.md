# Case framework (backend)

The case management system manages **cases**. The design must
accommodate multiple types without rewrites.
This document describes the framework that makes adding a new case type
a localised change to a new module.

## Goals

- Each case type is **self-contained** in its own module.
- The core application provides the framework; modules provide the behaviour.
- **Adding a new type is one folder + one line in `Program.cs`.** No other
  module changes; no core changes beyond the registration call.
- It is obvious from reading a module which **transitions** are allowed for each
  **state** of the case.

## Building blocks

Defined in `WasteRegulatorCaseManagement/Cases/`:

| Type | Purpose |
| --- | --- |
| `CaseState` | Identifier + display name for a state. `IsTerminal` marks completion states (e.g. approved/rejected). |
| `CaseTask` | Identifier + display name for a unit of work to be completed in a state. |
| `CaseTransition` | A named action (`approve`, `reject`, `withdraw`) that moves a case from one state to another. `CallerInvocable` (default `true`) decides whether the generic action endpoint may select it by id, or whether it is reachable only through a module's own service resolving it server-side. |
| `ICaseType` | Declares a type's `TypeId`, `DisplayName`, `InitialState`, `States`, `GetTasksForState(stateId)` and `Transitions`. Pure & side-effect free. |
| `ICaseModule` | A module's entry point. Exposes the `Type` and contributes `RegisterServices(services)` and `MapEndpoints(endpoints)`. |
| `ICaseRegistry` | DI-resolvable lookup of every registered type. |
| `ICaseService` | Framework service object that drives state transitions. Resolves the case, validates the request against the type, persists the change and writes an timeline. |
| `Case` | The persisted case envelope: id, type id, state id, submitted-at, last-modified-at, submitted-by (CDP client id), free-form payload. |
| `ICasePersistence` | Framework-owned MongoDB persistence for `Case`s. |
| `CaseModuleExtensions` | `AddCaseFramework()`, `AddCaseModule<T>()`, `MapCaseModules()`, `MapCaseFrameworkEndpoints()`. |

> The **state engine** validates progressions and handles the
> corresponding HTTP requests.
> Module-specific business logic belongs in module service objects which
> may call (or wrap) `ICaseService`.

## Ingestion API

The framework exposes generic, type-agnostic endpoints for accepting,
listing and progressing cases. They live in `CaseEndpoints` and
are mounted by `MapCaseFrameworkEndpoints()`:

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/cases` | Submit a new case. Body: `{ "typeId": "<type>", "payload": { ... } }`. The `typeId` must be registered with the framework; the server stamps the item with the type's `InitialState`, the caller's CDP client id and a server-side timestamp. Returns `201 Created` with `Location: /cases/{id}`. |
| `GET` | `/cases/{id}` | Fetch a single case by id, projected with the actions the engine will currently allow. |
| `GET` | `/cases` | List persisted cases (with the same projection), newest first, with filter / search / pagination per RA-93. Query string parameters: `typeId` (repeatable), `stateId` (repeatable), `search` (free-text — matched on id and submitter), `page` (1-based, default 1), `pageSize` (default 20, capped at 100). Returns a paged envelope: `{ items, totalCount, page, pageSize }`. |
| `POST` | `/cases/{id}/actions/{actionId}` | Invoke a named action declared by the type's transitions. `409` if the case is in a terminal state; `400` for unknown actions, transitions whose from-state does not match, and transitions declared `CallerInvocable: false`. |

All three endpoints require authentication via the CDP client id
header (`x-cdp-client-id`) per RA-89/RA-85b.

The persisted envelope is owned by the framework; modules describe the shape
of their payload via their `ICaseType` and operate on it via their own
service objects.

## Adding a new case type

1. **Create a folder** under `WasteRegulatorCaseManagement/Cases/<TypeName>/` containing:

   ```
   Cases/MyType/
     MyType.cs            // implements ICaseType
     MyTypeModule.cs      // implements ICaseModule
     Endpoints/           // module-scoped HTTP endpoints
     Services/            // module-scoped service objects
     Models/              // module-scoped models
   ```

2. **Implement `ICaseType`**, declaring states and transitions. Make
   the static structure obvious from a glance; if transitions depend on data, return
   a dynamically-built collection from `GetTasksForState` — but keep the
   declaration co-located with the type.

3. **Implement `ICaseModule`** to register the module's services and
   endpoints. Mount endpoints under `/cases/<type-id>/...` to keep
   modules isolated from each other.

4. **Register the module** in `Program.cs`:

   ```csharp
   static void ConfigureCases(IServiceCollection services)
   {
       services.AddCaseFramework();
       services.AddCaseModule<MyTypeModule>();   // <-- one line per module
   }
   ```

   `MapCaseModules()` is already invoked from `ConfigureEndpoints`.

That is the complete list of changes required outside the new module folder.

## Conventions

- A module **must not** depend on another module. If two modules need shared
  behaviour, lift it into the framework (or a clearly shared utility under
  `WasteRegulatorCaseManagement/Utils`).
- Module DI registrations should use **module-scoped interfaces**
  (`IMyTypePersistence`, not `IPersistence`) to avoid colliding with other
  modules.
- A module's HTTP routes should namespace themselves under
  `/cases/<type-id>` so they do not clash with another module's routes.
- Treat `ICaseType` as data: no I/O, no DI dependencies. Behaviour
  belongs in service objects registered via `RegisterServices`.
- Form submissions (state transitions, type-specific payload edits)
  **must** flow through service objects. The framework's `ICaseService`
  covers transitions, assignment and notes; module-scoped services should
  follow the same pattern (intent-named methods, return result objects
  rather than raw exceptions).
- A bespoke module endpoint that performs a state change **outside** a
  registered `CaseTransition` does not inherit the engine's guards. Such
  a service **must** resolve the same template the engine would, via
  `CaseEngineRules.ResolveTemplate`, so an in-flight item is judged by
  the rules it was submitted under. Put every check before any side effect,
  and return the shared failure code rather than a bespoke one — the
  frontend maps a single message per failure reason.
- **A multi-step change belongs behind one call.** Where reaching a
  meaningful outcome takes more than one transition, the module service
  applies them all server-side and the caller makes one request. Declare the
  intermediate transitions `CallerInvocable: false` so that is the only
  route, and accept every intermediate state as a valid entry state so a
  failure part-way through is finished by replaying the same request rather
  than needing a rescue path.

## Template versioning (RA-94)

Cases live for a long time. Their state machine and detail
templates evolve. Once a case has been progressed by an assessor under
v1 of a type, the audit history must continue to make sense even after the
team ships v2. The framework solves this by **freezing the template at
submission**.

### Wiring

`ICaseTemplate` is the slice of `ICaseType` the engine actually
needs at runtime: `States`, `Transitions`, `GetTasksForState(stateId)` and
a `TemplateVersion` string. `ICaseType` extends `ICaseTemplate`.

`CaseTemplateSnapshot` is a sealed, frozen `ICaseTemplate` produced
from a live `ICaseType` via `CaseTemplateSnapshot.Capture(type)`.
`Capture` walks every state, evaluates `GetTasksForState` and stores the
result in an in-memory dictionary so the snapshot does not call back into
the live type after capture.

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
`ICaseType` from the registry (so legacy items submitted before this
change continue to work). Every engine operation — action validation,
projection of available actions — runs
against the resolved template, never the live type. As a result, shipping
v2 of a type cannot retroactively change the rules under which an in-flight
v1 case was being progressed.

### Wire format

`CaseResponse` includes `templateVersion`. Clients use it (together
with `typeId`) to pick the correct detail template for the case.

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

These are used for audit attribution only (`Case.Timeline.CreatedBy` /
`CreatedByName` and similar). Role membership is not part of the trust
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

Every state-changing engine call (`SubmitAsync`, `CompleteTaskAsync`,
`ApplyActionAsync`, `AssignAsync`, `UnassignAsync`, `AddNoteAsync`)
automatically appends a `CaseTimelineEntry` to `Case.Timeline` on
success. The framework owns this — modules do not opt in and cannot opt
out, so every type inherits an identical audit trail. The submission entry
is the case's birth event: it is appended to the in-memory document
before the single `CreateAsync` call so the new document and its first
audit entry land in storage together.

### Storage

`Case.Timeline` is a `List<CaseTimelineEntry>` persisted inline on
the case document. An entry carries:

| Field | Purpose |
| --- | --- |
| `Id` | Server-generated ULID. |
| `Action` | Stable machine id of the action: `case-created`, `state-changed`, `assigned`, `unassigned`, `note-added`. Modules append their own alongside these (e.g. `notification-sent`). |
| `ActionDisplayName` | Human-readable description (e.g. `Note added`). |
| `Details` | `Dictionary<string, string?>` of contextual fields per action: `typeId`/`stateId`/`templateVersion` (submission); `actionId`/`actionDisplayName`/`fromStateId`/`toStateId`; `assigneeId`/`assigneeName`/`previousAssigneeId`/`previousAssigneeName` (unassigned); `text` (note). |
| `CreatedAt` | UTC timestamp from the injected `TimeProvider`. |
| `CreatedBy` | Snapshot of the actor's user id (`user:id` claim, required — mutations without it are rejected with 401). |
| `CreatedByName` | Snapshot of the actor's display name (`user:name`) at write time. |

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
- **No module-specific entry shape.** If a module needs richer details,
  extend the framework's `Details` keys rather than introducing a parallel
  audit channel.
- **Snapshot identity at write time.** `CreatedBy` / `CreatedByName` are
  not live foreign keys — the audit narrative survives directory changes.
