using System.Diagnostics.CodeAnalysis;
using WasteRegulatorCaseManagement.Auth;
using WasteRegulatorCaseManagement.Config;
using WasteRegulatorCaseManagement.Health;
using WasteRegulatorCaseManagement.Notifications;
using WasteRegulatorCaseManagement.Utils;
using WasteRegulatorCaseManagement.Utils.Http;
using WasteRegulatorCaseManagement.Utils.Logging;
using WasteRegulatorCaseManagement.Utils.Mongo;
using WasteRegulatorCaseManagement.WorkItems.Core;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MongoDB.Driver;
using MongoDB.Driver.Authentication.AWS;
using Notify.Client;
using Notify.Interfaces;
using Serilog;

var app = BuildApp(args);
await app.RunAsync();

[ExcludeFromCodeCoverage]
static WebApplication BuildApp(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    ConfigureHost(builder);
    ConfigureServices(builder);

    var app = builder.Build();

    LogNotifyClientRegistration(app);

    ConfigureMiddleware(app);
    ConfigureEndpoints(app);

    return app;
}

[ExcludeFromCodeCoverage]
static void ConfigureHost(WebApplicationBuilder builder)
{
    builder.Host.UseSerilog(CdpLogging.Configuration);

    // Suppress the "Server: Kestrel" response header. It's a minor
    // fingerprinting aid with no direct exploit path, but there's no
    // reason to advertise the server stack (pentest 2026-08-08, L4).
    builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
}

[ExcludeFromCodeCoverage]
static void ConfigureServices(WebApplicationBuilder builder)
{
    var services = builder.Services;
    var configuration = builder.Configuration;

    // Trust material must be loaded before anything creates outbound connections.
    services.LoadCustomTrustStoreFromEnvironment();

    // HttpClient.DefaultProxy MUST also be set before any bare HttpClient
    // is constructed. The GovukNotify SDK does `new HttpClient()` and
    // therefore depends on DefaultProxy — in CDP the runtime's
    // automatic env-var detection does not reliably fire, so we wire
    // it from HTTP(S)_PROXY explicitly to keep outbound Notify traffic
    // on the Squid proxy. Without this the SDK egresses direct and
    // hangs on the platform's deny-all firewall
    // (cdp-documentation/how-to/proxy.md).
    //
    // This is process-global: it silently applies to every HttpClient in
    // the process that doesn't explicitly opt out, not just Notify's bare
    // one — including named clients from AddHttpClient(...) that don't
    // configure a primary handler (an omitted handler is NOT the same as
    // "unproxied"; it just inherits this). Any client that must bypass
    // Squid (CDP-service-to-CDP-service calls) has to say so explicitly via
    // ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    // { UseProxy = false }).
    var defaultProxy = ProxyHttpMessageHandler.BuildDefaultProxy();
    if (defaultProxy is not null)
    {
        HttpClient.DefaultProxy = defaultProxy;
    }

    services.AddExceptionHandler<ExceptionLoggingHandler>();
    services.AddProblemDetails();
    services.AddValidation();
    services.AddSingleton(TimeProvider.System);

    // Generic structured-logging facade: caller-defined property bag,
    // routed through ILogger<T> so source-context is preserved. Open
    // generic so consumers depend on IStructuredLogger<TTheirComponent>.
    services.AddSingleton(typeof(IStructuredLogger<>), typeof(StructuredLogger<>));

    // Built-in .NET 10 OpenAPI document generation. Document only — no
    // Swagger UI is wired up (that ships in a separate package). The
    // document is exposed unauthenticated at /openapi/v1.json by
    // ConfigureEndpoints, mirroring the health endpoints' posture, since
    // BFF and platform tooling need to fetch it without credentials.
    services.AddOpenApi(
        "v1",
        options =>
        {
            // Inject concrete request-body examples so the Swagger UI
            // "Try it out" panel pre-fills with real payloads. Runs last in
            // the transformer pipeline to survive schema population. RA-124.
            // AddDocumentTransformer<T> registers the transformer in DI, so
            // no separate AddSingleton call is required.
            options.AddDocumentTransformer<WorkItemOpenApiExampleTransformer>();
        }
    );

    services.AddHttpContextAccessor();

    ConfigureAuth(services, configuration);

    ConfigureHeaderPropagation(services, configuration);
    ConfigureHttpClients(services);
    ConfigureMongo(services, configuration);
    ConfigureCors(services, configuration);
    ConfigureNotifications(services, configuration, builder.Environment);

    services
        .AddOptions<LivenessHealthCheckOptions>()
        .Bind(configuration.GetSection("Liveness"))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services
        .AddHealthChecks()
        // Tagged "live" so liveness (/health) only fires checks tagged
        // "live" (currently the thread-pool probe), and readiness
        // (/health/ready) only fires checks tagged "ready". The two sets
        // are deliberately disjoint so a downed dependency cannot recycle
        // the pod and a wedged process cannot keep serving traffic.
        .AddCheck<LivenessHealthCheck>("threadpool", tags: ["live"])
        .AddCheck<MongoHealthCheck>("mongodb", tags: ["ready"])
        .AddCheck<RequiredConfigHealthCheck>("required-config", tags: ["ready"]);

    ConfigureWorkItems(builder);
}

