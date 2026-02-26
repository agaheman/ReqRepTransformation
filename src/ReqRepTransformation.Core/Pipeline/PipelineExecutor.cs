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
/// 1. Sort <see cref="TransformEntry"/> list by <see cref="TransformEntry.Order"/> ASC.
/// 2. Resolve effective timeout and failure mode (detail → options fallback).
/// 3. Wrap each transform in a per-transform linked <see cref="CancellationTokenSource"/> timeout.
/// 4. Enforce payload-type discipline at the call site via typed context dispatch:
///    <see cref="IBufferTransformer"/> receives <see cref="IBufferMessageContext"/>;
///    <see cref="IStreamTransformer"/> receives <see cref="IStreamMessageContext"/>.
///    The transformer can only call methods that exist on its typed context — wrong
///    payload method = compile error inside the transformer implementation.
/// 5. Handle <see cref="FailureMode"/>: StopPipeline / LogAndSkip / Continue.
/// 6. Emit <c>[LoggerMessage]</c> structured logs and OpenTelemetry spans per transform.
///
/// Resilience note:
/// Circuit breaking and retries are NOT the responsibility of this layer.
/// Apply <c>Polly</c> or <c>Microsoft.Extensions.Http.Resilience</c> at the outbound
/// <see cref="HttpClient"/> boundary where transport-level failures are observable.
/// </summary>
public sealed class PipelineExecutor
{
    private readonly ILogger<PipelineExecutor> _logger;
    private readonly PipelineOptions           _options;

