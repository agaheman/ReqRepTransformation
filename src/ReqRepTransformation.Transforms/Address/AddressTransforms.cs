using System.Text.RegularExpressions;
using ReqRepTransformation.Core.Abstractions;

namespace ReqRepTransformation.Transforms.Address;

/// <summary>
/// Rewrites the request path using a static prefix replacement.
/// Example: /api/v1/users → /internal/users
/// </summary>
public sealed class PathPrefixRewriteTransform : IBufferTransform
{
    private readonly string _fromPrefix;
    private readonly string _toPrefix;

    public PathPrefixRewriteTransform(string fromPrefix, string toPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromPrefix);
        ArgumentNullException.ThrowIfNull(toPrefix);
        _fromPrefix = fromPrefix;
        _toPrefix = toPrefix;
    }

    public string Name => $"path-prefix-rewrite:{_fromPrefix}";

    public bool ShouldApply(IMessageContext context)
        => context.Address.AbsolutePath.StartsWith(_fromPrefix, StringComparison.OrdinalIgnoreCase);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var currentPath = context.Address.AbsolutePath;
        var newPath = _toPrefix + currentPath[_fromPrefix.Length..];

        context.Address = new UriBuilder(context.Address)
        {
            Path = newPath
        }.Uri;

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Rewrites the request path using a regular expression substitution.
/// Example: pattern="/api/v(\d+)/(.+)", replacement="/v$1/$2"
/// </summary>
public sealed class PathRegexRewriteTransform : IBufferTransform
{
    private readonly Regex _pattern;
    private readonly string _replacement;

    public PathRegexRewriteTransform(string pattern, string replacement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(replacement);
        _pattern = new Regex(pattern,
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
        _replacement = replacement;
    }

    public string Name => "path-regex-rewrite";

    public bool ShouldApply(IMessageContext context)
        => _pattern.IsMatch(context.Address.AbsolutePath);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var newPath = _pattern.Replace(context.Address.AbsolutePath, _replacement);

        context.Address = new UriBuilder(context.Address)
        {
            Path = newPath
        }.Uri;

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Adds or overwrites a query string parameter.
/// </summary>
public sealed class AddQueryParamTransform : IBufferTransform
{
    private readonly string _key;
    private readonly string _value;

    public AddQueryParamTransform(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _key = key;
        _value = value;
    }

    public string Name => $"add-query:{_key}";

    public bool ShouldApply(IMessageContext context) => true;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var uri = context.Address;
        var existingQuery = uri.Query.TrimStart('?');
        var newParam = $"{Uri.EscapeDataString(_key)}={Uri.EscapeDataString(_value)}";

        var newQuery = string.IsNullOrEmpty(existingQuery)
            ? newParam
            : $"{existingQuery}&{newParam}";

        context.Address = new UriBuilder(uri) { Query = newQuery }.Uri;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Removes a query string parameter by key.
/// </summary>
public sealed class RemoveQueryParamTransform : IBufferTransform
{
    private readonly string _key;

    public RemoveQueryParamTransform(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
    }

    public string Name => $"remove-query:{_key}";

    public bool ShouldApply(IMessageContext context)
        => context.Address.Query.Contains(_key, StringComparison.OrdinalIgnoreCase);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var uri = context.Address;
        var query = uri.Query.TrimStart('?');

        if (string.IsNullOrEmpty(query))
            return ValueTask.CompletedTask;

        // Parse and rebuild without the target key
        var parts = query.Split('&');
        var filtered = parts.Where(p =>
        {
            var keyPart = p.Split('=')[0];
            return !keyPart.Equals(_key, StringComparison.OrdinalIgnoreCase);
        });

        var newQuery = string.Join('&', filtered);
        context.Address = new UriBuilder(uri) { Query = newQuery }.Uri;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Rewrites the host (and optionally scheme/port) of the request URI.
/// Useful for routing to different backend clusters.
/// </summary>
public sealed class HostRewriteTransform : IBufferTransform
{
    private readonly string _host;
    private readonly int? _port;
    private readonly string? _scheme;

    public HostRewriteTransform(string host, int? port = null, string? scheme = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _host = host;
        _port = port;
        _scheme = scheme;
    }

    public string Name => $"host-rewrite:{_host}";

    public bool ShouldApply(IMessageContext context) => true;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var ub = new UriBuilder(context.Address)
        {
            Host = _host
        };
        if (_port.HasValue) ub.Port = _port.Value;
        if (_scheme is not null) ub.Scheme = _scheme;

        context.Address = ub.Uri;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Changes the HTTP method of the request.
/// </summary>
public sealed class MethodOverrideTransform : IBufferTransform
{
    private readonly string _newMethod;
    private readonly string? _onlyIfCurrentMethod;

    public MethodOverrideTransform(string newMethod, string? onlyIfCurrentMethod = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newMethod);
        _newMethod = newMethod.ToUpperInvariant();
        _onlyIfCurrentMethod = onlyIfCurrentMethod?.ToUpperInvariant();
    }

    public string Name => $"method-override:{_newMethod}";

    public bool ShouldApply(IMessageContext context)
        => _onlyIfCurrentMethod is null
        || context.Method.Equals(_onlyIfCurrentMethod, StringComparison.OrdinalIgnoreCase);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Method = _newMethod;
        return ValueTask.CompletedTask;
    }
}
