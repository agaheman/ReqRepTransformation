using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReqRepTransformation.Core.Abstractions;
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
    /// <list type="bullet">
    ///   <item><see cref="PipelineExecutor"/> — singleton, stateless after options resolved.</item>
    ///   <item><see cref="IMessageTransformationPipeline"/> → <see cref="TransformationPipeline"/> — singleton.</item>
    ///   <item><see cref="IRedactionPolicy"/> → <see cref="DefaultRedactionPolicy"/> — singleton.</item>
    ///   <item><see cref="PipelineOptions"/> bound from configuration section "ReqRepTransformation".</item>
    /// </list>
    ///
    /// <para>
    /// Resilience (retry, circuit-breaking) is intentionally omitted from this layer.
    /// It belongs at the HTTP-client boundary — use Polly or
    /// <c>Microsoft.Extensions.Http.Resilience</c> on the outbound <see cref="HttpClient"/>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddReqRepTransformationCore(
        this IServiceCollection services,
        Action<PipelineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services
            .AddOptions<PipelineOptions>()
            .BindConfiguration(PipelineOptions.SectionName);

        if (configure is not null)
            optionsBuilder.PostConfigure(configure);

        services.TryAddSingleton<IRedactionPolicy, DefaultRedactionPolicy>();
        services.TryAddSingleton<PipelineExecutor>();
        services.TryAddSingleton<IMessageTransformationPipeline, TransformationPipeline>();

        return services;
    }
}
