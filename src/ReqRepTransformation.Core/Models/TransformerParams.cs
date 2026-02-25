using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReqRepTransformation.Core.Models;

/// <summary>
/// Carries the configuration parameters for a single transformer instance.
/// Loaded from a database column (JSONB / TEXT) and passed to the transformer's
/// constructor or factory method.
///
/// Design contract:
///   - Each transformer declares which keys it reads from <see />.
///   - The JSON is parsed lazily on first access and cached.
///   - All transformers receive this object; they call Get&lt;T&gt;() or GetRequired&lt;T&gt;()
///     to read their typed parameters.
///   - Null or empty JSON is treated as empty params (not an error) — transformers
///     declare which parameters are required vs optional.
/// </summary>
public sealed class TransformerParams
{
    public static readonly TransformerParams Empty = new(null);

    private readonly JsonObject? _json;

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TransformerParams(string? paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
        {
            _json = null;
            return;
        }

        try
        {
            _json = JsonNode.Parse(paramsJson) as JsonObject;
        }
        catch (JsonException)
        {
            _json = null;
        }
    }

    public TransformerParams(JsonObject? json)
    {
        _json = json;
    }

    /// <summary>Raw JSON string for serialisation / logging.</summary>
    public string? RawJson => _json?.ToJsonString();

    /// <summary>
    /// Returns the string value for <paramref name="key"/>, or null if absent.
    /// </summary>
    public string? GetString(string key)
    {
        if (_json is null) return null;
        return _json.TryGetPropertyValue(key, out var node) ? node?.GetValue<string>() : null;
    }

    /// <summary>
    /// Returns the string value or throws <see cref="TransformerParamsMissingException"/>
    /// if the key is absent or the value is null/whitespace.
    /// </summary>
    public string GetRequiredString(string key)
        => GetString(key) is { Length: > 0 } v
            ? v
            : throw new TransformerParamsMissingException(key);

    /// <summary>Returns the bool value, or <paramref name="defaultValue"/> if absent.</summary>
    public bool GetBool(string key, bool defaultValue = false)
    {
        if (_json is null) return defaultValue;
        if (!_json.TryGetPropertyValue(key, out var node) || node is null) return defaultValue;

        // Stored as JSON bool or as string "true"/"false"
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out var b)) return b;
            if (jv.TryGetValue<string>(out var s)) return s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return defaultValue;
    }

    /// <summary>Returns the int value, or <paramref name="defaultValue"/> if absent.</summary>
    public int GetInt(string key, int defaultValue = 0)
    {
        if (_json is null) return defaultValue;
        if (!_json.TryGetPropertyValue(key, out var node) || node is null) return defaultValue;
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var i)) return i;
            if (jv.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        }
        return defaultValue;
    }

    /// <summary>
    /// Deserialises the entire params JSON into <typeparamref name="T"/>.
    /// Returns <c>default</c> if params are empty.
    /// </summary>
    public T? Deserialize<T>()
    {
        if (_json is null) return default;
        return _json.Deserialize<T>(_options);
    }

    /// <summary>
    /// Returns all string values for <paramref name="key"/> split by
    /// <paramref name="separator"/>. Useful for pipe-delimited lists stored as a string.
    /// </summary>
    public IReadOnlyList<string> GetStringList(string key, char separator = '|')
    {
        var raw = GetString(key);
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Returns a key→value dictionary from a pipe-separated "a=b|c=d" string stored
    /// at <paramref name="key"/>. Useful for claim maps.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetPairMap(string key, char pairSep = '|', char kvSep = '=')
    {
        var list = GetStringList(key, pairSep);
        var map  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            var idx = item.IndexOf(kvSep);
            if (idx > 0)
                map[item[..idx].Trim()] = item[(idx + 1)..].Trim();
        }
        return map;
    }

    public override string ToString() => RawJson ?? "{}";
}

/// <summary>
/// Thrown when a transformer requires a parameter that is absent from <see cref="TransformerParams"/>.
/// </summary>
public sealed class TransformerParamsMissingException : InvalidOperationException
{
    public string ParamKey { get; }

    public TransformerParamsMissingException(string paramKey)
        : base($"Required transformer parameter '{paramKey}' is missing from params JSON.")
    {
        ParamKey = paramKey;
    }
}
