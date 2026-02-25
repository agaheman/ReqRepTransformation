# ReqRepTransformation — AI Specification

> **Purpose of this document:** Send this spec to any AI coding assistant to reproduce the full `ReqRepTransformation` solution faithfully. Every architectural decision, interface contract, constraint, and naming convention is captured here.

---

## 1. Solution Identity

| Property | Value |
|---|---|
| Solution name | `ReqRepTransformation` |
| Target framework | `net9.0` |
| Language version | C# 13 |
| Nullable | enabled |
| Implicit usings | enabled |
| CS1591 (missing XML doc) | suppressed via `.editorconfig` |
| No YARP | Do NOT reference or integrate YARP |
| No EF Core | Persistence is out of scope in the core library |

---

## 2. Project Structure

```
ReqRepTransformation/
├── .editorconfig                          ← CS1591 suppressed for *.cs
├── ReqRepTransformation.sln
├── SolutionItems/
│   ├── AI_SPEC.md
│   └── HOW_TO_USE.md
├── src/
│   ├── ReqRepTransformation.Core/         ← Abstractions, models, pipeline engine
│   ├── ReqRepTransformation.AspNetCore/   ← ASP.NET Core middleware adapter
│   └── ReqRepTransformation.Transforms/   ← Built-in transform implementations
├── tests/
│   ├── ReqRepTransformation.Core.Tests/
│   ├── ReqRepTransformation.AspNetCore.Tests/
│   └── ReqRepTransformation.Transforms.Tests/
└── samples/
    └── SampleApiTestApp/                  ← Working ASP.NET Core 9 sample
```

---

## 3. Core Abstractions (`ReqRepTransformation.Core/Abstractions/`)

### 3.1 `IMessageContext`
Transport-agnostic message handle. Transforms operate ONLY on this interface — never on `HttpContext` or `HttpRequestMessage`.

```csharp
public interface IMessageContext
{
    string Method { get; set; }
    Uri Address { get; set; }
    IMessageHeaders Headers { get; }
    IPayload Payload { get; }
    CancellationToken Cancellation { get; }
    MessageSide Side { get; }
}
```

### 3.2 `IMessageHeaders`
```csharp
public interface IMessageHeaders
{
    IEnumerable<string> Keys { get; }
    string? Get(string key);
    IEnumerable<string> GetValues(string key);
    void Set(string key, string value);
    void Append(string key, string value);
    void Remove(string key);
    bool Contains(string key);
    bool TryGet(string key, out string? value);
}
```

### 3.3 `IPayload` — Zero-double-serialization contract
```csharp
public interface IPayload
{
    bool HasBody { get; }
    bool IsJson { get; }
    bool IsStreaming { get; }
    string? ContentType { get; }

    ValueTask<JsonNode?> GetJsonAsync(CancellationToken ct = default);
    ValueTask<ReadOnlyMemory<byte>> GetBufferAsync(CancellationToken ct = default);
    ValueTask<PipeReader> GetPipeReaderAsync(CancellationToken ct = default);
    ValueTask SetJsonAsync(JsonNode node, CancellationToken ct = default);
    ValueTask SetBufferAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);
    ValueTask ReplaceStreamAsync(Stream stream, CancellationToken ct = default);
    ValueTask<ReadOnlyMemory<byte>> FlushAsync(CancellationToken ct = default);
}
```

**Critical invariants:**
- `GetJsonAsync()` parses the body **exactly once** and caches the `JsonNode`. Subsequent calls return the same reference.
- Transforms mutate the `JsonNode` **in-place** — no need to call `SetJsonAsync` for field-level mutations.
- `FlushAsync()` serializes the `JsonNode` **exactly once** at pipeline exit. Never called by individual transforms.
- `GetBufferAsync()` throws `PayloadAccessViolationException` if `IsStreaming == true`.
- `GetPipeReaderAsync()` is the **only** valid call from `IStreamTransform` implementations.

### 3.4 `ITransformation` + Marker Interfaces
```csharp
public interface ITransformation
{
    string Name { get; }          // kebab-case, unique, stable
    bool ShouldApply(IMessageContext context);   // sync, allocation-free
    ValueTask ApplyAsync(IMessageContext context, CancellationToken ct);
}

public interface IBufferTransform : ITransformation { }  // may access JSON/buffer
public interface IStreamTransform : ITransformation { }  // must ONLY use GetPipeReaderAsync
```

