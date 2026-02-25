using Microsoft.Extensions.Caching.Memory;
using ReqRepTransformation.BuiltInTransformers;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace SampleApiTestApp.Providers;

/// <summary>
/// Sample <see cref="ITransformationDetailProvider"/> that simulates loading transformer
/// configuration from a database.
///
/// Architecture:
///   1. <see cref="GetCurrentRouteTransformers"/> simulates a DB query — returns a list of
///      <see cref="RouteTransformerEntry"/> records (TransformerKey + ParamsJson + Order + Side).
///   2. <see cref="Resolve"/> calls <see cref="GetCurrentRouteTransformers"/> and passes
///      the entries to <see cref="TransformationDetailBuilder.Build"/> which resolves the
///      keyed <see cref="ITransformer"/> services from DI and calls Configure(params) on each.
///   3. Results are cached per method+path to avoid repeated DI resolution.
///
/// In production, replace <see cref="GetCurrentRouteTransformers"/> with a real repository
/// that executes:
/// <code>
///   SELECT transformer_key, params_json, execution_order, transformer_side
///   FROM route_transformers
///   WHERE method = @method AND path_pattern = @path AND is_active = true
///   ORDER BY execution_order ASC
/// </code>
/// </summary>
public sealed class SampleTransformationDetailProvider : ITransformationDetailProvider
{
    private readonly TransformationDetailBuilder _builder;
    private readonly IMemoryCache                _cache;
    private readonly ILogger<SampleTransformationDetailProvider> _logger;

    public SampleTransformationDetailProvider(
        TransformationDetailBuilder builder,
        IMemoryCache  cache,
        ILogger<SampleTransformationDetailProvider> logger)
    {
        _builder = builder;
        _cache   = cache;
        _logger  = logger;
    }

