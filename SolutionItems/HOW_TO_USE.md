# ReqRepTransformation — How To Use

> A framework-agnostic, zero-double-serialization HTTP Request/Response Transformation Pipeline for ASP.NET Core 9.

---

## Table of Contents

1. [Installation & Setup](#1-installation--setup)
2. [Core Concepts](#2-core-concepts)
3. [Quick Start — 5 Minutes](#3-quick-start--5-minutes)
4. [TransformEntry & Execution Order](#4-transformentry--execution-order)
5. [Built-in Transforms Reference](#5-built-in-transforms-reference)
6. [Writing a Custom Transform](#6-writing-a-custom-transform)
7. [ITransformationDetailProvider — Provider Examples](#7-itransformationdetailprovider--provider-examples)
8. [Failure Modes](#8-failure-modes)
9. [Circuit Breaker](#9-circuit-breaker)
10. [JSON Body Mutation — Zero-Double-Serialization](#10-json-body-mutation--zero-double-serialization)
11. [Streaming Bodies — IStreamTransform](#11-streaming-bodies--istreamtransform)
12. [OpenTelemetry Integration](#12-opentelemetry-integration)
13. [Redaction Policy](#13-redaction-policy)
14. [JWT Forwarding & Claims Extraction](#14-jwt-forwarding--claims-extraction)
15. [Configuration Reference (appsettings.json)](#15-configuration-reference-appsettingsjson)
16. [Hard Rules Cheatsheet](#16-hard-rules-cheatsheet)

---

## 1. Installation & Setup

### NuGet References (add to your `.csproj`)

```xml
<!-- Core library — always required -->
<PackageReference Include="ReqRepTransformation.Core"       Version="1.0.0" />

<!-- ASP.NET Core integration -->
<PackageReference Include="ReqRepTransformation.AspNetCore"  Version="1.0.0" />

<!-- Built-in transforms (optional but recommended) -->
<PackageReference Include="ReqRepTransformation.Transforms"  Version="1.0.0" />
```

### Register in `Program.cs`

```csharp
using ReqRepTransformation.AspNetCore.DI;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

var builder = WebApplication.CreateBuilder(args);

// 1. Register all pipeline services
builder.Services.AddReqRepTransformationAspNet(options =>
{
    options.DefaultTimeout     = TimeSpan.FromSeconds(5);
    options.DefaultFailureMode = FailureMode.LogAndSkip;
});

// 2. Register your provider (see Section 7)
builder.Services.AddSingleton<ITransformationDetailProvider, MyTransformationDetailProvider>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// 3. Add middleware — AFTER auth, BEFORE MapControllers
app.UseReqRepTransformation();

app.MapControllers();
app.Run();
```

---

## 2. Core Concepts

| Concept | Description |
|---|---|
| `IMessageContext` | Transport-agnostic message handle. Transforms ONLY touch this — never `HttpContext`. |
| `IPayload` | Lazy-loaded body accessor. JSON parsed once, serialized once. |
| `ITransformation` | Single transform unit. `ShouldApply()` + `ApplyAsync()`. |
| `TransformEntry` | Wraps a transform with its `Order` (execution sequence). |
| `TransformationDetail` | Route-level config: which transforms, in what order, with what timeout and failure mode. |
| `ITransformationDetailProvider` | Your code that maps each request to a `TransformationDetail`. |
| `PipelineExecutor` | Engine that sorts by `Order`, checks circuit breakers, runs each transform. |

### Pipeline flow

```
Request arrives
     │
     ▼
ITransformationDetailProvider.GetTransformationDetailAsync()
     │  returns TransformationDetail (cached)
     ▼
PipelineExecutor: sort RequestTransformations by Order ASC
     │
     ├── TransformEntry(Order=10) → ShouldApply? → CircuitBreaker? → ApplyAsync()
     ├── TransformEntry(Order=20) → ShouldApply? → CircuitBreaker? → ApplyAsync()
     └── TransformEntry(Order=30) → ...
     │
     ▼
Forward to downstream (your controllers / proxy)
     │
     ▼
PipelineExecutor: sort ResponseTransformations by Order ASC
     │
     └── TransformEntry(Order=10) → ApplyAsync()
     │
     ▼
Write final response body to client
```

---

## 3. Quick Start — 5 Minutes

### Step 1: Implement `ITransformationDetailProvider`

```csharp
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Transforms.Auth;
using ReqRepTransformation.Transforms.Headers;
using ReqRepTransformation.Transforms.Body;

public sealed class MyProvider : ITransformationDetailProvider
{
    private static readonly CorrelationIdTransform       _correlationId = new();
    private static readonly JwtForwardTransform          _jwtForward    = new();
    private static readonly JsonGatewayMetadataTransform _meta          = new();
    private static readonly RemoveInternalResponseHeadersTransform _strip = new();

    public ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context, CancellationToken ct = default)
    {
        var detail = new TransformationDetail
        {
            RequestTransformations = new[]
            {
                TransformEntry.At(10, _correlationId),   // first
                TransformEntry.At(20, _jwtForward),      // second
                TransformEntry.At(30, _meta)             // third
            },
            ResponseTransformations = new[]
            {
                TransformEntry.At(10, _strip)            // strip internal headers
            },
            TransformationTimeout  = TimeSpan.FromSeconds(3),
            FailureMode            = FailureMode.LogAndSkip,
            HasExplicitFailureMode = true
        };

        return ValueTask.FromResult(detail);
    }
}
```

### Step 2: Register

```csharp
builder.Services.AddSingleton<ITransformationDetailProvider, MyProvider>();
```

### Step 3: Verify

Call any endpoint — the pipeline will:
- Inject `X-Correlation-Id` if absent
- Forward the `Authorization` header
- Add `_gateway` metadata to any JSON request body
- Strip backend headers from the response

---

## 4. TransformEntry & Execution Order

`Order` is an `int` property on `TransformEntry`. The pipeline sorts **ascending** before execution.

```csharp
// Registered in any order — execution is always 10 → 20 → 30
RequestTransformations = new[]
{
    TransformEntry.At(30, new JwtForwardTransform()),
    TransformEntry.At(10, new CorrelationIdTransform()),
    TransformEntry.At(20, new RequestIdPropagationTransform())
}
```

**Convention:** Use multiples of 10 (10, 20, 30...) to leave room for inserting new transforms without renumbering.

```csharp
// Later, insert at Order=15 without changing existing entries:
TransformEntry.At(15, new SomeNewTransform())
```

**Tied orders:** When two entries share the same `Order`, insertion order is preserved.

**Parallel mode:** Set `AllowParallelNonDependentTransforms = true` only for header/address transforms. JSON-mutating transforms must be sequential.

---

## 5. Built-in Transforms Reference

### Header Transforms

```csharp
// Add or overwrite a header
new AddHeaderTransform("X-Custom-Header", "value", overwrite: true)

// Add only if absent
new AddHeaderTransform("X-Custom-Header", "value", overwrite: false)

// Remove a header
new RemoveHeaderTransform("X-Powered-By")

// Rename a header
new RenameHeaderTransform("X-Old-Name", "X-New-Name")

// Append to multi-value header
new AppendHeaderTransform("Accept-Language", "en-US")

// Inject X-Correlation-Id (skips if already present)
new CorrelationIdTransform()
new CorrelationIdTransform(headerName: "X-Request-Correlation-Id")  // custom header name

// Inject X-Request-Id
new RequestIdPropagationTransform()

// Strip common backend-internal response headers (Server, X-Powered-By, etc.)
new RemoveInternalResponseHeadersTransform()
new RemoveInternalResponseHeadersTransform(new[] { "X-Internal-Token", "X-Debug" })

// Tag response with gateway version
new GatewayResponseTagTransform(version: "2.1", instanceId: "gateway-eu-west-1")
```

### Address / URI Transforms

```csharp
// Rewrite path prefix
new PathPrefixRewriteTransform("/api/v1", "/internal/v1")
// /api/v1/users/123 → /internal/v1/users/123

// Rewrite path using regex
new PathRegexRewriteTransform(pattern: "/api/v(\\d+)/(.*)", replacement: "/v$1/$2")
// /api/v2/orders → /v2/orders

// Add query parameter
new AddQueryParamTransform("api-version", "2024-01")
// /products → /products?api-version=2024-01

// Remove query parameter
new RemoveQueryParamTransform("debug")

// Rewrite host
new HostRewriteTransform("internal-catalog.svc", port: 8080, scheme: "http")

// Override HTTP method
new MethodOverrideTransform("PATCH")
new MethodOverrideTransform("PATCH", onlyIfCurrentMethod: "PUT")
```

### JSON Body Transforms

```csharp
// Add a field to the root JSON object
new JsonFieldAddTransform("status", JsonValue.Create("active")!)
new JsonFieldAddTransform("tier", JsonValue.Create("premium")!, overwrite: false)

// Remove a field
new JsonFieldRemoveTransform("internal_id")
new JsonFieldRemoveTransform("_metadata")

// Rename a field
new JsonFieldRenameTransform("userId", "user_id")
new JsonFieldRenameTransform("createdAt", "created_at")

// Set a nested field (dot-separated path, creates intermediate objects)
new JsonNestedFieldSetTransform("user.profile.tier", JsonValue.Create("gold")!)

// Inject _gateway metadata object
new JsonGatewayMetadataTransform()
new JsonGatewayMetadataTransform(version: "2.0")
// Adds: { "_gateway": { "version": "1.0", "processedAt": "...", "requestId": "..." } }
```

### Auth / JWT Transforms

```csharp
// Forward Authorization header as-is (passthrough)
new JwtForwardTransform()

// Extract JWT claims → inject as headers
new JwtClaimsExtractTransform(new Dictionary<string, string>
{
    ["sub"]   = "X-User-Id",
    ["email"] = "X-User-Email",
    ["roles"] = "X-User-Roles"
})

// Strip Authorization header before forwarding to downstream
new StripAuthorizationTransform()
```

---

## 6. Writing a Custom Transform

### Buffer Transform (JSON/XML/Headers/Address)

```csharp
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;
using System.Text.Json.Nodes;

/// <summary>
/// Injects the tenant ID from a header into the JSON request body.
/// </summary>
public sealed class TenantIdInjectTransform : IBufferTransform
{
    private const string TenantHeader = "X-Tenant-Id";

    public string Name => "tenant-id-inject";  // kebab-case, unique, stable

    public bool ShouldApply(IMessageContext context)
        // Only apply when the body is JSON AND tenant header is present
        => context.Payload.IsJson
        && context.Headers.Contains(TenantHeader);

    public async ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        // GetJsonAsync() parses once and caches — subsequent calls are free
        var node = await context.Payload.GetJsonAsync(ct);

        if (node is not JsonObject obj) return;

        var tenantId = context.Headers.Get(TenantHeader);
        obj["tenantId"] = JsonValue.Create(tenantId);  // mutate in-place — no SetJsonAsync needed
    }
}
```

### Stream Transform (streaming bodies — headers/address only)

```csharp
/// <summary>
/// Tags file upload requests with metadata headers.
/// MUST NOT touch the body — it's a streaming binary upload.
/// </summary>
public sealed class UploadTaggingTransform : IStreamTransform
{
    public string Name => "upload-tagging";

    public bool ShouldApply(IMessageContext context)
        => context.Payload.IsStreaming
        && context.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Set("X-Upload-Gateway", "reqrep/1.0");
        context.Headers.Set("X-Upload-Timestamp", DateTimeOffset.UtcNow.ToString("O"));
        // ↑ Only touch headers — NEVER call GetJsonAsync/GetBufferAsync here
        return ValueTask.CompletedTask;
    }
}
```

### Register your custom transform

```csharp
// Stateless → singleton
builder.Services.AddSingleton<TenantIdInjectTransform>();

// Then use in your provider:
TransformEntry.At(40, serviceProvider.GetRequiredService<TenantIdInjectTransform>())
```

---

## 7. ITransformationDetailProvider — Provider Examples

### Example A: Route-based in-memory provider

```csharp
public sealed class RouteBasedProvider : ITransformationDetailProvider
{
    private static readonly CorrelationIdTransform _corrId  = new();
    private static readonly JwtForwardTransform    _jwt     = new();

    public ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context, CancellationToken ct = default)
    {
        var path   = context.Address.AbsolutePath;
        var method = context.Method;

        // POST /api/orders — enrich JSON, forward JWT
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith("/api/orders", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(new TransformationDetail
            {
                RequestTransformations = new[]
                {
                    TransformEntry.At(10, _corrId),
                    TransformEntry.At(20, _jwt),
                    TransformEntry.At(30, new JsonGatewayMetadataTransform())
                },
                TransformationTimeout  = TimeSpan.FromSeconds(3),
                FailureMode            = FailureMode.LogAndSkip,
                HasExplicitFailureMode = true
            });
        }

        // Default: correlation ID only
        return ValueTask.FromResult(new TransformationDetail
        {
            RequestTransformations  = new[] { TransformEntry.At(10, _corrId) },
            HasExplicitFailureMode  = false  // → uses PipelineOptions.DefaultFailureMode
        });
    }
}
```

### Example B: Cached database-backed provider

```csharp
public sealed class DatabaseProvider : ITransformationDetailProvider
{
    private readonly IMemoryCache     _cache;
    private readonly IRouteRepository _repository;
    private readonly TransformFactory _factory;

    public DatabaseProvider(IMemoryCache cache, IRouteRepository repository, TransformFactory factory)
    {
        _cache      = cache;
        _repository = repository;
        _factory    = factory;
    }

    public async ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context, CancellationToken ct = default)
    {
        var cacheKey = $"reqrep:{context.Method}:{NormalizePath(context.Address.AbsolutePath)}";

        if (_cache.TryGetValue(cacheKey, out TransformationDetail? cached) && cached is not null)
            return cached;

        var rule = await _repository.FindMatchingRuleAsync(context.Method, context.Address.AbsolutePath, ct);

        if (rule is null)
            return TransformationDetail.Empty;

        var detail = new TransformationDetail
        {
            RequestTransformations = rule.RequestEntries
                .Select(e => TransformEntry.At(e.Order, _factory.Create(e)))
                .Where(e => e.Transform is not null)
                .ToArray(),
            ResponseTransformations = rule.ResponseEntries
                .Select(e => TransformEntry.At(e.Order, _factory.Create(e)))
                .Where(e => e.Transform is not null)
                .ToArray(),
            TransformationTimeout  = rule.TimeoutMs > 0
                ? TimeSpan.FromMilliseconds(rule.TimeoutMs)
                : TimeSpan.Zero,
            FailureMode            = (FailureMode)rule.FailureMode,
            HasExplicitFailureMode = rule.HasExplicitFailureMode
        };

        _cache.Set(cacheKey, detail, TimeSpan.FromMinutes(5));
        return detail;
    }

    private static string NormalizePath(string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length; i++)
            if (long.TryParse(segs[i], out _) || Guid.TryParse(segs[i], out _))
                segs[i] = "{id}";
        return "/" + string.Join('/', segs);
    }
}
```

### Example C: Feature-flag driven provider

```csharp
public sealed class FeatureFlagProvider : ITransformationDetailProvider
{
    private readonly IFeatureManager _features;

    public FeatureFlagProvider(IFeatureManager features) => _features = features;

    public async ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context, CancellationToken ct = default)
    {
        var entries = new List<TransformEntry>
        {
            TransformEntry.At(10, new CorrelationIdTransform())
        };

        if (await _features.IsEnabledAsync("GatewayMetadataInjection"))
            entries.Add(TransformEntry.At(50, new JsonGatewayMetadataTransform()));

        if (await _features.IsEnabledAsync("JwtForwarding"))
            entries.Add(TransformEntry.At(20, new JwtForwardTransform()));

        return new TransformationDetail
        {
            RequestTransformations = entries.OrderBy(e => e.Order).ToArray(),
            FailureMode            = FailureMode.LogAndSkip,
            HasExplicitFailureMode = true
        };
    }
}
```

---

## 8. Failure Modes

| Mode | Behaviour | Use When |
|---|---|---|
| `LogAndSkip` | Logs a warning, skips failing transform, continues | **Default — production-safe** |
| `Continue` | Logs an error, continues without skipping | You need all transforms attempted always |
| `StopPipeline` | Throws `TransformationException`, returns 502 | High-integrity flows (payments, auth gates) |

```csharp
// Per-route explicit failure mode
new TransformationDetail
{
    FailureMode            = FailureMode.StopPipeline,
    HasExplicitFailureMode = true   // ← REQUIRED when overriding
}

// Use global default (from PipelineOptions.DefaultFailureMode)
new TransformationDetail
{
    HasExplicitFailureMode = false  // ← provider doesn't specify, global wins
}
```

**Global default in `appsettings.json`:**

```json
{
  "ReqRepTransformation": {
    "DefaultFailureMode": "LogAndSkip"
  }
}
```

---

## 9. Circuit Breaker

The pipeline has a built-in **lock-free sliding-window circuit breaker** per transform. No Polly required.

```json
{
  "ReqRepTransformation": {
    "CircuitBreaker": {
      "WindowSize": 20,
      "FailureRatioThreshold": 0.5,
      "OpenDuration": "00:00:30"
    }
  }
}
```

**States:**
- `Closed` → normal operation
- `Open` → transform skipped for `OpenDuration` (30s default)
- `HalfOpen` → one trial execution allowed; success → Closed, failure → Open again

**Manual reset** (operational recovery):

```csharp
app.MapPost("/internal/circuit/reset/{transformName}", (
    string transformName,
    ITransformCircuitBreaker breaker) =>
{
    breaker.Reset(transformName);
    return Results.Ok(new { reset = transformName });
});
```

---

## 10. JSON Body Mutation — Zero-Double-Serialization

The library guarantees JSON is parsed **once** and serialized **once**, regardless of how many transforms run.

```csharp
// Transform A: adds "requestId"
public async ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
{
    var node = await context.Payload.GetJsonAsync(ct);  // ← parses body (1st call)
    if (node is JsonObject obj)
        obj["requestId"] = Guid.NewGuid().ToString("N");
}

// Transform B: adds "processedBy" — GetJsonAsync returns SAME cached node
public async ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
{
    var node = await context.Payload.GetJsonAsync(ct);  // ← cache hit, no re-parse
    if (node is JsonObject obj)
        obj["processedBy"] = "gateway-eu";
}

// At pipeline exit: FlushAsync() serializes the mutated node exactly once → wire
```

**Rules:**
- Mutate `JsonNode` in-place — do NOT call `SetJsonAsync` for field-level mutations.
- Call `SetJsonAsync` only when replacing the entire root node.
- Never store the `JsonNode` reference outside `ApplyAsync` — it is owned by `IPayload`.

---

## 11. Streaming Bodies — IStreamTransform

Use `IStreamTransform` when the body must not be buffered (file uploads, large downloads, gRPC).

```csharp
public sealed class UploadMetadataTransform : IStreamTransform
{
    public string Name => "upload-metadata";

    public bool ShouldApply(IMessageContext context)
        => context.Payload.IsStreaming;  // multipart/form-data, application/octet-stream, etc.

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        // ✅ Allowed: touch headers and address
        context.Headers.Set("X-Upload-Received", DateTimeOffset.UtcNow.ToString("O"));
        context.Headers.Set("X-Upload-Size", context.Headers.Get("Content-Length") ?? "unknown");

        // ❌ NEVER call: context.Payload.GetJsonAsync()
        // ❌ NEVER call: context.Payload.GetBufferAsync()
        // ✅ ONLY if needed: context.Payload.GetPipeReaderAsync() — for passthrough pipe work

        return ValueTask.CompletedTask;
    }
}
```

The pipeline automatically skips `IBufferTransform` implementations when `IPayload.IsStreaming == true`.

---

## 12. OpenTelemetry Integration

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("MyGateway", serviceVersion: "1.0"))
    .WithTracing(tracing => tracing
        .AddSource("ReqRepTransformation")     // ← register the ActivitySource
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://otel-collector:4317")));
```

**What you get per request:**
```
reqrep.pipeline.request                    [parent span]
  ├── reqrep.transform.correlation-id-inject  [transform.order=10, transform.result=ok]
  ├── reqrep.transform.jwt-forward            [transform.order=20, transform.result=ok]
  └── reqrep.transform.json-gateway-metadata  [transform.order=30, transform.result=ok]
reqrep.pipeline.response
  └── reqrep.transform.remove-internal-response-headers  [transform.result=ok]
```

**Span tags on each transform span:**
- `transform.name` — e.g. `"jwt-forward"`
- `transform.side` — `"request"` or `"response"`
- `transform.result` — `"ok"`, `"skipped"`, `"failed"`, `"circuit_open"`
- `transform.order` — e.g. `20`
- `payload.content_type` — e.g. `"application/json"`

---

## 13. Redaction Policy

All header values and query params are checked against `IRedactionPolicy` before logging or tracing.

**Default redacted keys:** `Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, `X-Client-Secret`.

**Custom policy:**

```csharp
public sealed class MyRedactionPolicy : IRedactionPolicy
{
    private static readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "X-Api-Key", "X-Ssn", "X-Card-Number"
    };

    public bool ShouldRedact(string key) => _keys.Contains(key);

    public string Redact(string key, string value)
        // Show last 4 chars for partial traceability
        => value.Length > 4 ? $"***{value[^4..]}" : "***";
}

// Register (replaces default)
builder.Services.AddSingleton<IRedactionPolicy, MyRedactionPolicy>();
```

---

## 14. JWT Forwarding & Claims Extraction

### Forward the token as-is

```csharp
TransformEntry.At(20, new JwtForwardTransform())
// The Authorization: Bearer <token> header is forwarded to downstream unchanged.
// The token value NEVER appears in logs (IRedactionPolicy enforced).
```

### Extract claims into headers

```csharp
TransformEntry.At(30, new JwtClaimsExtractTransform(new Dictionary<string, string>
{
    ["sub"]             = "X-User-Id",
    ["email"]           = "X-User-Email",
    ["custom:tenantId"] = "X-Tenant-Id",
    ["roles"]           = "X-User-Roles"
}))
// Downstream controller can read: Request.Headers["X-User-Id"]
```

### Strip token from admin/internal routes

```csharp
TransformEntry.At(20, new StripAuthorizationTransform())
// Removes Authorization header — downstream never sees the client JWT.
// Add your internal service key in the next step:
TransformEntry.At(30, new AddHeaderTransform("X-Internal-Key", "service-key-from-secrets"))
```

---

## 15. Configuration Reference (appsettings.json)

```json
{
  "ReqRepTransformation": {
    "DefaultTimeout": "00:00:05",
    "DefaultFailureMode": "LogAndSkip",

    "CircuitBreaker": {
      "WindowSize": 20,
      "FailureRatioThreshold": 0.5,
      "OpenDuration": "00:00:30"
    },

    "RedactedHeaderKeys": [
      "Authorization",
      "Cookie",
      "Set-Cookie",
      "X-Api-Key",
      "X-Client-Secret",
      "X-Api-Secret",
      "X-Internal-Token"
    ],

    "RedactedQueryKeys": [
      "access_token",
      "api_key",
      "token",
      "secret"
    ]
  }
}
```

| Key | Type | Default | Description |
|---|---|---|---|
| `DefaultTimeout` | `TimeSpan` | `00:00:05` | Fallback timeout when `TransformationDetail.TransformationTimeout == Zero` |
| `DefaultFailureMode` | `string` | `LogAndSkip` | Fallback when `TransformationDetail.HasExplicitFailureMode == false` |
| `CircuitBreaker.WindowSize` | `int` | `20` | Sliding window size |
| `CircuitBreaker.FailureRatioThreshold` | `double` | `0.5` | Open circuit when failure % exceeds this |
| `CircuitBreaker.OpenDuration` | `TimeSpan` | `00:00:30` | How long circuit stays open |

---

## 16. Hard Rules Cheatsheet

### ✅ Always do

```csharp
// Always set HasExplicitFailureMode when providing a FailureMode
new TransformationDetail
{
    FailureMode            = FailureMode.StopPipeline,
    HasExplicitFailureMode = true   // ← required
}

// Always use Order multiples of 10
TransformEntry.At(10, transformA)
TransformEntry.At(20, transformB)

// Always return ValueTask from ApplyAsync
public ValueTask ApplyAsync(IMessageContext ctx, CancellationToken ct)
    => ValueTask.CompletedTask;

// Always mutate JsonNode in-place
var node = await context.Payload.GetJsonAsync(ct);
if (node is JsonObject obj) obj["key"] = "value";  // ← in-place mutation
```

### ❌ Never do

```csharp
// Never call GetJsonAsync from IStreamTransform
public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
{
    var node = await context.Payload.GetJsonAsync(ct);  // ← VIOLATION
}

// Never reference HttpContext inside a transform
public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
{
    var httpCtx = (context as AspNetRequestMessageContext).HttpContext;  // ← VIOLATION
}

// Never store JsonNode outside ApplyAsync
private JsonNode? _cachedNode;  // ← VIOLATION — node owned by IPayload

// Never call FlushAsync from a transform
await context.Payload.FlushAsync();  // ← VIOLATION — pipeline exit only

// Never await a ValueTask twice
var task = transform.ApplyAsync(ctx, ct);
await task;
await task;  // ← VIOLATION — ValueTask must not be awaited more than once
```

---

## Appendix: Complete Example — Multi-Route Provider

```csharp
public sealed class FullGatewayProvider : ITransformationDetailProvider
{
    // Shared stateless instances
    private static readonly CorrelationIdTransform  _corrId      = new();
    private static readonly JwtForwardTransform     _jwtFwd      = new();
    private static readonly StripAuthorizationTransform _strip   = new();
    private static readonly JsonGatewayMetadataTransform _meta   = new();
    private static readonly GatewayResponseTagTransform  _tag    = new("2.0");
    private static readonly RemoveInternalResponseHeadersTransform _clean = new();
    private static readonly AddHeaderTransform _internalKey
        = new("X-Internal-Key", "get-from-secrets-manager");

    private static readonly JwtClaimsExtractTransform _claims = new(
        new Dictionary<string, string> { ["sub"] = "X-User-Id", ["email"] = "X-User-Email" });

    private static readonly PathPrefixRewriteTransform _catalogRewrite
        = new("/api/products", "/catalog/v2");

    private readonly IMemoryCache _cache;

    public FullGatewayProvider(IMemoryCache cache) => _cache = cache;

    public ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context, CancellationToken ct = default)
    {
        var key = $"{context.Method}:{NormalizePath(context.Address.AbsolutePath)}";

        if (_cache.TryGetValue(key, out TransformationDetail? hit) && hit is not null)
            return ValueTask.FromResult(hit);

        var detail = Resolve(context.Method, context.Address.AbsolutePath);
        _cache.Set(key, detail, TimeSpan.FromMinutes(5));
        return ValueTask.FromResult(detail);
    }

    private TransformationDetail Resolve(string method, string path)
    {
        // ── POST /api/orders ──────────────────────────────────────
        if (method == "POST" && path.StartsWith("/api/orders"))
            return new TransformationDetail
            {
                RequestTransformations = new[]
                {
                    TransformEntry.At(10, _corrId),
                    TransformEntry.At(20, _jwtFwd),
                    TransformEntry.At(30, _claims),
                    TransformEntry.At(40, _meta)
                },
                ResponseTransformations = new[]
                {
                    TransformEntry.At(10, _clean),
                    TransformEntry.At(20, _tag)
                },
                TransformationTimeout  = TimeSpan.FromSeconds(3),
                FailureMode            = FailureMode.LogAndSkip,
                HasExplicitFailureMode = true
            };

        // ── GET /api/products ─────────────────────────────────────
        if (method == "GET" && path.StartsWith("/api/products"))
            return new TransformationDetail
            {
                RequestTransformations = new[]
                {
                    TransformEntry.At(10, _corrId),
                    TransformEntry.At(20, _jwtFwd),
                    TransformEntry.At(30, _catalogRewrite)
                },
                ResponseTransformations = new[] { TransformEntry.At(10, _clean) },
                TransformationTimeout  = TimeSpan.FromSeconds(3),
                FailureMode            = FailureMode.LogAndSkip,
                HasExplicitFailureMode = true
            };

        // ── ANY /api/admin ────────────────────────────────────────
        if (path.StartsWith("/api/admin"))
            return new TransformationDetail
            {
                RequestTransformations = new[]
                {
                    TransformEntry.At(10, _corrId),
                    TransformEntry.At(20, _strip),
                    TransformEntry.At(30, _internalKey)
                },
                ResponseTransformations = new[] { TransformEntry.At(10, _clean) },
                FailureMode            = FailureMode.StopPipeline,
                HasExplicitFailureMode = true
            };

        // ── Default ───────────────────────────────────────────────
        return new TransformationDetail
        {
            RequestTransformations  = new[] { TransformEntry.At(10, _corrId) },
            HasExplicitFailureMode  = false   // use global default
        };
    }

    private static string NormalizePath(string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length; i++)
            if (long.TryParse(segs[i], out _) || Guid.TryParse(segs[i], out _))
                segs[i] = "{id}";
        return "/" + string.Join('/', segs);
    }
}
```
