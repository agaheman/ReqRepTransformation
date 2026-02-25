using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Infrastructure.Logging;
using ReqRepTransformation.Core.Infrastructure.Telemetry;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Pipeline;

/// <summary>
/// Core pipeline execution engine.
///
/// Responsibilities:
/// 1. Sorts TransformEntry collections by Order ASC before execution.
/// 2. Falls back to PipelineOptions when TransformationDetail uses defaults:
///    - detail.TransformationTimeout == Zero  → _options.DefaultTimeout
///    - detail.HasExplicitFailureMode == false → _options.DefaultFailureMode
/// 3. Checks circuit breaker before each transform.
/// 4. Wraps each transform with a per-transform linked CancellationTokenSource timeout.
/// 5. Enforces IBufferTransform vs IStreamTransform payload compatibility.
/// 6. Handles FailureMode: StopPipeline / Continue / LogAndSkip.
/// 7. Emits [LoggerMessage] structured logs and OpenTelemetry spans per transform.
/// 8. Records circuit breaker success/failure after each execution.
/// </summary>
public sealed class PipelineExecutor
{
    private readonly ITransformCircuitBreaker _circuitBreaker;
    private readonly ILogger<PipelineExecutor>  _logger;
    private readonly PipelineOptions            _options;

    public PipelineExecutor(
        ITransformCircuitBreaker        circuitBreaker,
        IOptions<PipelineOptions>       options,
        ILogger<PipelineExecutor>       logger)
    {
        _circuitBreaker = circuitBreaker;
        _options        = options.Value;
        _logger         = logger;
    }

    // ──────────────────────────────────────────────────────────────
    // Public entry points
    // ──────────────────────────────────────────────────────────────

    public ValueTask ExecuteRequestAsync(IMessageContext context, TransformationDetail detail)
        => ExecuteCoreAsync(context, detail, detail.RequestTransformations, MessageSide.Request);

    public ValueTask ExecuteResponseAsync(IMessageContext context, TransformationDetail detail)
        => ExecuteCoreAsync(context, detail, detail.ResponseTransformations, MessageSide.Response);

    // ──────────────────────────────────────────────────────────────
    // Effective option resolution — _options as global fallback
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the effective per-transform timeout.
    /// Priority: TransformationDetail.TransformationTimeout (if > zero)
    ///           → _options.DefaultTimeout (global fallback from appsettings).
    /// </summary>
    private TimeSpan ResolveTimeout(TransformationDetail detail)
        => detail.TransformationTimeout > TimeSpan.Zero
            ? detail.TransformationTimeout
            : _options.DefaultTimeout;

    /// <summary>
    /// Resolves the effective failure mode.
    /// Priority: detail.FailureMode (when HasExplicitFailureMode = true)
    ///           → _options.DefaultFailureMode (global fallback).
    /// HasExplicitFailureMode guards against FailureMode.StopPipeline = 0 (enum default)
    /// being silently applied to routes that never set a failure mode.
    /// </summary>
    private FailureMode ResolveFailureMode(TransformationDetail detail)
        => detail.HasExplicitFailureMode
            ? detail.FailureMode
            : _options.DefaultFailureMode;

    // ──────────────────────────────────────────────────────────────
    // Core loop
    // ──────────────────────────────────────────────────────────────

