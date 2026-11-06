# CDP request tracing

CDP propagates a request id via the `x-cdp-request-id` HTTP header. Every
service in the request chain must read it from inbound requests, enrich logs
with it, and forward it on outbound HTTP calls.

## Verification

| Concern | Status | Reference |
| --- | --- | --- |
| Header name | ✅ `x-cdp-request-id` | [`appsettings.json`](../WasteRegulatorCaseManagement/appsettings.json) `TraceHeader` |
| Inbound header is propagated on outbound HttpClients | ✅ Wired via `AddHeaderPropagation` | [`Program.cs`](../WasteRegulatorCaseManagement/Program.cs) `ConfigureHeaderPropagation` |
| Logs enriched with trace id | ✅ Serilog `Enrich.WithCorrelationId(traceIdHeader)` | [`Utils/Logging/CdpLogging.cs`](../WasteRegulatorCaseManagement/Utils/Logging/CdpLogging.cs) |
| Outbound clients forward the header | ✅ The source system callback client adds header propagation and bypasses the outbound proxy | [`Cases/CaseModuleExtensions.cs`](../WasteRegulatorCaseManagement/Cases/CaseModuleExtensions.cs) |

## When adding a downstream HttpClient

1. Register via `services.AddHttpClientWithTracing<IFoo, FooClient>();` (see
   `WasteRegulatorCaseManagement/Utils/Http/HttpClientRegistrationExtension.cs`),
   or add `.AddHeaderPropagation()` to a named client.
2. Confirm the request log on the receiver shows the same `trace.id` as the
   inbound request.
3. If the client must traverse the CDP outbound proxy, prefer
   `AddHttpClientWithProxy` so the `ProxyHttpMessageHandler` and tracing are
   both attached. A CDP service reached directly must not use the proxy.