[ExcludeFromCodeCoverage]
static void ConfigureWorkItems(WebApplicationBuilder builder)
{
    var services = builder.Services;

    // Register the framework, then add one line per work item module.
    // See docs in WasteRegulatorCaseManagement/WorkItems/Core for the contract a module must implement.
    services.AddWorkItemFramework();
    services.AddSingleton<IWorkItemPersistence, WorkItemPersistence>();

    // The demo seeder writes records referencing stub user ids, so it is
    // gated to Development hosts even when WorkItems:SeedOnStartup=true.
    // A misconfiguration warning is emitted at startup if the flag is
    // observed in any other environment. See
    // WorkItemModuleExtensions.AddWorkItemSeeders.
    services.AddWorkItemSeeders(builder.Environment, builder.Configuration);
}

[ExcludeFromCodeCoverage]
static void ConfigureAuth(IServiceCollection services, IConfiguration configuration)
{
    services.AddAuthentication(ClientIdDefaults.AuthenticationScheme).AddClientId();

    // Bind options lazily via PostConfigure so test fixtures that add
    // config via WebApplicationFactory.ConfigureAppConfiguration (which
    // fires during builder.Build(), after this method runs) can still
    // override the value.
    services
        .AddOptions<ClientIdAuthenticationOptions>(ClientIdDefaults.AuthenticationScheme)
        .Configure<IConfiguration>(
            (options, config) =>
            {
                options.ClientSecrets = BuildClientSecrets(config);
                options.MaxClientIdLength = config.GetValue(
                    "Auth:MaxClientIdLength",
                    options.MaxClientIdLength
                );
                options.MaxUserIdLength = config.GetValue(
                    "Auth:MaxUserIdLength",
                    options.MaxUserIdLength
                );
                options.MaxUserNameLength = config.GetValue(
                    "Auth:MaxUserNameLength",
                    options.MaxUserNameLength
                );
                options.MaxSignatureLength = config.GetValue(
                    "Auth:MaxSignatureLength",
                    options.MaxSignatureLength
                );
                options.MaxTimestampLength = config.GetValue(
                    "Auth:MaxTimestampLength",
                    options.MaxTimestampLength
                );
                options.MaxNonceLength = config.GetValue(
                    "Auth:MaxNonceLength",
                    options.MaxNonceLength
                );
            }
        );

    services.AddAuthorization();

    // RA-525: Mongo-backed replay store, shared across every running
    // instance — replaces the old per-process IMemoryCache. Registered
    // behind a Lazy<> (see ClientIdAuthenticationHandler's constructor
    // comment) so constructing the handler — which happens for every
    // request, not just signed ones (RA-105) — never depends on Mongo
    // being reachable.
    services.AddSingleton<IClientIdAuthNonceStore, ClientIdAuthNonceStore>();
    services.AddSingleton(sp => new Lazy<IClientIdAuthNonceStore>(() =>
        sp.GetRequiredService<IClientIdAuthNonceStore>()
    ));
}

