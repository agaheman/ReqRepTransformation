using System.IdentityModel.Tokens.Jwt;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.BuiltInTransformers.Auth;

// ─────────────────────────────────────────────────────────────────────────────
// Params schemas
// ─────────────────────────────────────────────────────────────────────────────
// JwtForwardTransformer        → {}  (no params — just marks the forward explicitly)
// JwtClaimsExtractTransformer  → { "claimMap": "sub=X-User-Id|email=X-User-Email|roles=X-User-Roles" }
//   claimMap: pipe-separated "claimType=HeaderName" pairs
// StripAuthorizationTransformer→ {}  (no params)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Forwards the Authorization header as-is to the downstream request.
/// Exists as an explicit registration so it appears in OTEL traces and
/// can be circuit-broken independently.
/// Params: {} (no configuration needed)
/// </summary>
public sealed class JwtForwardTransformer : IBufferTransformer
{
    private const string AuthorizationHeader = "Authorization";

    public string Name => "jwt-forward";

    public void Configure(TransformerParams @params) { /* no params */ }

    public bool ShouldApply(IMessageContext context)
        => context.Headers.Contains(AuthorizationHeader);

    public ValueTask ApplyAsync(IBufferMessageContext context, CancellationToken ct)
        => ValueTask.CompletedTask; // header is already in context — forwarding is implicit
}

/// <summary>
/// Extracts specified JWT claims and injects them as HTTP request headers.
///
/// Params: { "claimMap": "sub=X-User-Id|email=X-User-Email|roles=X-User-Roles" }
///   claimMap: pipe-separated "claimType=TargetHeaderName" pairs.
/// </summary>
public sealed class JwtClaimsExtractTransformer : IBufferTransformer
{
    private const string AuthorizationHeader = "Authorization";
    private const string BearerPrefix        = "Bearer ";

    private IReadOnlyDictionary<string, string> _claimMap
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly JwtSecurityTokenHandler _handler = new();

    public string Name => "jwt-claims-extract";

    public void Configure(TransformerParams @params)
        => _claimMap = @params.GetPairMap("claimMap");

    public bool ShouldApply(IMessageContext context)
        => context.Headers.TryGet(AuthorizationHeader, out var auth)
        && auth is not null
        && auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase);

    public ValueTask ApplyAsync(IBufferMessageContext context, CancellationToken ct)
    {
        if (!context.Headers.TryGet(AuthorizationHeader, out var auth) || auth is null)
            return ValueTask.CompletedTask;

        var token = auth[BearerPrefix.Length..].Trim();
        if (!_handler.CanReadToken(token)) return ValueTask.CompletedTask;

        JwtSecurityToken jwt;
        try   { jwt = _handler.ReadJwtToken(token); }
        catch { return ValueTask.CompletedTask; }

        foreach (var (claimType, headerName) in _claimMap)
        {
            var claim = jwt.Claims.FirstOrDefault(
                c => c.Type.Equals(claimType, StringComparison.OrdinalIgnoreCase));
            if (claim is not null)
                context.Headers.Set(headerName, claim.Value);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Removes the Authorization header before forwarding to downstream.
/// Use for routes where the downstream service must NOT receive the client JWT.
/// Params: {} (no params)
/// </summary>
public sealed class StripAuthorizationTransformer : IBufferTransformer
{
    private const string AuthorizationHeader = "Authorization";

    public string Name => "strip-authorization";

    public void Configure(TransformerParams @params) { /* no params */ }

    public bool ShouldApply(IMessageContext context)
        => context.Headers.Contains(AuthorizationHeader);

    public ValueTask ApplyAsync(IBufferMessageContext context, CancellationToken ct)
    {
        context.Headers.Remove(AuthorizationHeader);
        return ValueTask.CompletedTask;
    }
}