    private async ValueTask ExecuteCoreAsync(
        IMessageContext             context,
        TransformationDetail        detail,
        IReadOnlyList<TransformEntry> entries,
        MessageSide                 side)
    {
        if (entries.Count == 0) return;

        var sideLabel         = side == MessageSide.Request
            ? TelemetryConstants.SideRequest
            : TelemetryConstants.SideResponse;
        var effectiveTimeout  = ResolveTimeout(detail);
        var effectiveFailMode = ResolveFailureMode(detail);

        // ── Sort by Order ASC — stable, allocation-minimal ────────
        // Use a local sorted span copy; avoid mutating the caller's list.
        // For small transform counts (≤ 20 typical) an insertion sort is faster
        // than Array.Sort + alloc, but we use LINQ OrderBy for readability here
        // because this runs once per request, not in a tight inner loop.
        var sorted = entries
            .OrderBy(e => e.Order)
            .ToArray(); // single allocation per pipeline execution

        var sw = Stopwatch.StartNew();
        _logger.PipelineStarting(sideLabel, context.Method, context.Address.AbsolutePath);

        using var pipelineActivity = TelemetryConstants.ActivitySource.StartActivity(
            $"reqrep.pipeline.{sideLabel}", ActivityKind.Internal);

        pipelineActivity?.SetTag(TelemetryConstants.AttrPipelineSide,  sideLabel);
        pipelineActivity?.SetTag(TelemetryConstants.AttrRequestMethod, context.Method);
        pipelineActivity?.SetTag(TelemetryConstants.AttrContentType,   context.Payload.ContentType ?? "unknown");

        if (detail.AllowParallelNonDependentTransforms)
            await ExecuteParallelAsync(context, sorted, sideLabel, effectiveTimeout, effectiveFailMode)
                .ConfigureAwait(false);
        else
            await ExecuteSequentialAsync(context, sorted, sideLabel, effectiveTimeout, effectiveFailMode)
                .ConfigureAwait(false);

        sw.Stop();
        _logger.PipelineCompleted(sideLabel, sorted.Length, sw.ElapsedMilliseconds);
    }

