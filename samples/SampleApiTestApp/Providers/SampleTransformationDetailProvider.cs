using Microsoft.Extensions.Caching.Memory;
using ReqRepTransformation.BuiltInTransformers;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace SampleApiTestApp.Providers;

/// <summary>
/// <see cref="ITransformationDetailProvider"/> that simulates loading all route
/// transformer configuration from a database as a flat list of strings.
///
/// Each string in <see cref="_dbRows"/> represents one DB row in the format:
///   "Method|Path|TransformerName|TransformerSide|TransformerOrder|TransformerParamsJson"
///
/// This is exactly what a query like the following would return as raw string columns:
///   SELECT method, path, transformer_name, transformer_side,
///          transformer_order, transformer_params_json
///   FROM   route_transformers
///   WHERE  is_active = true
///
/// <see cref="GetCurrentRouteTransformers"/> parses and filters this list for the
/// incoming request method + path, then returns matching <see cref="RouteTransformerEntry"/>
/// records for <see cref="TransformationDetailBuilder"/> to resolve from keyed DI.
///
/// Matching strategy (most-specific-wins):
///   1. Exact method + longest matching path prefix.
///   2. Wildcard method ("*") + longest matching path prefix.
///
/// In production replace <see cref="_dbRows"/> with an injected repository.
/// </summary>
public sealed class SampleTransformationDetailProvider : ITransformationDetailProvider
{
    // ── Simulated DB result set ───────────────────────────────────────────────
    // Format per row: "Method|Path|TransformerName|TransformerSide|TransformerOrder|ParamsJson"
    // ParamsJson is optional — omit or leave empty to use transformer defaults.
    //
    // In production this list is replaced by a repository call:
    //   IReadOnlyList<string> rows = await _repository.GetAllRouteRowsAsync(ct);
    // ─────────────────────────────────────────────────────────────────────────
    private static readonly IReadOnlyList<string> _dbRows = new[]
    {
        // ── POST /api/orders — request side ──────────────────────────────────
        "POST|/api/orders|correlation-id|Request|10|",
        "POST|/api/orders|request-id|Request|20|",
        "POST|/api/orders|jwt-forward|Request|30|",
        "POST|/api/orders|jwt-claims-extract|Request|40|{\"claimMap\":\"sub=X-User-Id|email=X-User-Email\"}",
        "POST|/api/orders|json-gateway-metadata|Request|50|{\"version\":\"2.0\"}",

        // ── POST /api/orders — response side ─────────────────────────────────
        "POST|/api/orders|remove-internal-response-headers|Response|10|{\"headers\":\"X-Internal-Token|X-Backend-Version|Server|X-Powered-By\"}",
        "POST|/api/orders|gateway-response-tag|Response|20|{\"version\":\"2.0\",\"instanceId\":\"gateway-sample\"}",

        // ── GET /api/products — request side ─────────────────────────────────
        "GET|/api/products|correlation-id|Request|10|",
        "GET|/api/products|jwt-forward|Request|20|",
        "GET|/api/products|path-prefix-rewrite|Request|30|{\"fromPrefix\":\"/api/products\",\"toPrefix\":\"/catalog\"}",

        // ── GET /api/products — response side ────────────────────────────────
        "GET|/api/products|remove-internal-response-headers|Response|10|",

        // ── * /api/admin — request side (wildcard method) ────────────────────
        "*|/api/admin|correlation-id|Request|10|",
        "*|/api/admin|strip-authorization|Request|20|",
        "*|/api/admin|add-header|Request|30|{\"key\":\"X-Internal-Key\",\"value\":\"sample-key-change-in-prod\",\"overwrite\":true}",

        // ── * /api/admin — response side ─────────────────────────────────────
        "*|/api/admin|remove-internal-response-headers|Response|10|",

        // ── * / — catch-all default ───────────────────────────────────────────
        "*|/|correlation-id|Request|10|",
    };

    private readonly TransformationDetailBuilder                 _builder;
    private readonly IMemoryCache                                _cache;
    private readonly ILogger<SampleTransformationDetailProvider> _logger;

    public SampleTransformationDetailProvider(
        TransformationDetailBuilder                 builder,
        IMemoryCache                                cache,
        ILogger<SampleTransformationDetailProvider> logger)
    {
        _builder = builder;
        _cache   = cache;
        _logger  = logger;
    }

    // ── ITransformationDetailProvider ─────────────────────────────────────────