// RA-345: per-caller secrets, keyed by the clientId each caller is expected
// to assert. Each caller gets its own secret env var so one can be rotated
// without touching the other — see ADR-0005's "Follow-up: per-caller
// shared secrets" section.
[ExcludeFromCodeCoverage]
static IReadOnlyDictionary<string, string> BuildClientSecrets(IConfiguration config)
{
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    // Config-key form, NOT the literal env var name: EnvironmentVariablesConfigurationProvider
    // rewrites "__" to ":" while loading, so the real env var
    // AUTH_SHARED_SECRET__MANAGEMENT_FE is stored under config key
    // "AUTH_SHARED_SECRET:MANAGEMENT_FE" — a GetValue call using the literal
    // double-underscore string never matches it.
    AddCallerSecret(
        map,
        config,
        callerName: "ManagementFe",
        clientIdKey: "Auth:ManagementFeClientId",
        clientIdDefault: "frontend",
        secretKey: "AUTH_SHARED_SECRET:MANAGEMENT_FE"
    );
    AddCallerSecret(
        map,
        config,
        callerName: "CaseIntake",
        clientIdKey: "Auth:CaseIntakeClientId",
        clientIdDefault: "case-intake-processor",
        secretKey: "AUTH_SHARED_SECRET:CASE_INTAKE"
    );
    return map;
}

[ExcludeFromCodeCoverage]
static void AddCallerSecret(
    Dictionary<string, string> map,
    IConfiguration config,
    string callerName,
    string clientIdKey,
    string clientIdDefault,
    string secretKey
)
{
    var secret = config.GetValue<string>(secretKey);
    if (string.IsNullOrEmpty(secret))
    {
        // Caller not configured yet — same "not signed" posture as today
        // when AUTH_SHARED_SECRET was unset.
        return;
    }

    var clientId = config.GetValue(clientIdKey, clientIdDefault);

    // Fail LOUD: two callers configured to assert the same clientId would
    // mean the second caller's secret silently overwrites the first's in
    // the map, breaking the first caller's signing and defeating the
    // entire point of per-caller secrets (RA-345). Always a config
    // mistake — never a legitimate state — so this throws rather than
    // silently letting the later AddCallerSecret call win.
    if (map.ContainsKey(clientId))
    {
        throw new InvalidOperationException(
            $"ClientIdAuthentication misconfigured: caller '{callerName}' asserts "
                + $"clientId '{clientId}' (via {clientIdKey}), which is already registered to "
                + "another caller. Each caller must have a distinct clientId — check for a "
                + "copy-pasted or missing override."
        );
    }

    map[clientId] = secret;
}

[ExcludeFromCodeCoverage]
static void ConfigureHeaderPropagation(IServiceCollection services, IConfiguration configuration)
{
    var traceHeader = configuration.GetValue<string>("TraceHeader");

    services.AddHeaderPropagation(options =>
    {
        // EXPLICIT allow-list of headers that may be propagated from the
        // incoming request to outbound HttpClient calls. This is a security
        // boundary: anything NOT listed here is dropped on the way out so a
        // crafted upstream request can't leak credentials or auth state to
        // downstream services.
        //
        // Things deliberately NOT propagated:
        //   * Authorization, Cookie  — caller credentials must never be
        //     replayed verbatim to a downstream API.
        //   * x-cdp-auth-signature, x-cdp-auth-timestamp, x-cdp-auth-nonce,
        //     x-api-key — caller-bound to THIS request: the signature is an
        //     HMAC over the trust headers for THIS API, the timestamp
        //     bounds replay against THIS API's clock, and the nonce is
        //     burned in THIS API's replay cache. Forwarding any of them
        //     downstream would either leak the integrity proof to another
        //     service or replay the nonce out of band.
        //   * x-cdp-user-id, x-cdp-user-name,
        //     x-cdp-client-id — identity headers from the BFF are
        //     for THIS service to consume; downstream services that need
        //     them must mint their own.
        //
        // To add a new propagated header, add it here AND document why it
        // is safe to forward.
        if (!string.IsNullOrWhiteSpace(traceHeader))
        {
            // Distributed tracing correlation id. Safe: it carries no
            // authority and is the whole point of header propagation.
            options.Headers.Add(traceHeader);
        }
        // Standard W3C trace context. Safe for the same reason as above.
        options.Headers.Add("traceparent");
        options.Headers.Add("tracestate");
        // Request-id used by some load balancers / API gateways. Safe.
        options.Headers.Add("x-request-id");
    });
}

