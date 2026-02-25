using System.IdentityModel.Tokens.Jwt;
using ReqRepTransformation.Core.Abstractions;

namespace ReqRepTransformation.Transforms.Auth;

/// <summary>
/// Forwards the Authorization header from the incoming request to the outbound request.
/// Performs no modification to the token — forward as-is.
///
/// Registration: Use as a request-side transform.
/// The Authorization header value is NEVER logged (enforced by the redaction policy).
/// </summary>
public sealed class JwtForwardTransform : IBufferTransform
{
    private const string AuthorizationHeader = "Authorization";

    public string Name => "jwt-forward";

    public bool ShouldApply(IMessageContext context)
        => context.Headers.Contains(AuthorizationHeader);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        // The header is already present — forwarding is implicit for header transforms.
        // This transform exists as an explicit registration point so it appears in
        // OTEL traces and can be circuit-broken independently.
        // In proxy scenarios, the downstream request headers are built fresh from
        // context.Headers, so this transform ensures Authorization is retained.
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Extracts specified JWT claims and injects them as HTTP headers on the downstream request.
///
/// Example: sub claim → X-User-Id header
///
/// Configuration:
/// <code>
/// new JwtClaimsExtractTransform(new Dictionary&lt;string, string&gt;
/// {
///     ["sub"]   = "X-User-Id",
///     ["email"] = "X-User-Email",
///     ["roles"] = "X-User-Roles"
/// })
/// </code>
///
/// IMPORTANT: The token value itself is never logged. Only extracted claim values
/// (after redaction policy inspection) appear in trace attributes.
/// </summary>
public sealed class JwtClaimsExtractTransform : IBufferTransform
{
    private const string AuthorizationHeader = "Authorization";
    private const string BearerPrefix = "Bearer ";

    private readonly IReadOnlyDictionary<string, string> _claimToHeaderMap;
    private static readonly JwtSecurityTokenHandler _tokenHandler = new();

    public JwtClaimsExtractTransform(IReadOnlyDictionary<string, string> claimToHeaderMap)
    {
        ArgumentNullException.ThrowIfNull(claimToHeaderMap);
        _claimToHeaderMap = claimToHeaderMap;
    }

    public string Name => "jwt-claims-extract";

    public bool ShouldApply(IMessageContext context)
        => context.Headers.TryGet(AuthorizationHeader, out var auth)
        && auth is not null
        && auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        if (!context.Headers.TryGet(AuthorizationHeader, out var auth) || auth is null)
            return ValueTask.CompletedTask;

        var token = auth[BearerPrefix.Length..].Trim();

        if (!_tokenHandler.CanReadToken(token))
            return ValueTask.CompletedTask;

        JwtSecurityToken jwt;
        try
        {
            jwt = _tokenHandler.ReadJwtToken(token);
        }
        catch (Exception)
        {
            // Malformed token — do not throw, just skip claim injection
            return ValueTask.CompletedTask;
        }

        foreach (var (claimType, headerName) in _claimToHeaderMap)
        {
            var claim = jwt.Claims.FirstOrDefault(c =>
                c.Type.Equals(claimType, StringComparison.OrdinalIgnoreCase));

            if (claim is not null)
                context.Headers.Set(headerName, claim.Value);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Removes the Authorization header from outbound requests.
/// Use for routes where the downstream service should NOT receive the client JWT.
/// </summary>
public sealed class StripAuthorizationTransform : IBufferTransform
{
    private const string AuthorizationHeader = "Authorization";

    public string Name => "strip-authorization";

    public bool ShouldApply(IMessageContext context)
        => context.Headers.Contains(AuthorizationHeader);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Remove(AuthorizationHeader);
        return ValueTask.CompletedTask;
    }
}
