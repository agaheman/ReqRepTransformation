using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ReqRepTransformation.Core.Infrastructure.Telemetry;

/// <summary>
/// Central registry for all OpenTelemetry ActivitySource, Meter, and attribute name constants.
/// Import this class instead of using magic strings in pipeline code.
/// </summary>
public static class TelemetryConstants
{
    /// <summary>ActivitySource name. Register via AddSource("ReqRepTransformation") in OTEL setup.</summary>
    public const string ActivitySourceName = "ReqRepTransformation";

    /// <summary>Meter name for pipeline-level counters.</summary>
    public const string MeterName = "ReqRepTransformation";

    // ──────────────────────────────────────────────
    // Span / Activity attribute keys
    // ──────────────────────────────────────────────

    public const string AttrTransformName    = "transform.name";
    public const string AttrTransformSide    = "transform.side";
    public const string AttrTransformResult  = "transform.result";
    public const string AttrCircuitState     = "circuit.state";
    public const string AttrContentType      = "payload.content_type";
    public const string AttrRequestMethod    = "http.request.method";
    public const string AttrHttpRoute        = "http.route";
    public const string AttrErrorType        = "error.type";
    public const string AttrPipelineSide     = "pipeline.side";

    // ──────────────────────────────────────────────
    // Span / Activity attribute values
    // ──────────────────────────────────────────────

    public const string ResultOk            = "ok";
    public const string ResultSkipped       = "skipped";
    public const string ResultFailed        = "failed";
    public const string ResultCircuitOpen   = "circuit_open";
    public const string ResultShouldApplyFalse = "should_apply_false";

    public const string SideRequest         = "request";
    public const string SideResponse        = "response";

    // ──────────────────────────────────────────────
    // Shared ActivitySource and Meter instances
    // ──────────────────────────────────────────────

    /// <summary>Shared ActivitySource. Created once per process.</summary>
    public static readonly ActivitySource ActivitySource
        = new(ActivitySourceName, "1.0.0");

    /// <summary>Shared Meter. Created once per process.</summary>
    public static readonly Meter Meter
        = new(MeterName, "1.0.0");

    // Counters
    public static readonly Counter<long> TransformExecutedCounter
        = Meter.CreateCounter<long>(
            "reqrep.transform.executed",
            description: "Number of transform executions");

    public static readonly Counter<long> TransformSkippedCounter
        = Meter.CreateCounter<long>(
            "reqrep.transform.skipped",
            description: "Number of transforms skipped (ShouldApply=false or circuit open)");

    public static readonly Counter<long> TransformFailedCounter
        = Meter.CreateCounter<long>(
            "reqrep.transform.failed",
            description: "Number of transform failures");

    public static readonly Counter<long> CircuitOpenCounter
        = Meter.CreateCounter<long>(
            "reqrep.circuit.opened",
            description: "Number of times a circuit breaker opened");
}