### 3.5 `ITransformationDetailProvider`
```csharp
public interface ITransformationDetailProvider
{
    ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context, CancellationToken ct = default);
}
```
Called once per request at pipeline start. Cache aggressively by method+path.

### 3.6 `ITransformCircuitBreaker`
```csharp
public interface ITransformCircuitBreaker
{
    bool IsOpen(string transformName);
    void RecordSuccess(string transformName);
    void RecordFailure(string transformName);
    void Reset(string transformName);
    CircuitState GetState(string transformName);
}
public enum CircuitState { Closed, Open, HalfOpen }
```

### 3.7 `IRedactionPolicy`
```csharp
public interface IRedactionPolicy
{
    bool ShouldRedact(string key);
    string Redact(string key, string value);
}
```

---

## 4. Models (`ReqRepTransformation.Core/Models/`)

### 4.1 `TransformEntry` — Order wrapper
```csharp
public sealed record TransformEntry
{
    public int Order { get; init; }             // ASC execution order. Convention: 10, 20, 30...
    public ITransformation Transform { get; init; }

    public TransformEntry(int order, ITransformation transform);
    public static TransformEntry At(int order, ITransformation transform);
}
```

**Design rationale:** `Order` lives on `TransformEntry`, NOT on `ITransformation`, because:
1. The same transform instance may be shared across multiple routes with different orders.
2. Order is a pipeline-configuration concern, not a transform behaviour concern (SRP).

### 4.2 `TransformationDetail`
```csharp
public sealed record TransformationDetail
{
    public IReadOnlyList<TransformEntry> RequestTransformations { get; init; }  = [];
    public IReadOnlyList<TransformEntry> ResponseTransformations { get; init; } = [];
    public TimeSpan TransformationTimeout { get; init; } = TimeSpan.Zero;      // Zero = use PipelineOptions.DefaultTimeout
    public FailureMode FailureMode { get; init; } = FailureMode.LogAndSkip;
    public bool HasExplicitFailureMode { get; init; } = false;                 // false = use PipelineOptions.DefaultFailureMode
    public bool AllowParallelNonDependentTransforms { get; init; } = false;
    public static TransformationDetail Empty { get; }
}
```

### 4.3 Enums
```csharp
public enum FailureMode  { StopPipeline = 0, Continue = 1, LogAndSkip = 2 }
public enum MessageSide  { Request = 0, Response = 1 }
```

### 4.4 `PipelineOptions` (appsettings.json: `"ReqRepTransformation"`)
```csharp
public sealed class PipelineOptions
{
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public FailureMode DefaultFailureMode { get; set; } = FailureMode.LogAndSkip;
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();
    public IList<string> RedactedHeaderKeys { get; set; }   // Authorization, Cookie, X-Api-Key...
    public IList<string> RedactedQueryKeys { get; set; }    // access_token, api_key, token...
}
```

### 4.5 Exceptions
```csharp
public sealed class TransformationException : Exception
{
    public string TransformName { get; }
    public MessageSide Side { get; }
}
public sealed class PayloadAccessViolationException : InvalidOperationException { }
```

---

## 5. Pipeline Engine (`ReqRepTransformation.Core/Pipeline/`)

### 5.1 `PipelineExecutor` — Key behaviours

1. **Sort by `TransformEntry.Order` ASC** before execution. `OrderBy(e => e.Order).ToArray()`.
2. **`_options` fallback resolution** (CRITICAL — `_options` must always be used):
   - `ResolveTimeout`: `detail.TransformationTimeout > Zero ? detail.TransformationTimeout : _options.DefaultTimeout`
   - `ResolveFailureMode`: `detail.HasExplicitFailureMode ? detail.FailureMode : _options.DefaultFailureMode`
3. Per-transform **linked `CancellationTokenSource`** with `CancelAfter(effectiveTimeout)`.
4. **Circuit breaker** checked before `ShouldApply`. Open circuit → skip or throw per `FailureMode`.
5. **Payload type guard**: `IBufferTransform` on streaming payload → silent skip + warning log.
6. **FailureMode handling**: `StopPipeline` throws `TransformationException`; others log and continue.
7. **OTEL span** per transform with tags: `transform.name`, `transform.side`, `transform.result`, `transform.order`.
8. **[LoggerMessage] source generators** for all log calls (zero allocation).
9. `HasExplicitFailureMode = false` MUST fall back to `_options.DefaultFailureMode` to prevent accidental `StopPipeline` (enum default = 0).

