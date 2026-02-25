# ReqRepTransformation

> Enterprise-grade, framework-agnostic Request/Response Transformation Pipeline  
> .NET 9 · C# 13 · Zero-double-serialization · OpenTelemetry · Circuit Breaker · JWT Forwarding

---

## Projects

| Project | Purpose |
|---|---|
| `ReqRepTransformation.Core` | Abstractions, pipeline engine, circuit breaker, redaction, OTEL |
| `ReqRepTransformation.AspNetCore` | ASP.NET Core middleware adapter |
| `ReqRepTransformation.Transforms` | Built-in transforms: headers, address, JSON body, JWT |
| `SampleApiTestApp` | Sample ASP.NET Core API demonstrating the library |
| `*.Tests` | xUnit test suites |

---

## Quick Start

### 1. Register services in `Program.cs`

```csharp
builder.Services
    .AddReqRepTransformationAspNet(options =>
    {
        options.DefaultTimeout     = TimeSpan.FromSeconds(5);
        options.DefaultFailureMode = FailureMode.LogAndSkip;
    })
    .AddSingleton<ITransformationDetailProvider, YourDetailProvider>();
```

### 2. Add middleware

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseReqRepTransformation();  // After auth, before routing
app.MapControllers();
```

### 3. Implement `ITransformationDetailProvider`

```csharp
public sealed class MyProvider : ITransformationDetailProvider
{
    public ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context, CancellationToken ct = default)
    {
        return ValueTask.FromResult(new TransformationDetail
        {
            RequestTransformations = new ITransformation[]
            {
                new CorrelationIdTransform(),
                new JwtForwardTransform(),
                new JsonGatewayMetadataTransform()
            },
            ResponseTransformations = new ITransformation[]
            {
                new RemoveInternalResponseHeadersTransform(),
                new GatewayResponseTagTransform()
            },
            TransformationTimeout = TimeSpan.FromSeconds(3),
            FailureMode = FailureMode.LogAndSkip
        });
    }
}
```

---

## Writing a Custom Transform

```csharp
public sealed class MyTransform : IBufferTransform
{
    public string Name => "my-transform";

    public bool ShouldApply(IMessageContext context)
        => context.Payload.IsJson;

    public async ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var json = await context.Payload.GetJsonAsync(ct);
        if (json is JsonObject obj)
            obj["processedBy"] = "gateway";
        // No SetJsonAsync needed — mutate in-place
    }
}
```

Use `IStreamTransform` for transforms on streaming bodies (file uploads) that only touch headers.

---

## Hard Rules

| Rule | Detail |
|---|---|
| No double-serialization | `GetJsonAsync()` parses once, `FlushAsync()` serializes once at exit |
| No HttpContext in transforms | All transforms operate on `IMessageContext` only |
| No ValueTask awaited twice | Store as `Task` if multiple awaits needed |
| No body buffering in `IStreamTransform` | Never call `GetJsonAsync`/`GetBufferAsync` from a stream transform |
| No Authorization in logs | Enforced by `IRedactionPolicy` at the log infrastructure level |

---

## OpenTelemetry Setup

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(TelemetryConstants.ActivitySourceName)  // "ReqRepTransformation"
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());
```

---

## Circuit Breaker Configuration

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

---

## Running Tests

```bash
dotnet test
```

---

## Risk Register (Key Items)

| Risk | Mitigation |
|---|---|
| Large JSON bodies → GC pressure | `RecyclableMemoryStream` + `PipeReader` — no intermediate string alloc |
| Circuit breaker false-positive opens | Tune `WindowSize` + `FailureRatioThreshold` per environment |
| Transform order dependency bug | Sequential execution by default; integration tests assert order |
| Response body swap race condition | Swap done before `_next()` returns; original stream restored in `finally` |
| JWT leaked in logs | `DefaultRedactionPolicy` redacts `Authorization` header always |
| `PipeReader` not advanced → hang | `PayloadContext` always calls `reader.AdvanceTo(buffer.End)` |
| Parallel transforms + JSON mutation | `SemaphoreSlim(1,1)` on first JSON parse; document JSON-mutating transforms as sequential-only |
| Timeout CTS leaks | `using var timeoutCts = ...` in `PipelineExecutor` — disposed per-transform |