    public PipelineExecutor(
        IOptions<PipelineOptions> options,
        ILogger<PipelineExecutor> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    // ── Public entry points ───────────────────────────────────────────────────

    public ValueTask ExecuteRequestAsync(IMessageContext context, TransformationDetail detail)
        => ExecuteCoreAsync(context, detail, detail.RequestTransformations, MessageSide.Request);

    public ValueTask ExecuteResponseAsync(IMessageContext context, TransformationDetail detail)
        => ExecuteCoreAsync(context, detail, detail.ResponseTransformations, MessageSide.Response);

    // ── Option resolution ─────────────────────────────────────────────────────

    private TimeSpan ResolveTimeout(TransformationDetail detail)
        => detail.TransformationTimeout > TimeSpan.Zero
            ? detail.TransformationTimeout
            : _options.DefaultTimeout;

    private FailureMode ResolveFailureMode(TransformationDetail detail)
        => detail.HasExplicitFailureMode
            ? detail.FailureMode
            : _options.DefaultFailureMode;

    // ── Core loop ─────────────────────────────────────────────────────────────

    private async ValueTask ExecuteCoreAsync(
        IMessageContext               context,
        TransformationDetail          detail,
        IReadOnlyList<TransformEntry> entries,
        MessageSide                   side)
    {
        if (entries.Count == 0) return;

        var sideLabel        = side == MessageSide.Request
            ? TelemetryConstants.SideRequest
            : TelemetryConstants.SideResponse;
        var effectiveTimeout = ResolveTimeout(detail);
        var effectiveMode    = ResolveFailureMode(detail);

        var sorted = entries.OrderBy(e => e.Order).ToArray();

        var sw = Stopwatch.StartNew();
        _logger.PipelineStarting(sideLabel, context.Method, context.Address.AbsolutePath);

        using var pipelineActivity = TelemetryConstants.ActivitySource.StartActivity(
            $"reqrep.pipeline.{sideLabel}", ActivityKind.Internal);

        pipelineActivity?.SetTag(TelemetryConstants.AttrPipelineSide,  sideLabel);
        pipelineActivity?.SetTag(TelemetryConstants.AttrRequestMethod,  context.Method);
        pipelineActivity?.SetTag(TelemetryConstants.AttrContentType,    context.Payload.ContentType ?? "unknown");

        if (detail.AllowParallelNonDependentTransforms)
            await ExecuteParallelAsync(context, sorted, sideLabel, effectiveTimeout, effectiveMode)
                .ConfigureAwait(false);
        else
            await ExecuteSequentialAsync(context, sorted, sideLabel, effectiveTimeout, effectiveMode)
                .ConfigureAwait(false);

        sw.Stop();
        _logger.PipelineCompleted(sideLabel, sorted.Length, sw.ElapsedMilliseconds);
    }

    private async ValueTask ExecuteSequentialAsync(
        IMessageContext  context,
        TransformEntry[] sorted,
        string           sideLabel,
        TimeSpan         timeout,
        FailureMode      failureMode)
    {
        for (int i = 0; i < sorted.Length; i++)
            await ExecuteSingleAsync(context, sorted[i], sideLabel, timeout, failureMode)
                .ConfigureAwait(false);
    }

    private async ValueTask ExecuteParallelAsync(
        IMessageContext  context,
        TransformEntry[] sorted,
        string           sideLabel,
        TimeSpan         timeout,
        FailureMode      failureMode)
    {
        var tasks = new Task[sorted.Length];
        for (int i = 0; i < sorted.Length; i++)
        {
            var entry = sorted[i];
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

        if (!transform.ShouldApply(context))
        {
            _logger.TransformSkipped(transform.Name, sideLabel);
            TelemetryConstants.TransformSkippedCounter.Add(1,
                new KeyValuePair<string, object?>(TelemetryConstants.AttrTransformName, transform.Name));
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
        timeoutCts.CancelAfter(effectiveTimeout);

        var sw = Stopwatch.StartNew();
        _logger.TransformExecuting(transform.Name, sideLabel);

        using var activity = TelemetryConstants.ActivitySource.StartActivity(
            $"reqrep.transform.{transform.Name}", ActivityKind.Internal);
        activity?.SetTag(TelemetryConstants.AttrTransformName, transform.Name);
        activity?.SetTag(TelemetryConstants.AttrTransformSide, sideLabel);
        activity?.SetTag("transform.order",                     entry.Order);

        try
        {
            // Typed context dispatch — enforces payload discipline at the call site.
            // IBufferTransformer.ApplyAsync only receives IBufferMessageContext → IBufferPayload.
            // IStreamTransformer.ApplyAsync only receives IStreamMessageContext → IStreamPayload.
            // Wrong payload method = compile error inside the transformer, not a runtime skip.
            await DispatchApplyAsync(context, transform, timeoutCts.Token).ConfigureAwait(false);

            sw.Stop();
            TelemetryConstants.TransformExecutedCounter.Add(1,
                new KeyValuePair<string, object?>(TelemetryConstants.AttrTransformName, transform.Name));
            activity?.SetTag(TelemetryConstants.AttrTransformResult, TelemetryConstants.ResultOk);
            _logger.TransformCompleted(transform.Name, sideLabel, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested)
        {
            sw.Stop();
            TelemetryConstants.TransformFailedCounter.Add(1,
                new KeyValuePair<string, object?>(TelemetryConstants.AttrTransformName, transform.Name));
            activity?.SetTag(TelemetryConstants.AttrTransformResult, TelemetryConstants.ResultFailed);
            _logger.TransformTimedOut(transform.Name, sideLabel, effectiveTimeout.TotalMilliseconds);

            HandleFailure(context, effectiveFailureMode, transform, sideLabel,
                new TimeoutException(
                    $"Transform '{transform.Name}' exceeded timeout of {effectiveTimeout.TotalMilliseconds}ms."));
        }
        catch (Exception ex)
        {
            sw.Stop();
            TelemetryConstants.TransformFailedCounter.Add(1,
                new KeyValuePair<string, object?>(TelemetryConstants.AttrTransformName, transform.Name));
            activity?.SetTag(TelemetryConstants.AttrTransformResult, TelemetryConstants.ResultFailed);
            activity?.SetTag(TelemetryConstants.AttrErrorType, ex.GetType().Name);
            _logger.TransformFailed(ex, transform.Name, sideLabel, effectiveFailureMode.ToString());

            HandleFailure(context, effectiveFailureMode, transform, sideLabel, ex);
        }
    }

    /// <summary>
    /// Presents the correctly-narrowed typed context to each transformer.
    ///
    /// <see cref="IBufferTransformer"/>: receives <see cref="IBufferMessageContext"/>,
    ///   whose <see cref="IBufferMessageContext.Payload"/> is <see cref="IBufferPayload"/>.
    ///   <c>GetPipeReaderAsync</c> does not exist on <see cref="IBufferPayload"/> —
    ///   calling it from an <see cref="IBufferTransformer"/> is a compile error.
    ///
    /// <see cref="IStreamTransformer"/>: receives <see cref="IStreamMessageContext"/>,
    ///   whose <see cref="IStreamMessageContext.Payload"/> is <see cref="IStreamPayload"/>.
    ///   <c>GetJsonAsync</c> and <c>GetBufferAsync</c> do not exist on <see cref="IStreamPayload"/> —
    ///   calling them from an <see cref="IStreamTransformer"/> is a compile error.
    ///
    /// <see cref="MessageContextBase"/> implements both interfaces, so both casts are safe.
    /// </summary>
    private static ValueTask DispatchApplyAsync(
        IMessageContext   context,
        ITransformer      transform,
        CancellationToken ct)
    {
        if (transform is IBufferTransformer bufferTransformer)
            return bufferTransformer.ApplyAsync((IBufferMessageContext)context, ct);

        if (transform is IStreamTransformer streamTransformer)
            return streamTransformer.ApplyAsync((IStreamMessageContext)context, ct);

        // Sealed enforcement: ITransformer must be implemented via the two sub-interfaces.
        throw new InvalidOperationException(
            $"Transformer '{transform.Name}' implements ITransformer directly. " +
            "Implement IBufferTransformer or IStreamTransformer instead.");
    }

    // ── Failure handling ──────────────────────────────────────────────────────

    private void HandleFailure(
        IMessageContext context,
        FailureMode     mode,
        ITransformer    transform,
        string          sideLabel,
        Exception       ex)
    {
        if (mode != FailureMode.StopPipeline) return;

        _logger.PipelineAborted(sideLabel, transform.Name);
        throw new TransformationException(
            transform.Name, context.Side,
            $"Transform '{transform.Name}' failed and FailureMode is StopPipeline.", ex);
    }
}