### 5.2 `PayloadContext` — Implementation
- Internal `_cachedJson` (`JsonNode?`) populated lazily on first `GetJsonAsync()`.
- `SemaphoreSlim(1,1)` guards JSON parse for concurrent access in parallel-transform mode.
- Double-check pattern inside semaphore.
- `FlushAsync()` priority: `_replacedStream > _isJsonDirty > _isBufferDirty > original buffer`.
- `RecyclableMemoryStreamManager` for all stream allocations (no LOH pressure).
- `PipeReader.AdvanceTo(buffer.End)` always called — no hang risk.

### 5.3 `MessageContextBase`
Abstract base for adapters. Holds `Side` and `Cancellation`. Subclassed by:
- `AspNetRequestMessageContext`
- `AspNetResponseMessageContext`

---

## 6. Infrastructure (`ReqRepTransformation.Core/Infrastructure/`)

### 6.1 `SlidingWindowCircuitBreaker`
- Lock-free sliding window using `int[]` circular array + `Interlocked` operations. **No Polly dependency.**
- States: `Closed → Open → HalfOpen → Closed`.
- `Interlocked.CompareExchange` for CAS transitions (only one thread wins the Open transition).
- `Environment.TickCount64` for open-duration tracking.

### 6.2 `PooledMemoryManager`
- Wraps `RecyclableMemoryStreamManager` as a `static` process-wide singleton.
- Use stable positional constructor: `new RecyclableMemoryStreamManager(blockSize, largeBufferMultiple, maximumBufferSize)`.
- Do NOT use the `Options` object API — `ThrowExceptionOnToLargeBufferToPool` does not exist in v3.x.

### 6.3 Logging
- **All log calls use `[LoggerMessage]` partial methods** — zero allocation in hot path.
- Events: `PipelineStarting`, `PipelineCompleted`, `TransformExecuting`, `TransformCompleted`, `TransformSkipped`, `TransformFailed`, `TransformTimedOut`, `PipelineAborted`, `CircuitOpen`, `CircuitClosed`, `CircuitOpened`, `BufferTransformStreamAccessViolation`.
- `IRedactionPolicy` applied before any header value reaches a log call.

### 6.4 Telemetry
- `ActivitySource` name: `"ReqRepTransformation"`.
- One parent `Activity` per pipeline execution; one child `Activity` per transform.
- Tags: `transform.name`, `transform.side`, `transform.result`, `transform.order`, `pipeline.side`, `payload.content_type`, `http.request.method`.
- `Counter<long>` metrics: `reqrep.transform.executed`, `reqrep.transform.skipped`, `reqrep.transform.failed`, `reqrep.circuit.opened`.

---

## 7. ASP.NET Core Adapter (`ReqRepTransformation.AspNetCore/`)

### 7.1 `GatewayTransformMiddleware`
```
Request flow:
1. Build AspNetRequestMessageContext (wraps HttpContext.Request)
2. Call ITransformationDetailProvider.GetTransformationDetailAsync()
3. Run IMessageTransformationPipeline.ExecuteRequestAsync()
4. Swap Response.Body → RecyclableMemoryStream (BEFORE _next)
5. await _next(context)
6. Restore original Response.Body in finally block (ALWAYS)
7. Capture body bytes from swap stream
8. Build AspNetResponseMessageContext with captured bytes
9. Run IMessageTransformationPipeline.ExecuteResponseAsync()
10. Write final body (FlushAsync result) to original Response.Body
11. Update Content-Length if body length changed
```

### 7.2 Adapters
- `AspNetHeaderAdapter`: wraps `IHeaderDictionary`. `GetValues()` filters null entries explicitly.
- `AspNetRequestMessageContext`: reads body via `Request.BodyReader` (PipeReader). Builds URI from scheme/host/path/query.
- `AspNetResponseMessageContext`: created from captured byte buffer after `_next`. Exposes `GetFinalBodyAsync()` for post-transform serialization.

### 7.3 DI Registration
```csharp
services.AddReqRepTransformationAspNet(options => { ... });
app.UseReqRepTransformation();
```

---

## 8. Built-in Transforms (`ReqRepTransformation.Transforms/`)

### Headers
| Type | Name | Parameters |
|---|---|---|
| `AddHeaderTransform` | `add-header:{key}` | key, value, overwrite=true |
| `RemoveHeaderTransform` | `remove-header:{key}` | key |
| `RenameHeaderTransform` | `rename-header:{from}→{to}` | fromKey, toKey |
| `AppendHeaderTransform` | `append-header:{key}` | key, value |
| `CorrelationIdTransform` | `correlation-id-inject` | headerName (default: X-Correlation-Id) |
| `RequestIdPropagationTransform` | `request-id-propagation` | — |
| `RemoveInternalResponseHeadersTransform` | `remove-internal-response-headers` | headersToRemove (optional) |
| `GatewayResponseTagTransform` | `gateway-response-tag` | version, instanceId |
| `UploadMetadataHeaderTransform` | `upload-metadata-header` | — (IStreamTransform) |

