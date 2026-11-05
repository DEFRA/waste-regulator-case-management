# EPR Register Enrol Management Backend

A .NET 10 backend API for the EPR Register case management service. Built
from
[cdp-dotnet-backend-template](https://github.com/DEFRA/cdp-dotnet-backend-template).

The backend exposes a JSON HTTP API and persists data in MongoDB. It is
designed to run alongside the
[`epr-register-enrol-management-fe`](../epr-register-enrol-management-fe/)
service.

- [Requirements](#requirements)
- [Local development](#local-development)
- [Running with Docker Compose](#running-with-docker-compose)
- [Endpoints](#endpoints)
- [Authentication](#authentication)
- [Configuration](#configuration)
- [Testing](#testing)
- [Frontend integration](#frontend-integration)
- [Licence](#licence)

## Requirements

- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://www.docker.com/) and Docker Compose (for the Docker workflow)
- [MongoDB 7+](https://www.mongodb.com/docs/manual/installation/) running
  locally on `mongodb://127.0.0.1:27017` (or use the Docker Compose stack)

## Local development

Restore and run the API directly with `dotnet`:

```bash
dotnet restore
dotnet run --project WasteRegulatorCaseManagement --launch-profile WasteRegulatorCaseManagement
```

The API listens on `http://localhost:8085`. Verify it is up:

```bash
curl http://localhost:8085/health
```

Outside the Production environment (or with `Swagger__Enabled=true`) the
Swagger UI explorer is available at <http://localhost:8085/swagger>.

The explorer ships with a dev-only **"Authenticate as:"** dropdown in the
topbar that mirrors the BFF's stub login fixtures
(`stub-caseworker-1`, `stub-caseworker-2`, `stub-caseworker-3`). Pick a
user and every "Try it out" call will be sent with the three CDP trust
headers (`x-cdp-client-id`, `x-cdp-user-id`, `x-cdp-user-name`)
for that user. The selection persists in the browser's `localStorage`.
Picking "— anonymous —" clears it.

> The cURL preview Swagger UI shows in the response panel is generated
> *before* the request is sent and does **not** include the headers
> attached by the picker. The actual XHR over the wire does — check the
> server response, not the cURL snippet, to confirm authentication.

The MongoDB connection is configured in
[`WasteRegulatorCaseManagement/appsettings.Development.json`](WasteRegulatorCaseManagement/appsettings.Development.json)
and can be overridden via the `Mongo__DatabaseUri` and `Mongo__DatabaseName`
environment variables.

Outbound GOV.UK Notify email is disabled by default (RA-422): the master switch
`Notify__Enabled` defaults to `false`, so the notification hook is not registered
and a no-op client is used — nothing is sent and no `notification-*` audit
entries are written. To send real notifications you need **both**
`Notify__Enabled=true` **and** a `NOTIFY_API_KEY` from the
[Notify dashboard](https://www.notifications.service.gov.uk/); with the flag on
but no key the service still starts and uses the no-op client (calls logged, no
HTTP traffic to Notify).

Even with the flag on and a key set, notification calls are logged but **never**
sent to GOV.UK Notify when running locally: the no-op client is registered on a
Development host and whenever `ENVIRONMENT` is `local` or `dev`. It returns
success, so the case's audit log still records a `notification-sent` entry
and the UI behaves as it does in a sending environment. The non-production
Notify team key can only reach team-registered addresses plus five guests, and
those slots are used up — see
[docs/cdp-deployment.md](./docs/cdp-deployment.md#notify-sending-by-environment).

To smoke-test the real integration locally, set `NOTIFY_API_KEY` to a key from
the [Notify dashboard](https://www.notifications.service.gov.uk/), enable
`Notify__Enabled=true` **and** `NOTIFY_SENDEMAILS=true`, and make sure the
recipient address is registered on the Notify team.

Regulator-facing emails go to the nation's `Notify:RegulatorMailboxes` entry.
`appsettings.json` ships all four nations blank, so with `Notify__Enabled=true`
locally they are skipped with a `missing-regulator-mailbox` audit entry. To
exercise them, set
`Notify__RegulatorMailboxes__<Nation>` (e.g. `Notify__RegulatorMailboxes__England`)
to a Notify-team-registered address. Each deployed environment sets its own
addresses in cdp-app-config; the real regulator inboxes are in prod's config
only (see [docs/cdp-deployment.md](./docs/cdp-deployment.md#required-environment-variables)).

If you do not have MongoDB installed locally, start just the database from
the Compose stack:

```bash
docker compose up -d mongodb
```

## Running with Docker Compose

The repository ships a Compose stack that builds the API image and starts
its dependencies (MongoDB and a Floci-based AWS emulator):

```bash
docker compose up --build -d
```

Once the stack is healthy the API is reachable on `http://localhost:8085`.
Tear it down with:

```bash
docker compose down -v
```

## Endpoints

| Method | Path                                         | Description                            |
| ------ | -------------------------------------------- | -------------------------------------- |
| GET    | `/health`                                    | Health probe used by CDP               |
| GET    | `/openapi/v1.json`                           | OpenAPI document (anonymous)           |
| GET    | `/swagger`                                   | Swagger UI explorer (non-Production)   |
| POST   | `/cases`                                | Submit a new case                 |
| GET    | `/cases`                                | List/search cases                 |
| GET    | `/cases/{id}`                           | Get a single case by id           |
| POST   | `/cases/{id}/tasks/{taskId}/complete`   | Complete a task on a case         |
| POST   | `/cases/{id}/actions/{actionId}`        | Apply an action / state transition     |
| POST   | `/cases/{id}/assign`                    | Assign a case to a user           |
| POST   | `/cases/{id}/unassign`                  | Unassign a case                   |
| POST   | `/cases/{id}/notes`                     | Add a note to a case              |

## Authentication

All non-health endpoints require a client ID supplied in the
`x-cdp-client-id` request header. This is NOT verified by CDP itself —
outside Development, the caller must also sign the request with an
HMAC-SHA256 signature over a canonical payload, using a secret registered
for that client ID (see [BFF signing contract](docs/cdp-deployment.md#bff-signing-contract)
and RA-345/ADR-0006). In Development only, with no client secrets
configured, the handler falls back to trusting the header's presence alone:

```bash
curl -H 'x-cdp-client-id: my-upstream-service' \
  http://localhost:8085/cases
```

Requests without the header (or, outside Development, without a valid
signature) receive `401 Unauthorized`. The `/health`
endpoint is anonymous and remains reachable without authentication.

For local exploration without crafting cURL commands by hand, use the
Swagger UI explorer at <http://localhost:8085/swagger> with its built-in
stub-user picker — see [Local development](#local-development) above.

## Configuration

Config is loaded via ASP.NET Core's standard providers; nested `Section:Key`
config binds from `SECTION__KEY` env vars, while CDP-secrets-tab items use a
flat `UPPER_SNAKE_CASE` name instead. See
[`docs/cdp-deployment.md`](docs/cdp-deployment.md) for the authoritative
per-environment list — the tables below summarise it.

### Service-to-service auth

| Variable | Secret? | Description |
| --- | --- | --- |
| `AUTH_SHARED_SECRET__CASE_INTAKE` | Yes | Verifies inbound signed requests from the Case Intake Processor — must match the secret it signs with exactly |
| `AUTH_SHARED_SECRET__MANAGEMENT_FE` | Yes | Verifies inbound signed requests from `epr-register-enrol-management-fe` — must match management-fe's `BACKEND_API_SHARED_SECRET` exactly |
| `Auth__CaseIntakeClientId` | No | Expected `x-cdp-client-id` on inbound calls from the Case Intake Processor (default `case-intake-processor`) |
| `Auth__ManagementFeClientId` | No | Expected `x-cdp-client-id` on inbound calls from management-fe (default `frontend`) — must differ from `Auth__CaseIntakeClientId` or the service throws at first request |

The `CASE_INTAKE` and `MANAGEMENT_FE` secrets are optional in Development (the
handler falls back to header-trust mode while no caller secret at all is
registered) but required in every other environment.

### GOV.UK Notify

| Variable | Secret? | Description |
| --- | --- | --- |
| `NOTIFY_API_KEY` | Yes | GOV.UK Notify API key |
| `Notify__Enabled` | No | Master switch for all outbound Notify email + `notification-*` audit entries. `false` in every environment today (RA-422) |
| `Notify__BaseUri` | No | Optional override for the Notify SDK base URI |
| `NOTIFY_SENDEMAILS` | No | Force-overrides the `ENVIRONMENT`-derived decision on whether Notify sends are actually dispatched — used to smoke-test a real send locally |

See [Local development](#local-development) above for the full local
Notify-testing walkthrough.

### Database and misc

| Variable | Secret? | Description |
| --- | --- | --- |
| `Mongo__DatabaseUri` | No | MongoDB connection string (default `mongodb://127.0.0.1:27017`; IAM auth in deployed environments) |
| `Mongo__DatabaseName` | No | MongoDB database name (default `waste-regulator-case-management`) |
| `Cors__AllowedOrigins` | No | CORS allow-list (array). Empty = deny-all |
| `ENVIRONMENT` | No | Platform environment name (`local`/`dev`/`test`/`perf-test`/`ext-test`/`prod`) |

### Example local/testing values

```bash
AUTH_SHARED_SECRET__CASE_INTAKE=local-fake-case-intake-shared-secret-do-not-use
AUTH_SHARED_SECRET__MANAGEMENT_FE=local-fake-managementfe-shared-secret-do-not-use
NOTIFY_API_KEY=local-dev-not-a-real-notify-key
```

A fresh `dotnet run` needs none of these to boot or pass its own health
check — Development falls back to header-trust auth and a no-op Notify client.

## Testing

Tests use [Ephemeral MongoDB](https://github.com/asimmon/ephemeral-mongo)
so they run end-to-end against a real (in-memory) Mongo instance:

```bash
dotnet test
```

### Checks that don't check

One failure shape turned up four times in a single story (RA-292 / epr-2uxy),
found by four different people, and it is worth recognising on sight:

> **A check that appears to exercise a path it never touches.**

Each one failed *quietly*, and in the direction the author was hoping for —
which is why none of them surfaced as a red test.

| Instance | What went wrong | Remedy |
| --- | --- | --- |
| **A dry run that mutates isn't dry** | The migration's dry run flipped values in memory before checking its apply flag, so its "what I would change" report described a state it had already partly created | Run the dry run, then **re-read** the document and assert it is unchanged |
| **`NullLogger` doesn't format** | Tests asserted that logging *happened*. `NullLogger` never invokes the formatter, so two malformed message templates (a duplicate placeholder name is a second positional slot) passed every test and would have thrown `FormatException` on a real run — producing no report at all | Assert on **rendered** output through a logger that actually formats |
| **An unvalidated scan doesn't scan** | A scan for the above reported "0 duplicates". A scan reporting zero is indistinguishable from a scan that is broken | Validate the scanner against a **known positive** first, then report what it examined ("105 templates, 190 placeholders, 0 skipped"), not just the zero |
| **A reproduction with a different failure isn't a reproduction** | A local repro of a CI failure died on `TypeError: fetch failed` where CI failed on assertions. Treating it as the same failure would have meant reporting a merged story as broken when it wasn't | Confirm the repro fails the **same way**, not merely that it fails |

The common remedy, and the thing to carry away:

**Make the check prove itself before you trust its result.** A green check that
never ran is worse than a red one — it spends the credibility of a test without
doing the work of one.

### Git hooks

A `pre-commit` hook in [`.githooks/`](.githooks/) runs the same lint and
test checks as CI (`dotnet format style --verify-no-changes` and
`dotnet test`). Enable it once per clone:

```bash
git config core.hooksPath .githooks
```

Bypass in an emergency with `git commit --no-verify`.

## Frontend integration

The companion frontend
([`epr-register-enrol-management-fe`](../epr-register-enrol-management-fe/))
calls this API server-to-server. With both services running locally the
frontend at `http://localhost:3000/backend-status` reports the backend's
`/health` response, providing an end-to-end smoke test.

To run both services together via Docker Compose, see the
[frontend README](../epr-register-enrol-management-fe/README.md#running-the-full-stack).

## Deployment

This service targets the CDP platform. See
[`docs/cdp-deployment.md`](docs/cdp-deployment.md) for the container port,
required environment variables, secrets, AWS resources and Squid proxy
allow-list. Tracing behaviour is documented in
[`docs/cdp-tracing.md`](docs/cdp-tracing.md). Architecture decisions live
under [`docs/adr/`](docs/adr/).

## Licence

THIS INFORMATION IS LICENSED UNDER THE CONDITIONS OF THE OPEN GOVERNMENT
LICENCE found at: <http://www.nationalarchives.gov.uk/doc/open-government-licence/version/3>.
