using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Infrastructure.CircuitBreaker;
using ReqRepTransformation.Core.Infrastructure.Redaction;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Core.Pipeline;

namespace ReqRepTransformation.Core.DI;

/// <summary>
/// Extension methods for registering ReqRepTransformation.Core services.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers all core ReqRepTransformation services:
    /// - PipelineExecutor
    /// - TransformationPipeline (IMessageTransformationPipeline)
    /// - SlidingWindowCircuitBreaker (ITransformCircuitBreaker)
    /// - DefaultRedactionPolicy (IRedactionPolicy)
    /// - PipelineOptions from configuration
    ///
    /// <para>
    /// Call this first, then optionally AddReqRepAspNet() or add transforms manually.
    /// Register your ITransformationDetailProvider after this call.
    /// </para>
    /// </summary>
    public static IServiceCollection AddReqRepTransformationCore(
        this IServiceCollection services,
        Action<PipelineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Options
        var optionsBuilder = services
            .AddOptions<PipelineOptions>()
            .BindConfiguration(PipelineOptions.SectionName);

        if (configure is not null)
            optionsBuilder.PostConfigure(configure);

        // Infrastructure — singleton, thread-safe
        services.TryAddSingleton<ITransformCircuitBreaker, SlidingWindowCircuitBreaker>();
        services.TryAddSingleton<IRedactionPolicy, DefaultRedactionPolicy>();

        // Pipeline — singleton (stateless once options resolved)
        services.TryAddSingleton<PipelineExecutor>();
        services.TryAddSingleton<IMessageTransformationPipeline, TransformationPipeline>();

        return services;
    }
}
