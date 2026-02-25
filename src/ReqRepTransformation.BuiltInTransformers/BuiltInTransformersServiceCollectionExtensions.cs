using Microsoft.Extensions.DependencyInjection;
using ReqRepTransformation.BuiltInTransformers.Address;
using ReqRepTransformation.BuiltInTransformers.Auth;
using ReqRepTransformation.BuiltInTransformers.Body;
using ReqRepTransformation.BuiltInTransformers.Headers;
using ReqRepTransformation.Core.Abstractions;

namespace ReqRepTransformation.BuiltInTransformers;

/// <summary>
/// Registers all built-in <see cref="ITransformer"/> implementations as keyed transient services.
///
/// Keyed pattern:
/// <code>
/// services.AddKeyedTransient&lt;ITransformer, AddHeaderTransformer&gt;(TransformerKeys.AddHeader);
/// </code>
///
/// Resolution in <c>TransformationDetailBuilder</c>:
/// <code>
/// var transformer = sp.GetRequiredKeyedService&lt;ITransformer&gt;(entry.TransformerKey);
/// transformer.Configure(new TransformerParams(entry.ParamsJson));
/// </code>
///
/// Transient lifetime is intentional:
///   - <see cref="ITransformer.Configure"/> makes each instance stateful (it stores parsed params).
///   - A new instance is created per resolution so route A's params don't bleed into route B.
///   - <c>TransformationDetailBuilder</c> caches the resolved + configured instances per route,
///     so the transient cost is paid only on cache miss.
/// </summary>
public static class BuiltInTransformersServiceCollectionExtensions
{
    /// <summary>
    /// Registers all 22 built-in transformers as keyed transient <see cref="ITransformer"/> services.
    /// Also registers <see cref="TransformationDetailBuilder"/> as a singleton.
    ///
    /// Call after <c>AddReqRepTransformationCore()</c> or <c>AddReqRepTransformationAspNet()</c>.
    /// </summary>
    public static IServiceCollection AddBuiltInTransformers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ── Headers ───────────────────────────────────────────────────────────
        services.AddKeyedTransient<ITransformer, AddHeaderTransformer>
            (TransformerKeys.AddHeader);
        services.AddKeyedTransient<ITransformer, RemoveHeaderTransformer>
            (TransformerKeys.RemoveHeader);
        services.AddKeyedTransient<ITransformer, RenameHeaderTransformer>
            (TransformerKeys.RenameHeader);
        services.AddKeyedTransient<ITransformer, AppendHeaderTransformer>
            (TransformerKeys.AppendHeader);
        services.AddKeyedTransient<ITransformer, CorrelationIdTransformer>
            (TransformerKeys.CorrelationId);
        services.AddKeyedTransient<ITransformer, RequestIdTransformer>
            (TransformerKeys.RequestId);
        services.AddKeyedTransient<ITransformer, RemoveInternalResponseHeadersTransformer>
            (TransformerKeys.RemoveInternalResponseHeaders);
        services.AddKeyedTransient<ITransformer, GatewayResponseTagTransformer>
            (TransformerKeys.GatewayResponseTag);
        services.AddKeyedTransient<ITransformer, UploadMetadataHeaderTransformer>
            (TransformerKeys.UploadMetadataHeader);

        // ── Address ───────────────────────────────────────────────────────────
        services.AddKeyedTransient<ITransformer, PathPrefixRewriteTransformer>
            (TransformerKeys.PathPrefixRewrite);
        services.AddKeyedTransient<ITransformer, PathRegexRewriteTransformer>
            (TransformerKeys.PathRegexRewrite);
        services.AddKeyedTransient<ITransformer, AddQueryParamTransformer>
            (TransformerKeys.AddQueryParam);
        services.AddKeyedTransient<ITransformer, RemoveQueryParamTransformer>
            (TransformerKeys.RemoveQueryParam);
        services.AddKeyedTransient<ITransformer, HostRewriteTransformer>
            (TransformerKeys.HostRewrite);
        services.AddKeyedTransient<ITransformer, MethodOverrideTransformer>
            (TransformerKeys.MethodOverride);

        // ── JSON Body ─────────────────────────────────────────────────────────
        services.AddKeyedTransient<ITransformer, JsonFieldAddTransformer>
            (TransformerKeys.JsonFieldAdd);
        services.AddKeyedTransient<ITransformer, JsonFieldRemoveTransformer>
            (TransformerKeys.JsonFieldRemove);
        services.AddKeyedTransient<ITransformer, JsonFieldRenameTransformer>
            (TransformerKeys.JsonFieldRename);
        services.AddKeyedTransient<ITransformer, JsonNestedFieldSetTransformer>
            (TransformerKeys.JsonNestedFieldSet);
        services.AddKeyedTransient<ITransformer, JsonGatewayMetadataTransformer>
            (TransformerKeys.JsonGatewayMetadata);

        // ── Auth / JWT ────────────────────────────────────────────────────────
        services.AddKeyedTransient<ITransformer, JwtForwardTransformer>
            (TransformerKeys.JwtForward);
        services.AddKeyedTransient<ITransformer, JwtClaimsExtractTransformer>
            (TransformerKeys.JwtClaimsExtract);
        services.AddKeyedTransient<ITransformer, StripAuthorizationTransformer>
            (TransformerKeys.StripAuthorization);

        // ── Builder (resolves keyed services → TransformationDetail) ─────────
        services.AddSingleton<TransformationDetailBuilder>();

        return services;
    }
}
