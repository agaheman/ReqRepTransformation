using ReqRepTransformation.Core.Abstractions;

namespace ReqRepTransformation.Core.Models;

/// <summary>
/// Wraps a single ITransformer with its execution order within a pipeline side.
///
/// Design rationale:
/// Order does NOT live on ITransformer itself, because:
///   1. The same transformer instance (e.g. CorrelationIdTransformer) may be registered
///      on multiple routes with different orders — it must remain stateless and reusable.
///   2. Order is a pipeline-configuration concern, not a transform behavior concern.
///      Keeping them separate respects SRP.
///
/// PipelineExecutor sorts TransformEntry collections by Order ASC before execution.
/// Convention: use multiples of 10 (10, 20, 30 ...) to leave room for insertion.
/// </summary>
public sealed record TransformEntry
{
    /// <summary>
    /// Ascending execution order within the pipeline side.
    /// Lower numbers execute first. Ties are broken by list insertion order.
    /// </summary>
    public int Order { get; init; }

    /// <summary>The transformer to execute at this position.</summary>
    public ITransformer Transform { get; init; }

    public TransformEntry(int order, ITransformer transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        Order     = order;
        Transform = transform;
    }

    /// <summary>Convenience factory.</summary>
    public static TransformEntry At(int order, ITransformer transform)
        => new(order, transform);
}

/// <summary>
/// Resolved per-request transformation configuration.
/// Returned by ITransformationDetailProvider and consumed by PipelineExecutor.
///
/// Fallback rules applied by PipelineExecutor:
///   - TransformationTimeout == TimeSpan.Zero → PipelineOptions.DefaultTimeout
///   - HasExplicitFailureMode == false        → PipelineOptions.DefaultFailureMode
/// </summary>
public sealed record TransformationDetail
{
    /// <summary>Request-side transform entries. Sorted by Order ASC before execution.</summary>
    public IReadOnlyList<TransformEntry> RequestTransformations { get; init; }
        = Array.Empty<TransformEntry>();

    /// <summary>Response-side transform entries. Sorted by Order ASC before execution.</summary>
    public IReadOnlyList<TransformEntry> ResponseTransformations { get; init; }
        = Array.Empty<TransformEntry>();

    /// <summary>
    /// Per-transform timeout for this route.
    /// Zero → falls back to PipelineOptions.DefaultTimeout.
    /// </summary>
    public TimeSpan TransformationTimeout { get; init; } = TimeSpan.Zero;

    /// <summary>Failure handling mode for this route (see HasExplicitFailureMode).</summary>
    public FailureMode FailureMode { get; init; } = FailureMode.LogAndSkip;

    /// <summary>
    /// Must be true for FailureMode to take effect.
    /// Guards against the StopPipeline enum default (= 0) being silently applied
    /// to routes that never set a failure mode.
    /// </summary>
    public bool HasExplicitFailureMode { get; init; } = false;

    /// <summary>
    /// When true, header/address transforms on the same side run concurrently via Task.WhenAll.
    /// WARNING: never enable for routes containing JSON-mutating transforms — JsonNode is not
    /// thread-safe for concurrent writes.
    /// </summary>
    public bool AllowParallelNonDependentTransforms { get; init; } = false;

    /// <summary>Pass-through: no transforms applied, global defaults used.</summary>
    public static TransformationDetail Empty { get; } = new();
}

/// <summary>
/// Governs what happens when a transform throws or times out.
/// </summary>
public enum FailureMode
{
    /// <summary>Abort the entire pipeline immediately (throws TransformationException).</summary>
    StopPipeline = 0,

    /// <summary>Log the error and continue to the next transform.</summary>
    Continue = 1,

    /// <summary>Log a warning, skip only the failing transform, continue. Recommended default.</summary>
    LogAndSkip = 2
}

/// <summary>Which side of the HTTP exchange the context represents.</summary>
public enum MessageSide
{
    Request  = 0,
    Response = 1
}

/// <summary>
/// Global pipeline configuration. Bind from appsettings.json under "ReqRepTransformation".
/// Values act as fallbacks when TransformationDetail does not specify them.
/// </summary>
public sealed class PipelineOptions
{
    public const string SectionName = "ReqRepTransformation";

    /// <summary>
    /// Global per-transform timeout fallback.
    /// Applied when TransformationDetail.TransformationTimeout is zero.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Global failure mode fallback.
    /// Applied when TransformationDetail.HasExplicitFailureMode is false.
    /// Default: LogAndSkip.
    /// </summary>
    public FailureMode DefaultFailureMode { get; set; } = FailureMode.LogAndSkip;

    // ── Note on resilience ────────────────────────────────────────────────────
    // Circuit breaking, retries, and bulkhead isolation are NOT configured here.
    // Apply them at the outbound HttpClient level via Polly or
    // Microsoft.Extensions.Http.Resilience. This keeps transform concerns separate
    // from transport-level resilience concerns (SRP).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Header keys redacted in all logs and OTEL traces.</summary>
    public IList<string> RedactedHeaderKeys { get; set; } = new List<string>
    {
        "Authorization", "Cookie", "Set-Cookie",
        "X-Api-Key", "X-Client-Secret", "X-Api-Secret", "X-Internal-Token"
    };

    /// <summary>Query string keys redacted in logs and traces.</summary>
    public IList<string> RedactedQueryKeys { get; set; } = new List<string>
    {
        "access_token", "api_key", "token", "secret"
    };
}
