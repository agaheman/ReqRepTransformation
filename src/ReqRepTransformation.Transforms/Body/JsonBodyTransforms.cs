using System.Text.Json.Nodes;
using ReqRepTransformation.Core.Abstractions;

namespace ReqRepTransformation.Transforms.Body;

/// <summary>
/// Adds a field with a static value to the root of a JSON object body.
/// If the field already exists, behaviour is controlled by the overwrite flag.
/// </summary>
public sealed class JsonFieldAddTransform : IBufferTransform
{
    private readonly string _fieldName;
    private readonly JsonNode _value;
    private readonly bool _overwrite;

    public JsonFieldAddTransform(string fieldName, JsonNode value, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(value);
        _fieldName = fieldName;
        _value = value;
        _overwrite = overwrite;
    }

    public string Name => $"json-field-add:{_fieldName}";

    public bool ShouldApply(IMessageContext context)
        => context.Payload.IsJson;

    public async ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var node = await context.Payload.GetJsonAsync(ct).ConfigureAwait(false);

        if (node is not JsonObject obj) return;

        if (_overwrite || !obj.ContainsKey(_fieldName))
        {
            // JsonNode values cannot be shared between documents — clone via re-parse
            obj[_fieldName] = _value.ToJsonString() is { } json
                ? JsonNode.Parse(json)
                : null;
        }
    }
}

/// <summary>
/// Removes a field from the root of a JSON object body.
/// </summary>
public sealed class JsonFieldRemoveTransform : IBufferTransform
{
    private readonly string _fieldName;

    public JsonFieldRemoveTransform(string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        _fieldName = fieldName;
    }

    public string Name => $"json-field-remove:{_fieldName}";

    public bool ShouldApply(IMessageContext context) => context.Payload.IsJson;

    public async ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var node = await context.Payload.GetJsonAsync(ct).ConfigureAwait(false);
        if (node is JsonObject obj)
            obj.Remove(_fieldName);
    }
}

/// <summary>
/// Renames a field in the root of a JSON object body (copy + remove).
/// </summary>
public sealed class JsonFieldRenameTransform : IBufferTransform
{
    private readonly string _fromName;
    private readonly string _toName;

    public JsonFieldRenameTransform(string fromName, string toName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromName);
        ArgumentException.ThrowIfNullOrWhiteSpace(toName);
        _fromName = fromName;
        _toName = toName;
    }

    public string Name => $"json-field-rename:{_fromName}→{_toName}";

    public bool ShouldApply(IMessageContext context) => context.Payload.IsJson;

    public async ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var node = await context.Payload.GetJsonAsync(ct).ConfigureAwait(false);
        if (node is not JsonObject obj) return;

        if (obj.TryGetPropertyValue(_fromName, out var value))
        {
            obj.Remove(_fromName);
            obj[_toName] = value;
        }
    }
}

/// <summary>
/// Injects a gateway metadata object into every JSON request body.
/// Adds: { "_gateway": { "version": "1.0", "processedAt": "...", "requestId": "..." } }
/// </summary>
public sealed class JsonGatewayMetadataTransform : IBufferTransform
{
    private readonly string _version;

    public JsonGatewayMetadataTransform(string version = "1.0")
    {
        _version = version;
    }

    public string Name => "json-gateway-metadata";

    public bool ShouldApply(IMessageContext context) => context.Payload.IsJson;

    public async ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var node = await context.Payload.GetJsonAsync(ct).ConfigureAwait(false);
        if (node is not JsonObject obj) return;

        obj["_gateway"] = new JsonObject
        {
            ["version"]     = JsonValue.Create(_version),
            ["processedAt"] = JsonValue.Create(DateTimeOffset.UtcNow.ToString("O")),
            ["requestId"]   = JsonValue.Create(Guid.NewGuid().ToString("N"))
        };
    }
}

/// <summary>
/// Sets a nested JSON field by dot-separated path.
/// Example: path = "user.profile.tier", value = "premium"
/// Creates intermediate objects if they don't exist.
/// </summary>
public sealed class JsonNestedFieldSetTransform : IBufferTransform
{
    private readonly string[] _pathSegments;
    private readonly JsonNode _value;
    private readonly string _fullPath;

    public JsonNestedFieldSetTransform(string dotPath, JsonNode value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotPath);
        ArgumentNullException.ThrowIfNull(value);
        _pathSegments = dotPath.Split('.');
        _value = value;
        _fullPath = dotPath;
    }

    public string Name => $"json-nested-set:{_fullPath}";

    public bool ShouldApply(IMessageContext context) => context.Payload.IsJson;

    public async ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        var node = await context.Payload.GetJsonAsync(ct).ConfigureAwait(false);
        if (node is not JsonObject root) return;

        var current = root;
        for (int i = 0; i < _pathSegments.Length - 1; i++)
        {
            var segment = _pathSegments[i];
            if (!current.TryGetPropertyValue(segment, out var child) || child is not JsonObject childObj)
            {
                childObj = new JsonObject();
                current[segment] = childObj;
            }
            current = childObj;
        }

        var lastKey = _pathSegments[^1];
        current[lastKey] = JsonNode.Parse(_value.ToJsonString()!);
    }
}