### Address
| Type | Name | Parameters |
|---|---|---|
| `PathPrefixRewriteTransform` | `path-prefix-rewrite:{from}` | fromPrefix, toPrefix |
| `PathRegexRewriteTransform` | `path-regex-rewrite` | pattern (compiled, 100ms timeout), replacement |
| `AddQueryParamTransform` | `add-query:{key}` | key, value |
| `RemoveQueryParamTransform` | `remove-query:{key}` | key |
| `HostRewriteTransform` | `host-rewrite:{host}` | host, port?, scheme? |
| `MethodOverrideTransform` | `method-override:{method}` | newMethod, onlyIfCurrentMethod? |

### JSON Body
| Type | Name | Notes |
|---|---|---|
| `JsonFieldAddTransform` | `json-field-add:{field}` | Clones value via re-parse |
| `JsonFieldRemoveTransform` | `json-field-remove:{field}` | |
| `JsonFieldRenameTransform` | `json-field-rename:{from}→{to}` | |
| `JsonGatewayMetadataTransform` | `json-gateway-metadata` | Injects `_gateway: {version, processedAt, requestId}` |
| `JsonNestedFieldSetTransform` | `json-nested-set:{path}` | dot-separated path, creates intermediate objects |

### Auth
| Type | Name | Notes |
|---|---|---|
| `JwtForwardTransform` | `jwt-forward` | Passthrough — Authorization forwarded implicitly |
| `JwtClaimsExtractTransform` | `jwt-claims-extract` | `Dictionary<claimType, headerName>` |
| `StripAuthorizationTransform` | `strip-authorization` | Removes Authorization before downstream |

---

## 9. ITransformationDetailProvider — Implementation Contract

```csharp
public sealed class MyProvider : ITransformationDetailProvider
{
    public ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context, CancellationToken ct = default)
    {
        return ValueTask.FromResult(new TransformationDetail
        {
            RequestTransformations = new[]
            {
                TransformEntry.At(10, new CorrelationIdTransform()),
                TransformEntry.At(20, new JwtForwardTransform()),
                TransformEntry.At(30, new JsonGatewayMetadataTransform())
            },
            ResponseTransformations = new[]
            {
                TransformEntry.At(10, new RemoveInternalResponseHeadersTransform()),
                TransformEntry.At(20, new GatewayResponseTagTransform())
            },
            TransformationTimeout  = TimeSpan.FromSeconds(3),
            FailureMode            = FailureMode.LogAndSkip,
            HasExplicitFailureMode = true   // ← ALWAYS set when specifying FailureMode
        });
    }
}
```

Cache by method + normalized path (replace numeric/GUID segments with `{id}`).

---

## 10. Hard Constraints (Non-Negotiable)

### MUST
1. `_options.DefaultTimeout` used when `detail.TransformationTimeout == TimeSpan.Zero`.
2. `_options.DefaultFailureMode` used when `detail.HasExplicitFailureMode == false`.
3. `PipelineExecutor` sorts `TransformEntry` list by `Order` ASC before execution.
4. `GetJsonAsync()` parses body exactly once — cached on first call.
5. `FlushAsync()` serializes exactly once — called only at pipeline exit, never by transforms.
6. Transforms operate only on `IMessageContext` — zero `HttpContext` or `HttpRequestMessage` references inside transform code.
7. All transforms return `ValueTask`, not `Task`.
8. Circuit breaker uses `Interlocked` + circular array — no Polly dependency.
9. All log calls use `[LoggerMessage]` partial methods — no raw `ILogger.Log()`.
10. `IRedactionPolicy` applied before any header value reaches any log call.
11. `PooledMemoryManager` uses positional `RecyclableMemoryStreamManager` constructor — NOT the `Options` class (property `ThrowExceptionOnToLargeBufferToPool` does not exist in v3.x).
12. `AspNetHeaderAdapter.GetValues()` must filter null entries (fix for CS8619).
13. `Response.Body` swap restored in `finally` block — always, even if `_next` throws.