    public ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context, CancellationToken ct = default)
    {
        var cacheKey = $"{context.Method}:{NormalizePath(context.Address.AbsolutePath)}";

        if (_cache.TryGetValue(cacheKey, out TransformationDetail? cached) && cached is not null)
            return ValueTask.FromResult(cached);

        var detail = Resolve(context.Method, context.Address.AbsolutePath);

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

    // ── Resolution ────────────────────────────────────────────────────────────

    private TransformationDetail Resolve(string method, string path)
    {
        var entries = GetCurrentRouteTransformers(method, path);

        if (entries.Count == 0)
            return TransformationDetail.Empty;

        return _builder.Build(
            entries,
            timeout:     TimeSpan.FromSeconds(3),
            failureMode: FailureMode.LogAndSkip);
    }

    /// <summary>
    /// Parses <see cref="_dbRows"/> (flat list of pipe-delimited strings) and
    /// returns the <see cref="RouteTransformerEntry"/> records that match the
    /// incoming <paramref name="method"/> and <paramref name="path"/>.
    ///
    /// Row format: "Method|Path|TransformerName|TransformerSide|TransformerOrder|ParamsJson"
    ///
    /// Matching: exact method + longest path prefix wins;
    ///           wildcard method ("*") is fallback.
    /// </summary>
    private static IReadOnlyList<RouteTransformerEntry> GetCurrentRouteTransformers(
        string method, string path)
    {
        // ── Step 1: parse all rows ────────────────────────────────────────────
        var parsed = ParseRows(_dbRows);

        // ── Step 2: find the best (most-specific) matching path prefix ────────
        //           Exact method takes priority over wildcard.
        var matchedPath =
            FindLongestPath(parsed, method, path, exactMethod: true)  ??
            FindLongestPath(parsed, method, path, exactMethod: false);

        if (matchedPath is null)
            return Array.Empty<RouteTransformerEntry>();

        // ── Step 3: filter rows to the winning (method, path) and project ─────
        return parsed
            .Where(r => RowMatches(r, method, matchedPath))
            .Select(r => RouteTransformerEntry.Create(
                transformerKey: r.TransformerName,
                side:           ParseSide(r.TransformerSide),
                order:          r.TransformerOrder,
                paramsJson:     string.IsNullOrEmpty(r.ParamsJson) ? null : r.ParamsJson))
            .ToArray();
    }

    // ── Row parsing ───────────────────────────────────────────────────────────

    // Parsed representation of one "Method|Path|Name|Side|Order|ParamsJson" string.
    private readonly record struct DbRow(
        string Method,
        string Path,
        string TransformerName,
        string TransformerSide,
        int    TransformerOrder,
        string ParamsJson);

    /// <summary>
    /// Parses each raw string into a <see cref="DbRow"/>.
    /// Invalid rows are silently skipped so a single bad entry never crashes the pipeline.
    /// Format: "Method|Path|TransformerName|TransformerSide|TransformerOrder|ParamsJson"
    ///   - Columns 0-4: required.
    ///   - Column 5 (ParamsJson): optional — absent or empty → no params.
    ///   - ParamsJson may itself contain '|' (valid inside JSON strings) because
    ///     we split on the FIRST 5 delimiters only (limit = 6 parts).
    /// </summary>
    private static IReadOnlyList<DbRow> ParseRows(IReadOnlyList<string> rows)
    {
        var result = new List<DbRow>(rows.Count);

        foreach (var raw in rows)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // Split into at most 6 parts so ParamsJson (col 5) can contain '|'
            var parts = raw.Split('|', count: 6);

            if (parts.Length < 5) continue;                    // malformed — skip
            if (!int.TryParse(parts[4], out var order)) continue;

            result.Add(new DbRow(
                Method:          parts[0].Trim(),
                Path:            parts[1].Trim(),
                TransformerName: parts[2].Trim(),
                TransformerSide: parts[3].Trim(),
                TransformerOrder: order,
                ParamsJson:      parts.Length > 5 ? parts[5].Trim() : string.Empty));
        }

        return result;
    }

    // ── Matching helpers ──────────────────────────────────────────────────────

    private static string? FindLongestPath(
        IReadOnlyList<DbRow> rows, string method, string requestPath, bool exactMethod)
    {
        string? best    = null;
        int     bestLen = -1;

        foreach (var r in rows)
        {
            bool methodOk = exactMethod
                ? r.Method.Equals(method, StringComparison.OrdinalIgnoreCase)
                : r.Method == "*";

            if (!methodOk) continue;
            if (!requestPath.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase)) continue;

            if (r.Path.Length > bestLen)
            {
                bestLen = r.Path.Length;
                best    = r.Path;
            }
        }

        return best;
    }

    private static bool RowMatches(DbRow r, string method, string matchedPath)
        => (r.Method.Equals(method, StringComparison.OrdinalIgnoreCase) || r.Method == "*")
        && r.Path.Equals(matchedPath, StringComparison.OrdinalIgnoreCase);

    private static TransformerSide ParseSide(string value)
        => value.Equals("Response", StringComparison.OrdinalIgnoreCase)
            ? TransformerSide.Response
            : TransformerSide.Request;

    // ── Cache-key normalisation ───────────────────────────────────────────────

    private static string NormalizePath(string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length; i++)
            if (long.TryParse(segs[i], out _) || Guid.TryParse(segs[i], out _))
                segs[i] = "{id}";
        return "/" + string.Join('/', segs);
    }
}
