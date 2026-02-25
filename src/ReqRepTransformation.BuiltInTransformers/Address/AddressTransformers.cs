using System.Text.RegularExpressions;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.BuiltInTransformers.Address;

// ─────────────────────────────────────────────────────────────────────────────
// Params schemas
// ─────────────────────────────────────────────────────────────────────────────
// PathPrefixRewriteTransformer → { "fromPrefix": "/api/v1", "toPrefix": "/internal/v1" }
// PathRegexRewriteTransformer  → { "pattern": "/api/v(\\d+)/(.*)", "replacement": "/v$1/$2" }
// AddQueryParamTransformer     → { "key": "api-version", "value": "2024-01" }
// RemoveQueryParamTransformer  → { "key": "debug" }
// HostRewriteTransformer       → { "host": "catalog.svc", "port": 8080, "scheme": "http" }
//   port and scheme are optional
// MethodOverrideTransformer    → { "newMethod": "PATCH", "onlyIfCurrentMethod": "PUT" }
//   onlyIfCurrentMethod is optional
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Rewrites the request path using a static prefix replacement.</summary>
public sealed class PathPrefixRewriteTransformer : IBufferTransformer
{
    private string _fromPrefix = string.Empty;
    private string _toPrefix   = string.Empty;

    public string Name => "path-prefix-rewrite";

    public void Configure(TransformerParams @params)
    {
        _fromPrefix = @params.GetRequiredString("fromPrefix");
        _toPrefix   = @params.GetRequiredString("toPrefix");
    }

    public bool ShouldApply(IMessageContext context)
        => context.Address.AbsolutePath.StartsWith(_fromPrefix, StringComparison.OrdinalIgnoreCase);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var current = context.Address.AbsolutePath;
        var newPath = _toPrefix + current[_fromPrefix.Length..];
        context.Address = new UriBuilder(context.Address) { Path = newPath }.Uri;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Rewrites the request path using a compiled regular expression.</summary>
public sealed class PathRegexRewriteTransformer : IBufferTransformer
{
    private Regex?  _pattern;
    private string  _replacement = string.Empty;

    public string Name => "path-regex-rewrite";

    public void Configure(TransformerParams @params)
    {
        var pattern = @params.GetRequiredString("pattern");
        _replacement = @params.GetRequiredString("replacement");
        _pattern = new Regex(
            pattern,
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
    }

    public bool ShouldApply(IMessageContext context)
        => _pattern?.IsMatch(context.Address.AbsolutePath) == true;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        if (_pattern is null) return ValueTask.CompletedTask;
        var newPath = _pattern.Replace(context.Address.AbsolutePath, _replacement);
        context.Address = new UriBuilder(context.Address) { Path = newPath }.Uri;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Adds or appends a query string parameter.</summary>
public sealed class AddQueryParamTransformer : IBufferTransformer
{
    private string _key   = string.Empty;
    private string _value = string.Empty;

    public string Name => "add-query-param";

    public void Configure(TransformerParams @params)
    {
        _key   = @params.GetRequiredString("key");
        _value = @params.GetRequiredString("value");
    }

    public bool ShouldApply(IMessageContext context) => true;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var uri      = context.Address;
        var existing = uri.Query.TrimStart('?');
        var newParam = $"{Uri.EscapeDataString(_key)}={Uri.EscapeDataString(_value)}";
        var newQuery = string.IsNullOrEmpty(existing) ? newParam : $"{existing}&{newParam}";
        context.Address = new UriBuilder(uri) { Query = newQuery }.Uri;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Removes a query string parameter by key.</summary>
public sealed class RemoveQueryParamTransformer : IBufferTransformer
{
    private string _key = string.Empty;

    public string Name => "remove-query-param";

    public void Configure(TransformerParams @params)
        => _key = @params.GetRequiredString("key");

    public bool ShouldApply(IMessageContext context)
        => context.Address.Query.Contains(_key, StringComparison.OrdinalIgnoreCase);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var uri     = context.Address;
        var query   = uri.Query.TrimStart('?');
        if (string.IsNullOrEmpty(query)) return ValueTask.CompletedTask;

        var filtered = query.Split('&').Where(p =>
            !p.Split('=')[0].Equals(_key, StringComparison.OrdinalIgnoreCase));

        context.Address = new UriBuilder(uri) { Query = string.Join('&', filtered) }.Uri;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Rewrites the host (and optionally scheme/port) of the request URI.</summary>
public sealed class HostRewriteTransformer : IBufferTransformer
{
    private string  _host   = string.Empty;
    private int?    _port;
    private string? _scheme;

    public string Name => "host-rewrite";

    public void Configure(TransformerParams @params)
    {
        _host   = @params.GetRequiredString("host");
        var port = @params.GetString("port");
        _port   = port is not null && int.TryParse(port, out var p) ? p : null;
        _scheme = @params.GetString("scheme");
    }

    public bool ShouldApply(IMessageContext context) => true;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var ub = new UriBuilder(context.Address) { Host = _host };
        if (_port.HasValue)   ub.Port   = _port.Value;
        if (_scheme is not null) ub.Scheme = _scheme;
        context.Address = ub.Uri;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Changes the HTTP method of the request.</summary>
public sealed class MethodOverrideTransformer : IBufferTransformer
{
    private string  _newMethod           = string.Empty;
    private string? _onlyIfCurrentMethod;

    public string Name => "method-override";

    public void Configure(TransformerParams @params)
    {
        _newMethod           = @params.GetRequiredString("newMethod").ToUpperInvariant();
        _onlyIfCurrentMethod = @params.GetString("onlyIfCurrentMethod")?.ToUpperInvariant();
    }

    public bool ShouldApply(IMessageContext context)
        => _onlyIfCurrentMethod is null
        || context.Method.Equals(_onlyIfCurrentMethod, StringComparison.OrdinalIgnoreCase);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Method = _newMethod;
        return ValueTask.CompletedTask;
    }
}
