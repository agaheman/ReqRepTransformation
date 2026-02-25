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
/// Recommended convention: use multiples of 10 (10, 20, 30 ...) to leave room for
/// insertion without renumbering all entries.
/// </summary>
public sealed record TransformEntry
{
    /// <summary>
    /// Ascending execution order within the pipeline side (request or response).
    /// Lower numbers execute first. Ties are broken by list insertion order.
    /// Convention: use multiples of 10 (10, 20, 30...) for easy reordering.
    /// </summary>
    public int Order { get; init; }

    /// <summary>The transform to execute at this position.</summary>
    public ITransformer Transform { get; init; }

    public TransformEntry(int order, ITransformer transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        Order     = order;
        Transform = transform;
    }

    /// <summary>Convenience factory: new TransformEntry(order, transform).</summary>
    public static TransformEntry At(int order, ITransformer transform)
        => new(order, transform);
}

/// <summary>
/// Resolved per-request transformation configuration.
/// Returned by ITransformationDetailProvider and consumed by PipelineExecutor.
///
/// PipelineExecutor applies these fallback rules using PipelineOptions:
///   - TransformationTimeout == TimeSpan.Zero → PipelineOptions.DefaultTimeout
///   - HasExplicitFailureMode == false        → PipelineOptions.DefaultFailureMode
///     (guards against DB rows with a NULL failure_mode defaulting to StopPipeline = 0)
///
/// Transforms are executed in ascending Order within each side.
/// </summary>
public sealed record TransformationDetail
{
    /// <summary>
    /// Ordered transform entries for the request side (before forwarding downstream).
    /// PipelineExecutor sorts by TransformEntry.Order ASC before execution.
    /// </summary>
    public IReadOnlyList<TransformEntry> RequestTransformations { get; init; }
        = Array.Empty<TransformEntry>();

    /// <summary>
    /// Ordered transform entries for the response side (after receiving downstream response).
    /// PipelineExecutor sorts by TransformEntry.Order ASC before execution.
    /// </summary>
    public IReadOnlyList<TransformEntry> ResponseTransformations { get; init; }
        = Array.Empty<TransformEntry>();

    /// <summary>
    /// Per-transform execution timeout for all transforms on this route.
    /// Zero (default) → PipelineExecutor falls back to PipelineOptions.DefaultTimeout.
    /// </summary>
    public TimeSpan TransformationTimeout { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Failure handling mode for this route.
    /// Only respected when HasExplicitFailureMode is true.
    /// Otherwise PipelineExecutor uses PipelineOptions.DefaultFailureMode.
    /// </summary>
    public FailureMode FailureMode { get; init; } = FailureMode.LogAndSkip;

    /// <summary>
    /// Set to true when FailureMode is intentionally configured (not just the enum default).
    /// Prevents a DB row with a NULL failure_mode column from silently becoming StopPipeline.
    /// In-code providers: always set this to true when specifying a FailureMode.
    /// </summary>
    public bool HasExplicitFailureMode { get; init; } = false;

    /// <summary>
    /// When true, independent (non-JSON-mutating) transforms on the same side
    /// are executed concurrently via Task.WhenAll.
    /// WARNING: Do NOT enable for routes that contain JSON-mutating transforms —
    /// concurrent JsonNode mutation is not thread-safe.
    /// Defaults to false (sequential, safe for all transform types).
    /// </summary>
    public bool AllowParallelNonDependentTransforms { get; init; } = false;

    /// <summary>Pass-through detail: no transforms applied, global defaults used.</summary>
    public static TransformationDetail Empty { get; } = new();
}

/// <summary>
/// Governs what happens when an individual transform throws or its circuit breaker is open.
/// </summary>
public enum FailureMode
{
    /// <summary>
    /// Abort the entire pipeline immediately by throwing TransformationException.
    /// Appropriate for payment flows or any route where partial transformation is dangerous.
    /// </summary>
    StopPipeline = 0,

    /// <summary>Log the error and continue to the next transform in the pipeline.</summary>
    Continue = 1,

    /// <summary>
    /// Log a warning, skip only the failing transform, and continue.
    /// Recommended production default — safe and observable.
    /// </summary>
    LogAndSkip = 2
}

/// <summary>Indicates which side of the HTTP exchange a message context represents.</summary>
public enum MessageSide
{
    Request  = 0,
    Response = 1
}

/// <summary>Configuration options for the sliding-window circuit breaker.</summary>
public sealed record CircuitBreakerOptions
{
    /// <summary>Number of recent executions tracked in the sliding window. Default: 20.</summary>
    public int WindowSize { get; init; } = 20;

    /// <summary>
    /// Failure fraction that opens the circuit (0.0–1.0). Default: 0.5 (50%).
    /// </summary>
    public double FailureRatioThreshold { get; init; } = 0.50;

    /// <summary>How long the circuit stays open before transitioning to HalfOpen. Default: 30s.</summary>
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Global pipeline configuration. Bind from appsettings.json under "ReqRepTransformation".
/// Values are used as fallbacks when TransformationDetail does not specify them explicitly.
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
    /// Default: LogAndSkip (production-safe — never crashes the pipeline on transform failure).
    /// </summary>
    public FailureMode DefaultFailureMode { get; set; } = FailureMode.LogAndSkip;

    /// <summary>Circuit breaker configuration applied globally to all transforms.</summary>
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();

    /// <summary>Header keys whose values are redacted in all logs and OTEL traces.</summary>
    public IList<string> RedactedHeaderKeys { get; set; } = new List<string>
    {
        "Authorization", "Cookie", "Set-Cookie",
        "X-Api-Key", "X-Client-Secret", "X-Api-Secret", "X-Internal-Token"
    };

    /// <summary>Query string parameter names whose values are redacted in logs and traces.</summary>
    public IList<string> RedactedQueryKeys { get; set; } = new List<string>
    {
        "access_token", "api_key", "token", "secret"
    };
}