    private async ValueTask ExecuteSequentialAsync(
        IMessageContext context,
        TransformEntry[]   sorted,
        string             sideLabel,
        TimeSpan           timeout,
        FailureMode        failureMode)
    {
        // Index-based — no enumerator allocation in the hot path.
        for (int i = 0; i < sorted.Length; i++)
        {
            await ExecuteSingleAsync(context, sorted[i], sideLabel, timeout, failureMode)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask ExecuteParallelAsync(
        IMessageContext context,
        TransformEntry[]   sorted,
        string             sideLabel,
        TimeSpan           timeout,
        FailureMode        failureMode)
    {
        // Parallel execution path: Order is still respected as a signal of grouping,
        // but all entries run concurrently. Use only for header/address transforms —
        // never for JSON-mutating transforms (JsonNode is NOT thread-safe for writes).
        var tasks = new Task[sorted.Length];
        for (int i = 0; i < sorted.Length; i++)
        {
            var entry = sorted[i]; // capture
            tasks[i] = ExecuteSingleAsync(context, entry, sideLabel, timeout, failureMode).AsTask();
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async ValueTask ExecuteSingleAsync(
        IMessageContext context,
        TransformEntry  entry,
        string          sideLabel,
        TimeSpan        effectiveTimeout,
        FailureMode     effectiveFailureMode)
    {
        var transform = entry.Transform;

        // ── 1. Payload / type compatibility ───────────────────────
        if (transform is IBufferTransform && context.Payload.IsStreaming)
        {
            _logger.BufferTransformStreamAccessViolation(transform.Name);
            return;
        }

        // ── 2. ShouldApply ────────────────────────────────────────
        if (!transform.ShouldApply(context))
        {
            _logger.TransformSkipped(transform.Name, sideLabel);
            TelemetryConstants.TransformSkippedCounter.Add(1,
                new KeyValuePair<string, object?>(TelemetryConstants.AttrTransformName, transform.Name));
            return;
        }

        // ── 3. Circuit breaker ────────────────────────────────────
        if (_circuitBreaker.IsOpen(transform.Name))
        {
            _logger.CircuitOpen(transform.Name, sideLabel);
            TelemetryConstants.TransformSkippedCounter.Add(1,
                new KeyValuePair<string, object?>(TelemetryConstants.AttrTransformName, transform.Name),
                new KeyValuePair<string, object?>(TelemetryConstants.AttrCircuitState, "open"));

            await HandleCircuitOpenAsync(context, effectiveFailureMode, transform, sideLabel)
                .ConfigureAwait(false);
            return;
        }

        // ── 4. Execute with per-transform timeout (from _options) ─
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
        timeoutCts.CancelAfter(effectiveTimeout); // ← _options.DefaultTimeout used here

        var sw = Stopwatch.StartNew();
        _logger.TransformExecuting(transform.Name, sideLabel);

        using var activity = TelemetryConstants.ActivitySource.StartActivity(
            $"reqrep.transform.{transform.Name}", ActivityKind.Internal);
        activity?.SetTag(TelemetryConstants.AttrTransformName,  transform.Name);
        activity?.SetTag(TelemetryConstants.AttrTransformSide,  sideLabel);
        activity?.SetTag("transform.order",                      entry.Order);

        try
        {
            await transform.ApplyAsync(context, timeoutCts.Token).ConfigureAwait(false);

            sw.Stop();
            _circuitBreaker.RecordSuccess(transform.Name);
            TelemetryConstants.TransformExecutedCounter.Add(1,
                new KeyValuePair<string, object?>(TelemetryConstants.AttrTransformName, transform.Name));
            activity?.SetTag(TelemetryConstants.AttrTransformResult, TelemetryConstants.ResultOk);
            _logger.TransformCompleted(transform.Name, sideLabel, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested)
        {
            // Timeout (not client abort)
            sw.Stop();
            _circuitBreaker.RecordFailure(transform.Name);
            TelemetryConstants.TransformFailedCounter.Add(1,
                new KeyValuePair<string, object?>(TelemetryConstants.AttrTransformName, transform.Name));
            activity?.SetTag(TelemetryConstants.AttrTransformResult, TelemetryConstants.ResultFailed);
            _logger.TransformTimedOut(transform.Name, sideLabel, effectiveTimeout.TotalMilliseconds);

            await HandleFailureAsync(context, effectiveFailureMode, transform, sideLabel,
                new TimeoutException(
                    $"Transform '{transform.Name}' exceeded timeout of {effectiveTimeout.TotalMilliseconds}ms."))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _circuitBreaker.RecordFailure(transform.Name);
            TelemetryConstants.TransformFailedCounter.Add(1,
                new KeyValuePair<string, object?>(TelemetryConstants.AttrTransformName, transform.Name));
            activity?.SetTag(TelemetryConstants.AttrTransformResult, TelemetryConstants.ResultFailed);
            activity?.SetTag(TelemetryConstants.AttrErrorType, ex.GetType().Name);
            _logger.TransformFailed(ex, transform.Name, sideLabel, effectiveFailureMode.ToString());

            await HandleFailureAsync(context, effectiveFailureMode, transform, sideLabel, ex)
                .ConfigureAwait(false);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Failure handlers
    // ──────────────────────────────────────────────────────────────

    private ValueTask HandleFailureAsync(
        IMessageContext  context,
        FailureMode      mode,
        ITransformation  transform,
        string           sideLabel,
        Exception        ex)
    {
        if (mode == FailureMode.StopPipeline)
        {
            _logger.PipelineAborted(sideLabel, transform.Name);
            throw new TransformationException(
                transform.Name, context.Side,
                $"Transform '{transform.Name}' failed and FailureMode is StopPipeline.", ex);
        }

        return ValueTask.CompletedTask; // LogAndSkip / Continue: already logged
    }

    private ValueTask HandleCircuitOpenAsync(
        IMessageContext  context,
        FailureMode      mode,
        ITransformation  transform,
        string           sideLabel)
    {
        if (mode == FailureMode.StopPipeline)
        {
            _logger.PipelineAborted(sideLabel, transform.Name);
            throw new TransformationException(
                transform.Name, context.Side,
                $"Transform '{transform.Name}' circuit is open and FailureMode is StopPipeline.");
        }

        return ValueTask.CompletedTask;
    }
}
