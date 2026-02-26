using System.Text.Json.Nodes;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.BuiltInTransformers.Body;

// ─────────────────────────────────────────────────────────────────────────────
// Params schemas
// ─────────────────────────────────────────────────────────────────────────────
// JsonFieldAddTransformer       → { "fieldName": "status", "value": "\"active\"", "overwrite": true }
//   value: any valid JSON string (serialised as string in the params JSON)
// JsonFieldRemoveTransformer    → { "fieldName": "_internal" }
// JsonFieldRenameTransformer    → { "fromName": "userId", "toName": "user_id" }
// JsonNestedFieldSetTransformer → { "dotPath": "user.profile.tier", "value": "\"gold\"" }
// JsonGatewayMetadataTransformer→ { "version": "1.0" }   — version is optional
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Adds a field with a static JSON value to the root of a JSON object body.</summary>
public sealed class JsonFieldAddTransformer : IBufferTransformer
{
    private string  _fieldName = string.Empty;
    private string  _valueJson = "null";
    private bool    _overwrite = true;

    public string Name => "json-field-add";

    public void Configure(TransformerParams @params)
    {
        _fieldName = @params.GetRequiredString("fieldName");
        _valueJson = @params.GetRequiredString("value");
        _overwrite = @params.GetBool("overwrite", defaultValue: true);
    }

    public bool ShouldApply(IMessageContext context) => context.Payload.IsJson;

    public async ValueTask ApplyAsync(IBufferMessageContext context, CancellationToken ct)
    {
        var node = await context.Payload.GetJsonAsync(ct).ConfigureAwait(false);
        if (node is not JsonObject obj) return;

        if (_overwrite || !obj.ContainsKey(_fieldName))
            obj[_fieldName] = JsonNode.Parse(_valueJson);
    }
}

/// <summary>Removes a field from the root of a JSON object body.</summary>
public sealed class JsonFieldRemoveTransformer : IBufferTransformer
{
    private string _fieldName = string.Empty;

    public string Name => "json-field-remove";

    public void Configure(TransformerParams @params)
        => _fieldName = @params.GetRequiredString("fieldName");

    public bool ShouldApply(IMessageContext context) => context.Payload.IsJson;

    public async ValueTask ApplyAsync(IBufferMessageContext context, CancellationToken ct)
    {
        var node = await context.Payload.GetJsonAsync(ct).ConfigureAwait(false);
        if (node is JsonObject obj) obj.Remove(_fieldName);
    }
}

/// <summary>Renames a field in the root of a JSON object body (copy + remove).</summary>
public sealed class JsonFieldRenameTransformer : IBufferTransformer
{
    private string _fromName = string.Empty;
    private string _toName   = string.Empty;

    public string Name => "json-field-rename";

    public void Configure(TransformerParams @params)
    {
        _fromName = @params.GetRequiredString("fromName");
        _toName   = @params.GetRequiredString("toName");
    }

    public bool ShouldApply(IMessageContext context) => context.Payload.IsJson;

    public async ValueTask ApplyAsync(IBufferMessageContext context, CancellationToken ct)
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
/// Sets a nested JSON field using a dot-separated path.
/// Creates intermediate JsonObject nodes as needed.
/// </summary>
public sealed class JsonNestedFieldSetTransformer : IBufferTransformer
{
    private string[] _segments  = Array.Empty<string>();
    private string   _valueJson = "null";

    public string Name => "json-nested-field-set";

    public void Configure(TransformerParams @params)
    {
        var dotPath  = @params.GetRequiredString("dotPath");
        _valueJson   = @params.GetRequiredString("value");
        _segments    = dotPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
    }

    public bool ShouldApply(IMessageContext context) => context.Payload.IsJson;

    public async ValueTask ApplyAsync(IBufferMessageContext context, CancellationToken ct)
    {
        var node = await context.Payload.GetJsonAsync(ct).ConfigureAwait(false);
        if (node is not JsonObject root) return;

        var current = root;
        for (int i = 0; i < _segments.Length - 1; i++)
        {
            var seg = _segments[i];
            if (!current.TryGetPropertyValue(seg, out var child) || child is not JsonObject childObj)
            {
                childObj = new JsonObject();
                current[seg] = childObj;
            }
            current = childObj;
        }

        current[_segments[^1]] = JsonNode.Parse(_valueJson);
    }
}

/// <summary>
/// Injects a <c>_gateway</c> metadata object into every JSON request body.
/// Result: { "_gateway": { "version": "1.0", "processedAt": "...", "requestId": "..." } }
///
/// Params: { "version": "1.0" }  — version is optional.
/// </summary>
public sealed class JsonGatewayMetadataTransformer : IBufferTransformer
{
    private string _version = "1.0";

    public string Name => "json-gateway-metadata";

    public void Configure(TransformerParams @params)
        => _version = @params.GetString("version") ?? "1.0";

    public bool ShouldApply(IMessageContext context) => context.Payload.IsJson;

    public async ValueTask ApplyAsync(IBufferMessageContext context, CancellationToken ct)
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