[ExcludeFromCodeCoverage]
static void ConfigureHttpClients(IServiceCollection services)
{
    services.AddTransient<ProxyHttpMessageHandler>();

    // services.AddHttpClientWithTracing<IExampleClient, ExampleClient>();
    // services.AddHttpClientWithProxy<IExternalClient, ExternalClient>();
}

[ExcludeFromCodeCoverage]
static void ConfigureMongo(IServiceCollection services, IConfiguration configuration)
{
    MongoExtensions.Register();
    MongoConventions.Register();

    services
        .AddOptions<MongoConfig>()
        .Bind(configuration.GetRequiredSection("Mongo"))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services.AddSingleton<IMongoDbClientFactory, MongoDbClientFactory>();
}

/// <summary>
/// GOV.UK Notify wiring (RA-123). When <c>NOTIFY_API_KEY</c> is absent or
/// empty the no-op client is registered so the service still boots in
/// environments without Notify credentials. The no-op client is also
/// registered — API key or not — in environments that
/// <see cref="NotifySendingPolicy"/> says must not dispatch real email
/// (dev and localhost, because the non-production Notify team key has run
/// out of guest-address slots). Otherwise the real GovukNotify SDK client
/// is registered behind the project-owned <see cref="INotifyClient"/>
/// abstraction with Polly retries.
/// </summary>
[ExcludeFromCodeCoverage]
static void ConfigureNotifications(
    IServiceCollection services,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment
)
{
    services.AddOptions<NotifyConfig>().Bind(configuration.GetSection("Notify"));

    var apiKey = configuration.GetValue<string>("NOTIFY_API_KEY");

    // RA-422: outbound Case Management service email is centrally disabled via Notify:Enabled
    // (default false). Register the no-op client when notifications are
    // disabled OR no API key is configured — belt-and-braces, so no real email
    // can leave even if something resolves INotifyClient directly. The
    // notification post-action hook (which also writes the notification-* audit
    // entries) is gated on the same flag in ReAccreditationModule.
    if (!NotifyFeature.NotificationsEnabled(configuration) || string.IsNullOrWhiteSpace(apiKey))
    {
        services.AddSingleton<INotifyClient, NoOpNotifyClient>();
        return;
    }

    var shouldSend = NotifySendingPolicy.ShouldSendEmails(
        configuration.GetValue<bool?>(NotifySendingPolicy.SendEmailsKey),
        CdpEnvironment.Read(),
        hostEnvironment.IsDevelopment()
    );

    if (!shouldSend)
    {
        // A key IS configured, but this environment deliberately does not
        // send. Callers still get success + a notification-sent audit entry.
        services.AddSingleton<INotifyClient>(sp => new NoOpNotifyClient(
            sp.GetRequiredService<IStructuredLogger<NoOpNotifyClient>>(),
            NotifySendingPolicy.SuppressedByEnvironmentReason
        ));
        return;
    }

    var baseUri = configuration.GetValue<string>("Notify:BaseUri");
    services.AddSingleton<IAsyncNotificationClient>(_ =>
        string.IsNullOrWhiteSpace(baseUri)
            ? new NotificationClient(apiKey)
            : new NotificationClient(baseUri, apiKey)
    );
    services.AddSingleton<INotifyClient, GovukNotifyClient>();
}

