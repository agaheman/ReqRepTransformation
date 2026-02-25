using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.BuiltInTransformers;

/// <summary>
/// Converts a list of <see cref="RouteTransformerEntry"/> records (from the DB / provider)
/// into a fully-configured <see cref="TransformationDetail"/> by:
///
///   1. Calling <c>IKeyedServiceProvider.GetKeyedService&lt;ITransformer&gt;(entry.TransformerKey)</c>
///      to get a fresh transient instance for each entry.
///   2. Calling <c>transformer.Configure(new TransformerParams(entry.ParamsJson))</c>
///      to inject the per-route JSON params.
///   3. Wrapping the configured instance in a <see cref="TransformEntry"/> with the
///      correct <see cref="RouteTransformerEntry.Order"/>.
///   4. Partitioning entries by <see cref="TransformerSide"/> and building the final
///      <see cref="TransformationDetail"/>.
///
/// Unknown transformer keys are logged as warnings and skipped — the pipeline continues
/// with all other registered transformers.
/// </summary>
public sealed class TransformationDetailBuilder
{
    private readonly IServiceProvider           _serviceProvider;
    private readonly ILogger<TransformationDetailBuilder> _logger;

    public TransformationDetailBuilder(
        IServiceProvider                   serviceProvider,
        ILogger<TransformationDetailBuilder> logger)
    {
        _serviceProvider = serviceProvider;
        _logger          = logger;
    }

    /// <summary>
    /// Builds a <see cref="TransformationDetail"/> from a flat list of transformer entries.
    /// </summary>
    /// <param name="entries">
    ///   Typically returned by <c>ITransformationDetailProvider.GetCurrentRouteTransformers()</c>.
    /// </param>
    /// <param name="timeout">
    ///   Route-specific timeout. <see cref="TimeSpan.Zero"/> = use <c>PipelineOptions.DefaultTimeout</c>.
    /// </param>
    /// <param name="failureMode">Route-specific failure mode (null = use global default).</param>
    public TransformationDetail Build(
        IReadOnlyList<RouteTransformerEntry> entries,
        TimeSpan?    timeout     = null,
        FailureMode? failureMode = null)
    {
        var requestEntries  = new List<TransformEntry>();
        var responseEntries = new List<TransformEntry>();

        foreach (var entry in entries)
        {
            var transformer = ResolveAndConfigure(entry);
            if (transformer is null) continue;

            var transformEntry = TransformEntry.At(entry.Order, transformer);

            if (entry.Side == TransformerSide.Request)
                requestEntries.Add(transformEntry);
            else
                responseEntries.Add(transformEntry);
        }

        return new TransformationDetail
        {
            RequestTransformations  = requestEntries,
            ResponseTransformations = responseEntries,
            TransformationTimeout   = timeout ?? TimeSpan.Zero,
            FailureMode             = failureMode ?? FailureMode.LogAndSkip,
            HasExplicitFailureMode  = failureMode.HasValue
        };
    }

    // ──────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────

    private ITransformer? ResolveAndConfigure(RouteTransformerEntry entry)
    {
        // Resolve keyed transient from DI
        var transformer = _serviceProvider
            .GetKeyedService<ITransformer>(entry.TransformerKey);

        if (transformer is null)
        {
            _logger.LogWarning(
                "Unknown transformer key '{Key}' for entry Order={Order}. " +
                "Ensure AddBuiltInTransformers() was called or a custom transformer is registered. Skipping.",
                entry.TransformerKey, entry.Order);
            return null;
        }

        // Pass JSON params — transformer reads its typed config here
        try
        {
            transformer.Configure(new TransformerParams(entry.ParamsJson));
        }
        catch (TransformerParamsMissingException ex)
        {
            _logger.LogError(
                "Transformer '{Key}' (Order={Order}) is missing required param '{Param}'. " +
                "Check the paramsJson in the database. Skipping.",
                entry.TransformerKey, entry.Order, ex.ParamKey);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Transformer '{Key}' (Order={Order}) threw during Configure(). Skipping.",
                entry.TransformerKey, entry.Order);
            return null;
        }

        return transformer;
    }
}