### MUST NOT
1. Must not buffer streaming bodies (`IsStreaming == true`) in `IBufferTransform`.
2. Must not call `GetJsonAsync()` or `GetBufferAsync()` from `IStreamTransform` implementations.
3. Must not log `Authorization` header value without redaction.
4. Must not use `ToList()` or LINQ in the inner transform execution loop.
5. Must not use blocking waits or `Thread.Sleep`.
6. Must not reference YARP anywhere.
7. Must not use the `RecyclableMemoryStreamManager.Options` nested class.

---

## 11. Test Strategy

### Fakes
- `MessageContextFake`: in-memory `IMessageContext` with `FakeHeaderDictionary`. Factory methods: `Create()`, `CreateWithJson(JsonNode)`, `CreateWithBuffer(buffer, contentType)`.
- `FakeHeaderDictionary`: `Dictionary<string, List<string>>` (case-insensitive).

### Core tests must cover
- `PayloadContext`: lazy JSON parse, same reference returned on repeated calls, `FlushAsync` round-trip, streaming violation throws, dirty/clean flush paths.
- `PipelineExecutor`: Order ASC sorting, `ShouldApply=false` skip, `LogAndSkip` continues after failure, `StopPipeline` throws `TransformationException`, circuit-open skip, `HasExplicitFailureMode=false` uses `_options.DefaultFailureMode`.
- `SlidingWindowCircuitBreaker`: opens at threshold, stays closed below threshold, closes after `RecordSuccess` from HalfOpen, `Reset()` closes open circuit, thread-safety under concurrent load.

### Transform tests must cover
- Every built-in transform: `ShouldApply` guard, `ApplyAsync` mutation, edge cases (header absent, JSON not an object, path mismatch).

### Integration tests (TestServer)
- Correlation ID injected when absent.
- JSON body mutated (`_gateway` field present after `JsonGatewayMetadataTransform`).
- Response header added from response-side transform.
- Pass-through with no transforms returns original body.
- **Order test**: transforms registered at orders 30, 10, 20 execute in order 10 → 20 → 30.

---

## 12. NuGet Package Versions (net9.0)

```xml
Microsoft.Extensions.DependencyInjection.Abstractions  9.0.0
Microsoft.Extensions.Logging.Abstractions              9.0.0
Microsoft.Extensions.Options                           9.0.0
Microsoft.IO.RecyclableMemoryStream                    3.0.1
System.IO.Pipelines                                    9.0.0
OpenTelemetry                                          1.9.0
OpenTelemetry.Api                                      1.9.0
System.IdentityModel.Tokens.Jwt                        8.0.2  (Transforms project only)

<!-- Tests -->
Microsoft.NET.Test.Sdk                                 17.12.0
xunit                                                  2.9.2
xunit.runner.visualstudio                              2.8.2
FluentAssertions                                       7.0.0
NSubstitute                                            5.3.0
Microsoft.AspNetCore.Mvc.Testing                       9.0.0  (AspNetCore.Tests only)

<!-- Sample -->
Microsoft.AspNetCore.Authentication.JwtBearer          9.0.0
OpenTelemetry.Extensions.Hosting                       1.9.0
OpenTelemetry.Instrumentation.AspNetCore               1.9.0
OpenTelemetry.Exporter.Console                         1.9.0
Swashbuckle.AspNetCore                                 7.2.0
```

---

## 13. Naming Conventions

| Concern | Convention |
|---|---|
| Transform `Name` property | kebab-case, e.g. `"correlation-id-inject"`, `"jwt-forward"` |
| `TransformEntry.Order` | Multiples of 10: 10, 20, 30 ... (leaves room for insertion) |
| Namespaces | `ReqRepTransformation.{Layer}.{Subfolder}` |
| Internal adapters | `internal sealed class` |
| All public types | `public sealed class` or `public sealed record` |
| Interfaces | `I` prefix, PascalCase |
| Async methods | `Async` suffix, return `ValueTask` not `Task` |
| Logging event IDs | Core: 1000–1599. Reserve 2000+ for custom providers. |

---

## 14. Extension Points

| Extension Point | How |
|---|---|
| Custom transform | Implement `IBufferTransform` or `IStreamTransform`, register in DI |
| Custom detail provider | Implement `ITransformationDetailProvider`, register as singleton |
| Custom circuit breaker | Implement `ITransformCircuitBreaker`, replace default DI registration |
| Custom redaction | Implement `IRedactionPolicy`, replace default DI registration |
| Custom header adapter | Implement `IMessageHeaders` for new transport |
| Custom payload type | Implement `IPayload` for new content types |