/// <summary>
/// CORS posture: deny-all by default. The named policy <c>BackendCors</c> is
/// applied with an explicit allow-list of trusted origins read from
/// <c>Cors:AllowedOrigins</c>. With no configured origins the policy is
/// registered with an empty list which means <em>no</em> browser origin can
/// successfully call the API — desired in CDP where calls go through the
/// server-side BFF.
/// </summary>
[ExcludeFromCodeCoverage]
static void ConfigureCors(IServiceCollection services, IConfiguration configuration)
{
    services.AddCors();

    // Bind options lazily via PostConfigure so test fixtures that add
    // Cors:AllowedOrigins via WebApplicationFactory.ConfigureAppConfiguration
    // (which fires during builder.Build(), after this method runs) can still
    // override the value.
    services
        .AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
        .Configure<IConfiguration>(
            (options, config) =>
            {
                var allowedOrigins =
                    config.GetSection("Cors:AllowedOrigins").Get<string[]>()
                    ?? Array.Empty<string>();
                var traceHeader = config.GetValue<string>("TraceHeader");

                options.AddPolicy(
                    "BackendCors",
                    policy =>
                    {
                        if (allowedOrigins.Length == 0)
                        {
                            // Deny-all: no allowed origin and no wildcard. Browser
                            // requests from any origin will receive no CORS headers and
                            // will be blocked by the browser.
                            policy.WithOrigins().DisallowCredentials();
                            return;
                        }

                        // EXPLICIT allow-list of request headers a browser is
                        // permitted to send cross-origin. This is a security
                        // boundary: a header that is NOT here will fail a CORS
                        // preflight and the browser will refuse to issue the
                        // request. Mirror of the propagation allow-list above —
                        // keep them in sync.
                        //
                        // Things deliberately NOT advertised:
                        //   * Authorization, Cookie  — we do not accept caller
                        //     credentials over CORS. CDP traffic reaches this
                        //     service via the server-side BFF, not directly from
                        //     a browser.
                        //   * x-cdp-auth-signature, x-cdp-auth-timestamp,
                        //     x-cdp-auth-nonce, x-api-key — HMAC inputs are
                        //     injected by the BFF server-side and must never
                        //     originate from a browser. The HMAC check remains
                        //     the primary defence; excluding them here ensures a
                        //     browser preflight cannot even smuggle them.
                        //   * x-cdp-user-id, x-cdp-user-name,
                        //     x-cdp-client-id — identity headers are
                        //     BFF-injected and must not be browser-supplied.
                        //
                        // To advertise a new header, add it here AND document why
                        // it is browser-legitimate.
                        var allowedHeaders = new List<string>
                        {
                            // Standard request payload negotiation.
                            "Content-Type",
                            "Accept",
                            // W3C trace context — same headers we propagate
                            // outbound. Carry no authority.
                            "traceparent",
                            "tracestate",
                            "x-request-id",
                        };
                        if (!string.IsNullOrWhiteSpace(traceHeader))
                        {
                            allowedHeaders.Add(traceHeader);
                        }

                        policy
                            .WithOrigins(allowedOrigins)
                            .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                            .WithHeaders([.. allowedHeaders])
                            .DisallowCredentials();
                    }
                );
            }
        );
}

/// <summary>
/// Emit a single startup log entry naming the registered
/// <see cref="INotifyClient"/> implementation and the active Notify
/// configuration. Surfaces silent misconfiguration (missing API key,
/// wrong base URI) in docker / CDP logs the moment the host starts.
/// </summary>
[ExcludeFromCodeCoverage]
static void LogNotifyClientRegistration(WebApplication app)
{
    var notifyClient = app.Services.GetRequiredService<INotifyClient>();
    var notifyOptions = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotifyConfig>>()
        .Value;
    var apiKeyConfigured = !string.IsNullOrWhiteSpace(
        app.Configuration.GetValue<string>("NOTIFY_API_KEY")
    );
    var cdpEnvironment = CdpEnvironment.Read();
    var sendingEnabled = NotifySendingPolicy.ShouldSendEmails(
        app.Configuration.GetValue<bool?>(NotifySendingPolicy.SendEmailsKey),
        cdpEnvironment,
        app.Environment.IsDevelopment()
    );
    app.Logger.LogInformation(
        "Notify integration: enabled={NotificationsEnabled} client={NotifyClientType} "
            + "apiKeyConfigured={ApiKeyConfigured} sendingEnabled={NotifySendingEnabled} "
            + "environment={CdpEnvironment} baseUri={NotifyBaseUri} "
            + "timeoutSeconds={NotifyTimeoutSeconds} templates={NotifyTemplateCount}",
        NotifyFeature.NotificationsEnabled(app.Configuration),
        notifyClient.GetType().FullName,
        apiKeyConfigured,
        sendingEnabled,
        string.IsNullOrWhiteSpace(cdpEnvironment) ? "<unset>" : cdpEnvironment,
        string.IsNullOrWhiteSpace(notifyOptions.BaseUri) ? "<sdk-default>" : notifyOptions.BaseUri,
        notifyOptions.RequestTimeoutSeconds,
        notifyOptions.Templates.Count
    );
}