    public ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context, CancellationToken ct = default)
    {
        var cacheKey = $"{context.Method}:{NormalizePath(context.Address.AbsolutePath)}";

        if (_cache.TryGetValue(cacheKey, out TransformationDetail? cached) && cached is not null)
            return ValueTask.FromResult(cached);

        var detail = Resolve(context);

        _cache.Set(cacheKey, detail, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
            SlidingExpiration               = TimeSpan.FromMinutes(2)
        });

        _logger.LogDebug(
            "TransformationDetail resolved | {Method} {Path} | Req:{Rq} Res:{Rs}",
            context.Method, context.Address.AbsolutePath,
            detail.RequestTransformations.Count,
            detail.ResponseTransformations.Count);

        return ValueTask.FromResult(detail);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Resolve: calls GetCurrentRouteTransformers and builds TransformationDetail
    // ──────────────────────────────────────────────────────────────────────────

    private TransformationDetail Resolve(IMessageContext ctx)
    {
        var entries = GetCurrentRouteTransformers(ctx.Method, ctx.Address.AbsolutePath);

        if (entries.Count == 0)
            return TransformationDetail.Empty;

        // TransformationDetailBuilder resolves each keyed ITransformer from DI,
        // calls Configure(new TransformerParams(entry.ParamsJson)) on each instance,
        // and assembles the TransformationDetail sorted by Order.
        return _builder.Build(
            entries,
            timeout:     TimeSpan.FromSeconds(3),
            failureMode: FailureMode.LogAndSkip);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GetCurrentRouteTransformers
    // Simulates loading from DB: returns RouteTransformerEntry list for the route.
    //
    // In production replace with:
    //   await _repository.GetRouteTransformersAsync(method, path, ct)
    //
    // Each RouteTransformerEntry has:
    //   TransformerKey — matches a TransformerKeys constant (= keyed service key)
    //   ParamsJson     — JSON string with the transformer's config (can be null)
    //   Order          — execution order within the side (ASC)
    //   Side           — Request or Response
    // ──────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<RouteTransformerEntry> GetCurrentRouteTransformers(
        string method, string path)
    {
        // ── POST /api/orders ─────────────────────────────────────────────────
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith("/api/orders", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                // Request-side transformers (Order drives execution sequence)
                RouteTransformerEntry.Create(
                    TransformerKeys.CorrelationId,
                    TransformerSide.Request, order: 10,
                    paramsJson: null),                     // uses default X-Correlation-Id

                RouteTransformerEntry.Create(
                    TransformerKeys.RequestId,
                    TransformerSide.Request, order: 20,
                    paramsJson: null),

                RouteTransformerEntry.Create(
                    TransformerKeys.JwtForward,
                    TransformerSide.Request, order: 30,
                    paramsJson: null),

                RouteTransformerEntry.Create(
                    TransformerKeys.JwtClaimsExtract,
                    TransformerSide.Request, order: 40,
                    // pipe-separated "claimType=HeaderName" pairs stored as JSON string
                    paramsJson: """{"claimMap":"sub=X-User-Id|email=X-User-Email"}"""),

                RouteTransformerEntry.Create(
                    TransformerKeys.JsonGatewayMetadata,
                    TransformerSide.Request, order: 50,
                    paramsJson: """{"version":"2.0"}"""),

                // Response-side transformers
                RouteTransformerEntry.Create(
                    TransformerKeys.RemoveInternalResponseHeaders,
                    TransformerSide.Response, order: 10,
                    paramsJson: """{"headers":"X-Internal-Token|X-Backend-Version|Server|X-Powered-By"}"""),

                RouteTransformerEntry.Create(
                    TransformerKeys.GatewayResponseTag,
                    TransformerSide.Response, order: 20,
                    paramsJson: """{"version":"2.0","instanceId":"gateway-sample"}""")
            };
        }

        // ── GET /api/products ────────────────────────────────────────────────
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith("/api/products", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                RouteTransformerEntry.Create(
                    TransformerKeys.CorrelationId,
                    TransformerSide.Request, order: 10,
                    paramsJson: null),

                RouteTransformerEntry.Create(
                    TransformerKeys.JwtForward,
                    TransformerSide.Request, order: 20,
                    paramsJson: null),

                RouteTransformerEntry.Create(
                    TransformerKeys.PathPrefixRewrite,
                    TransformerSide.Request, order: 30,
                    paramsJson: """{"fromPrefix":"/api/products","toPrefix":"/catalog"}"""),

                RouteTransformerEntry.Create(
                    TransformerKeys.RemoveInternalResponseHeaders,
                    TransformerSide.Response, order: 10,
                    paramsJson: null)
            };
        }

        // ── ANY /api/admin ───────────────────────────────────────────────────
        if (path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                RouteTransformerEntry.Create(
                    TransformerKeys.CorrelationId,
                    TransformerSide.Request, order: 10,
                    paramsJson: null),

                RouteTransformerEntry.Create(
                    TransformerKeys.StripAuthorization,
                    TransformerSide.Request, order: 20,
                    paramsJson: null),

                RouteTransformerEntry.Create(
                    TransformerKeys.AddHeader,
                    TransformerSide.Request, order: 30,
                    // params carry what the add-header transformer needs
                    paramsJson: """{"key":"X-Internal-Key","value":"sample-key-change-in-prod","overwrite":true}"""),

                RouteTransformerEntry.Create(
                    TransformerKeys.RemoveInternalResponseHeaders,
                    TransformerSide.Response, order: 10,
                    paramsJson: null)
            };
        }

        // ── Default: correlation ID only ─────────────────────────────────────
        return new[]
        {
            RouteTransformerEntry.Create(
                TransformerKeys.CorrelationId,
                TransformerSide.Request, order: 10,
                paramsJson: null)
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    private static string NormalizePath(string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length; i++)
            if (long.TryParse(segs[i], out _) || Guid.TryParse(segs[i], out _))
                segs[i] = "{id}";
        return "/" + string.Join('/', segs);
    }
}
