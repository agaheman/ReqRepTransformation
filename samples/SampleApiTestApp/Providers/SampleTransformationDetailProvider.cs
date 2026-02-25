using Microsoft.Extensions.Caching.Memory;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Transforms.Address;
using ReqRepTransformation.Transforms.Auth;
using ReqRepTransformation.Transforms.Body;
using ReqRepTransformation.Transforms.Headers;

namespace SampleApiTestApp.Providers;

/// <summary>
/// Sample ITransformationDetailProvider: route-based, in-memory, cached.
///
/// Route rules:
///   POST /api/orders  → correlationId(10) → requestId(20) → jwtForward(30) → jwtClaims(40) → gatewayMeta(50)
///                       response: removeInternal(10) → gatewayTag(20)
///   GET  /api/products → correlationId(10) → jwtForward(20) → catalogRewrite(30)
///                        response: removeInternal(10)
///   ANY  /api/admin   → correlationId(10) → stripAuth(20) → internalKey(30)  [StopPipeline]
///   default           → correlationId(10) only
///
/// TransformEntry.Order governs execution sequence — PipelineExecutor sorts ASC.
/// Replace this class with a database-backed provider in production.
/// </summary>
public sealed class SampleTransformationDetailProvider : ITransformationDetailProvider
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<SampleTransformationDetailProvider> _logger;

    // ── Shared, stateless transform instances (safe to reuse across routes) ──
    private static readonly CorrelationIdTransform          _correlationId  = new();
    private static readonly RequestIdPropagationTransform   _requestId      = new();
    private static readonly JwtForwardTransform             _jwtForward     = new();
    private static readonly RemoveInternalResponseHeadersTransform _removeInternal = new();
    private static readonly GatewayResponseTagTransform     _gatewayTag     = new();
    private static readonly JsonGatewayMetadataTransform    _gatewayMeta    = new();
    private static readonly StripAuthorizationTransform     _stripAuth      = new();
    private static readonly PathPrefixRewriteTransform      _catalogRewrite = new("/api/products", "/catalog");
    private static readonly AddHeaderTransform              _internalKey    = new("X-Internal-Key", "sample-key-use-secrets-manager");

    private static readonly JwtClaimsExtractTransform _jwtClaims = new(
        new Dictionary<string, string>
        {
            ["sub"]   = "X-User-Id",
            ["email"] = "X-User-Email"
        });

    public SampleTransformationDetailProvider(
        IMemoryCache cache,
        ILogger<SampleTransformationDetailProvider> logger)
    {
        _cache  = cache;
        _logger = logger;
    }

    public ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context, CancellationToken ct = default)
    {
        var key = $"{context.Method}:{NormalizePath(context.Address.AbsolutePath)}";

        if (_cache.TryGetValue(key, out TransformationDetail? cached) && cached is not null)
            return ValueTask.FromResult(cached);

        var detail = Resolve(context);

        _cache.Set(key, detail, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
            SlidingExpiration               = TimeSpan.FromMinutes(2)
        });

        _logger.LogDebug(
            "TransformationDetail resolved | {Method} {Path} | Req:{R} Res:{Rs}",
            context.Method, context.Address.AbsolutePath,
            detail.RequestTransformations.Count,
            detail.ResponseTransformations.Count);

        return ValueTask.FromResult(detail);
    }

    // ──────────────────────────────────────────────────────────────
    // Route matching — Order values drive execution sequence
    // ──────────────────────────────────────────────────────────────

    private static TransformationDetail Resolve(IMessageContext ctx)
    {
        var path   = ctx.Address.AbsolutePath;
        var method = ctx.Method;

        // POST /api/orders
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith("/api/orders", StringComparison.OrdinalIgnoreCase))
        {
            return new TransformationDetail
            {
                RequestTransformations = new[]
                {
                    TransformEntry.At(10, _correlationId),  // 1st — inject correlation id
                    TransformEntry.At(20, _requestId),      // 2nd — inject request id
                    TransformEntry.At(30, _jwtForward),     // 3rd — forward JWT
                    TransformEntry.At(40, _jwtClaims),      // 4th — extract claims → headers
                    TransformEntry.At(50, _gatewayMeta)     // 5th — inject _gateway JSON field
                },
                ResponseTransformations = new[]
                {
                    TransformEntry.At(10, _removeInternal), // strip backend headers
                    TransformEntry.At(20, _gatewayTag)      // tag response
                },
                TransformationTimeout  = TimeSpan.FromSeconds(3),
                FailureMode            = FailureMode.LogAndSkip,
                HasExplicitFailureMode = true
            };
        }

        // GET /api/products
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith("/api/products", StringComparison.OrdinalIgnoreCase))
        {
            return new TransformationDetail
            {
                RequestTransformations = new[]
                {
                    TransformEntry.At(10, _correlationId),
                    TransformEntry.At(20, _jwtForward),
                    TransformEntry.At(30, _catalogRewrite)  // /api/products → /catalog
                },
                ResponseTransformations = new[]
                {
                    TransformEntry.At(10, _removeInternal)
                },
                TransformationTimeout  = TimeSpan.FromSeconds(3),
                FailureMode            = FailureMode.LogAndSkip,
                HasExplicitFailureMode = true
            };
        }

        // ANY /api/admin — fail hard (StopPipeline)
        if (path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase))
        {
            return new TransformationDetail
            {
                RequestTransformations = new[]
                {
                    TransformEntry.At(10, _correlationId),
                    TransformEntry.At(20, _stripAuth),      // remove JWT before forwarding
                    TransformEntry.At(30, _internalKey)     // inject internal service key
                },
                ResponseTransformations = new[]
                {
                    TransformEntry.At(10, _removeInternal)
                },
                FailureMode            = FailureMode.StopPipeline,  // admin: fail hard
                HasExplicitFailureMode = true
            };
        }

        // Default: correlation ID only
        return new TransformationDetail
        {
            RequestTransformations  = new[] { TransformEntry.At(10, _correlationId) },
            ResponseTransformations = Array.Empty<TransformEntry>(),
            HasExplicitFailureMode  = false  // → falls back to PipelineOptions.DefaultFailureMode
        };
    }

    private static string NormalizePath(string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length; i++)
            if (long.TryParse(segs[i], out _) || Guid.TryParse(segs[i], out _))
                segs[i] = "{id}";
        return "/" + string.Join('/', segs);
    }
}