[ExcludeFromCodeCoverage]
static void ConfigureMiddleware(WebApplication app)
{
    // ExceptionHandler MUST be the very first middleware so unhandled
    // exceptions thrown anywhere downstream are caught and translated into
    // an RFC 7807 ProblemDetails response (configured via
    // services.AddProblemDetails()) instead of leaking stack traces or
    // returning a bare 500.
    app.UseExceptionHandler();
    // StatusCodePages turns plain status-only responses (e.g. a 404 from
    // routing) into ProblemDetails too, for a uniform error shape.
    app.UseStatusCodePages();

    app.UseSerilogRequestLogging();

    app.UseHeaderPropagation();

    // CORS must run before Auth so preflight (OPTIONS) responses are
    // produced for browser clients before authorisation kicks in.
    app.UseCors("BackendCors");

    app.UseAuthentication();
    app.UseAuthorization();
}

[ExcludeFromCodeCoverage]
static void ConfigureEndpoints(WebApplication app)
{
    // Liveness: only "live"-tagged checks run. Currently the thread-pool
    // probe (LivenessHealthCheck) which detects a wedged process — a
    // bare "answer 200 if the pipeline parsed the request" probe would
    // never let Kubernetes / CDP recycle a deadlocked or thread-pool-
    // starved pod.
    app.MapHealthChecks(
            "/health",
            new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") }
        )
        .AllowAnonymous()
        .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get, HttpMethods.Head }));
    // Readiness: only the "ready"-tagged checks run (currently MongoDB).
    // CDP / Kubernetes uses this to decide when to send traffic.
    app.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }
        )
        .AllowAnonymous()
        .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get, HttpMethods.Head }));

    // OpenAPI document at the conventional /openapi/{documentName}.json
    // route. Anonymous on purpose: the BFF and platform tooling fetch it
    // unauthenticated, same posture as /health. Always-on (not gated on
    // IsDevelopment) because this service sits behind the BFF inside CDP
    // and the document is internal-platform-facing rather than public.
    app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();

    // Swagger UI explorer at /swagger, gated by SwaggerUiGating: enabled
    // outside Production, or anywhere that opts in via Swagger:Enabled.
    // Anonymous on purpose, mirroring the /health and /openapi posture —
    // the underlying document is already anonymous so authenticating the
    // explorer page would be theatre. Production stays off by default so
    // the explorer is not exposed to platform traffic without an explicit
    // operator decision.
    if (SwaggerUiGating.ShouldEnableSwaggerUi(app.Environment, app.Configuration))
    {
        // Serve the stub-user picker JS that augments the Swagger UI
        // topbar with a dev-only dropdown. Anonymous: the JS contains no
        // secrets, only the dev-fixture stub user list. RA-124.
        app.MapGet(
                SwaggerUiStubUserAssets.ScriptPath,
                () => Results.Content(SwaggerUiStubUserAssets.ScriptBody, "application/javascript")
            )
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/v1.json", "EPR Management BE v1");
            options.RoutePrefix = "swagger";
            // Inject the stub-user picker into the topbar. The script
            // also monkey-patches window.fetch so every Try it out call
            // carries the four CDP trust headers for the selected user
            // — Swashbuckle's UseRequestInterceptor is unreliable with
            // arrow-function bodies, so we bypass it.
            options.InjectJavascript(SwaggerUiStubUserAssets.ScriptPath);
        });
    }

    app.MapWorkItemFrameworkEndpoints();
    app.MapWorkItemModules();
}
