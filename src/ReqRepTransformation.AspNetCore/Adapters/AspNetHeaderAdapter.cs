using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ReqRepTransformation.Core.Abstractions;

namespace ReqRepTransformation.AspNetCore.Adapters;

/// <summary>
/// Adapts ASP.NET Core IHeaderDictionary to IMessageHeaders.
/// Supports both request and response header dictionaries.
/// No allocations for single-value headers in the hot path.
/// </summary>
internal sealed class AspNetHeaderAdapter : IMessageHeaders
{
    private readonly IHeaderDictionary _headers;

    public AspNetHeaderAdapter(IHeaderDictionary headers)
    {
        _headers = headers;
    }

    public IEnumerable<string> Keys => _headers.Keys;

    public string? Get(string key)
        => _headers.TryGetValue(key, out var values)
            ? values.ToString()
            : null;

    public IEnumerable<string> GetValues(string key)
    {
        if (!_headers.TryGetValue(key, out var values))
            return Enumerable.Empty<string>();

        // StringValues.ToArray() returns string?[] — filter nulls explicitly
        var result = new List<string>(values.Count);
        foreach (var v in values)
        {
            if (v is not null)
                result.Add(v);
        }
        return result;
    }

    public void Set(string key, string value)
        => _headers[key] = new StringValues(value);

    public void Append(string key, string value)
    {
        if (_headers.TryGetValue(key, out var existing))
            _headers[key] = StringValues.Concat(existing, value);
        else
            _headers[key] = new StringValues(value);
    }

    public void Remove(string key)
        => _headers.Remove(key);

    public bool Contains(string key)
        => _headers.ContainsKey(key);

    public bool TryGet(string key, out string? value)
    {
        if (_headers.TryGetValue(key, out var values))
        {
            value = values.ToString();
            return true;
        }
        value = null;
        return false;
    }
}
