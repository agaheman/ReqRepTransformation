using Microsoft.Extensions.Logging;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Infrastructure.Logging;

/// <summary>
/// Zero-allocation logging helpers using [LoggerMessage] source generators.
/// All log calls in the pipeline go through these methods — never raw ILogger.Log.
/// Values are passed through IRedactionPolicy before any write.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "Logging must never throw.")]
public static partial class PipelineLogMessages
{
    // ──────────────────────────────────────────────────────────────
    // Pipeline lifecycle
    // ──────────────────────────────────────────────────────────────

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "Pipeline starting | Side={Side} Method={Method} Path={Path}")]
    public static partial void PipelineStarting(
        this ILogger logger,
        string side,
        string method,
        string path);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "Pipeline completed | Side={Side} TransformCount={TransformCount} ElapsedMs={ElapsedMs}")]
    public static partial void PipelineCompleted(
        this ILogger logger,
        string side,
        int transformCount,
        long elapsedMs);

    // ──────────────────────────────────────────────────────────────
    // Transform execution
    // ──────────────────────────────────────────────────────────────

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Trace,
        Message = "Transform executing | Name={TransformName} Side={Side}")]
    public static partial void TransformExecuting(
        this ILogger logger,
        string transformName,
        string side);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Trace,
        Message = "Transform completed | Name={TransformName} Side={Side} ElapsedMs={ElapsedMs}")]
    public static partial void TransformCompleted(
        this ILogger logger,
        string transformName,
        string side,
        long elapsedMs);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Debug,
        Message = "Transform skipped (ShouldApply=false) | Name={TransformName} Side={Side}")]
    public static partial void TransformSkipped(
        this ILogger logger,
        string transformName,
        string side);

    // ──────────────────────────────────────────────────────────────
    // Failures
    // ──────────────────────────────────────────────────────────────

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Error,
        Message = "Transform failed | Name={TransformName} Side={Side} FailureMode={FailureMode}")]
    public static partial void TransformFailed(
        this ILogger logger,
        Exception ex,
        string transformName,
        string side,
        string failureMode);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "Transform timed out | Name={TransformName} Side={Side} TimeoutMs={TimeoutMs}")]
    public static partial void TransformTimedOut(
        this ILogger logger,
        string transformName,
        string side,
        double timeoutMs);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Error,
        Message = "Pipeline aborted (StopPipeline) | Side={Side} FailingTransform={TransformName}")]
    public static partial void PipelineAborted(
        this ILogger logger,
        string side,
        string transformName);

    // ──────────────────────────────────────────────────────────────
    // Circuit breaker
    // ──────────────────────────────────────────────────────────────

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Warning,
        Message = "Circuit open — transform skipped | Name={TransformName} Side={Side}")]
    public static partial void CircuitOpen(
        this ILogger logger,
        string transformName,
        string side);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Information,
        Message = "Circuit closed (recovered) | Name={TransformName}")]
    public static partial void CircuitClosed(
        this ILogger logger,
        string transformName);

    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Warning,
        Message = "Circuit opened (threshold exceeded) | Name={TransformName} FailureRatio={FailureRatio:F2}")]
    public static partial void CircuitOpened(
        this ILogger logger,
        string transformName,
        double failureRatio);

    // ──────────────────────────────────────────────────────────────
    // Payload
    // ──────────────────────────────────────────────────────────────

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Warning,
        Message = "IBufferTransformer attempted stream access — skipping transform | Name={TransformName}")]
    public static partial void BufferTransformStreamAccessViolation(
        this ILogger logger,
        string transformName);

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Warning,
        Message = "IStreamTransformer attempted buffer access — skipping transform | Name={TransformName}")]
    public static partial void StreamTransformBufferAccessViolation(
        this ILogger logger,
        string transformName);

    // ──────────────────────────────────────────────────────────────
    // Detail Provider
    // ──────────────────────────────────────────────────────────────

    [LoggerMessage(
        EventId = 1500,
        Level = LogLevel.Debug,
        Message = "TransformationDetail resolved | Method={Method} Path={Path} RequestTransforms={RequestCount} ResponseTransforms={ResponseCount}")]
    public static partial void DetailResolved(
        this ILogger logger,
        string method,
        string path,
        int requestCount,
        int responseCount);
}
