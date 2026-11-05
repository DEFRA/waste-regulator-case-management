# Waste Regulator Case Management

A .NET 10 API that holds the cases regulators work through. A source system's
Case Intake Processor creates a case for each submission; caseworkers, through
the regulator frontend, assign it, add notes and conclude it; the service then
sends the decision back to the source system. Every change is recorded on the
case's timeline and published to the platform's analytics topic.

The first case type is CSoC, for compliance declarations held by the Waste
Obligations API. Case types are configured in JSON, not code — see
[docs/cases.md](docs/cases.md).

Built from
[cdp-dotnet-backend-template](https://github.com/DEFRA/cdp-dotnet-backend-template).

- [Requirements](#requirements)
- [Local development](#local-development)
- [Running with Docker Compose](#running-with-docker-compose)
- [Endpoints](#endpoints)
- [Authentication](#authentication)
- [Configuration](#configuration)
- [Testing](#testing)
- [Deployment](#deployment)
- [Licence](#licence)

## Requirements

- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://www.docker.com/) and Docker Compose (for the Docker workflow)
- [MongoDB 7+](https://www.mongodb.com/docs/manual/installation/) running
  locally on `mongodb://127.0.0.1:27017` as a replica set, because case
  writes use transactions (or use the Docker Compose stack, which starts a
  single-node replica set)

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

If you do not have MongoDB installed locally, start just the database from
the Compose stack:

```bash
docker compose up -d mongodb
```

The MongoDB connection is configured in
[`WasteRegulatorCaseManagement/appsettings.Development.json`](WasteRegulatorCaseManagement/appsettings.Development.json)
and can be overridden via the `Mongo__DatabaseUri` and `Mongo__DatabaseName`
environment variables.

Outside the Production environment (or with `Swagger__Enabled=true`) the
Swagger UI explorer is available at <http://localhost:8085/swagger>.

The explorer has a dev-only **"Authenticate as:"** dropdown in the topbar
(`stub-caseworker-1`, `stub-caseworker-2`, `stub-caseworker-3`). Pick a user
and every "Try it out" call is sent with that user's trust headers:
`x-cdp-client-id`, `x-cdp-user-id`, `x-cdp-user-name`, `x-cdp-user-nation`
(`ENGLAND`) and `x-cdp-user-email`. The selection persists in the browser's
`localStorage`. Picking "— anonymous —" clears it.

The picker's client id, `local-swagger-ui`, is not the Case Intake Processor, so
`POST /cases` returns 403 from Swagger; create cases with `curl` and the
`x-cdp-client-id: case-intake-processor` header instead.

> The cURL preview Swagger UI shows in the response panel is generated
> *before* the request is sent and does **not** include the headers
> attached by the picker. The actual XHR over the wire does — check the
> server response, not the cURL snippet, to confirm authentication.

Concluding a CSoC case calls its `callbackUrl`, so locally a case can only be
accepted or cancelled when that URL points at something that answers.

## Running with Docker Compose

The repository ships a Compose stack that builds the API image and starts
its dependencies: MongoDB as a single-node replica set, and Floci, an AWS
emulator with the analytics SNS topic created and publishing enabled:

```bash
docker compose up --build -d
```

Once the stack is healthy the API is reachable on <http://localhost:8085/swagger>.
Tear it down with:

```bash
docker compose down -v
```

## Endpoints

| Method | Path                             | Caller                    | Description                            |
| ------ | -------------------------------- | ------------------------- | -------------------------------------- |
| GET    | `/health`                        | Anyone                    | Liveness probe used by CDP             |
| GET    | `/health/ready`                  | Anyone                    | Readiness probe                        |
| GET    | `/openapi/v1.json`               | Anyone                    | OpenAPI document                       |
| GET    | `/swagger`                       | Anyone                    | Swagger UI explorer (non-Production)   |
| POST   | `/cases`                         | Case Intake Processor     | Create a case for a submission         |
| GET    | `/cases`                         | Caseworker                | List cases in the caller's nation      |
| GET    | `/cases/{id}`                    | Caseworker                | Get a case                             |
| POST   | `/cases/{id}/actions/{actionId}` | Caseworker                | Apply an action, such as `accept`      |
| POST   | `/cases/{id}/assign`             | Caseworker                | Assign a case to a caseworker          |
| POST   | `/cases/{id}/unassign`           | Caseworker                | Unassign a case                        |
| POST   | `/cases/{id}/notes`              | Caseworker                | Add a note to a case                   |

The request and response shapes are in [docs/cases.md](docs/cases.md) and the
OpenAPI document.

## Authentication

Every case endpoint requires a client id in the `x-cdp-client-id` header.
CDP does not verify it: outside Development the caller must also sign the
request with HMAC-SHA256 over a canonical payload, using the secret
registered for that client id (see
[BFF signing contract](docs/cdp-deployment.md#bff-signing-contract)). Two
callers are registered:

- **The Case Intake Processor** is the only caller that can create cases.
- **The regulator frontend** acts for a caseworker, forwarding the user's id,
  name, nation and email as signed headers. A caseworker sees and acts on the
  cases of their own nation only.

In Development only, with no client secrets configured, the handler trusts
the headers without a signature:

```bash
curl -H 'x-cdp-client-id: frontend' -H 'x-cdp-user-id: stub-caseworker-1' \
  -H 'x-cdp-user-nation: ENGLAND' http://localhost:8085/cases
```

Requests without the header (or, outside Development, without a valid
signature) receive `401 Unauthorized`.

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
| `AUTH_SHARED_SECRET__MANAGEMENT_FE` | Yes | Verifies inbound signed requests from the regulator frontend — must match the secret it signs with exactly |
| `CALLBACK_AUTH__CSOC` | Yes | `clientId:clientSecret` for the Waste Obligations API, sent as Basic auth on the CSoC decision callback |
| `Auth__CaseIntakeClientId` | No | Expected `x-cdp-client-id` on inbound calls from the Case Intake Processor (default `case-intake-processor`) |
| `Auth__ManagementFeClientId` | No | Expected `x-cdp-client-id` on inbound calls from the regulator frontend (default `frontend`) — must differ from `Auth__CaseIntakeClientId` or the service throws at first request |

The `CASE_INTAKE` and `MANAGEMENT_FE` secrets are optional in Development (the
handler falls back to header-trust mode while no caller secret at all is
registered) but required in every other environment.

### GOV.UK Notify

| Variable | Secret? | Description |
| --- | --- | --- |
| `NOTIFY_API_KEY` | Yes | GOV.UK Notify API key |
| `Notify__Enabled` | No | Master switch for all outbound Notify email (default `false`) |
| `Notify__BaseUri` | No | Optional override for the Notify SDK base URI |
| `NOTIFY_SENDEMAILS` | No | Force-overrides the `ENVIRONMENT`-derived decision on whether Notify sends are actually dispatched — used to smoke-test a real send locally |

### Analytics

| Variable | Secret? | Description |
| --- | --- | --- |
| `AnalyticsAuditEventProcessor__ProcessingEnabled` | No | Publish audit events to the analytics SNS topic (default `false`; `true` in Docker Compose) |
| `AnalyticsAuditEventProcessor__TopicArn` | No | Analytics SNS topic ARN, required when processing is enabled |

### Database and misc

| Variable | Secret? | Description |
| --- | --- | --- |
| `Mongo__DatabaseUri` | No | MongoDB connection string to a replica set (default `mongodb://127.0.0.1:27017/?directConnection=true`; IAM auth in deployed environments) |
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

### Git hooks

A `pre-commit` hook in [`.githooks/`](.githooks/) runs the same lint and
test checks as CI (`dotnet format style --verify-no-changes` and
`dotnet test`). Enable it once per clone:

```bash
git config core.hooksPath .githooks
```

Bypass in an emergency with `git commit --no-verify`.

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
