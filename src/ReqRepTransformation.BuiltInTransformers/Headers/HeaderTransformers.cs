using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.BuiltInTransformers.Headers;

// ─────────────────────────────────────────────────────────────────────────────
// Params schemas (for documentation and serialisation)
// ─────────────────────────────────────────────────────────────────────────────
// AddHeaderTransformer     → { "key": "X-Foo", "value": "bar", "overwrite": true }
// RemoveHeaderTransformer  → { "key": "X-Foo" }
// RenameHeaderTransformer  → { "fromKey": "X-Old", "toKey": "X-New" }
// AppendHeaderTransformer  → { "key": "X-Foo", "value": "bar" }
// CorrelationIdTransformer → { "headerName": "X-Correlation-Id" }      (optional)
// RequestIdTransformer     → {}
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Adds or overwrites a header with a static value.</summary>
public sealed class AddHeaderTransformer : IBufferTransformer
{
    private string _key    = string.Empty;
    private string _value  = string.Empty;
    private bool   _overwrite = true;

    public string Name => "add-header";

    public void Configure(TransformerParams @params)
    {
        _key      = @params.GetRequiredString("key");
        _value    = @params.GetRequiredString("value");
        _overwrite = @params.GetBool("overwrite", defaultValue: true);
    }

    public bool ShouldApply(IMessageContext context)
        => _overwrite || !context.Headers.Contains(_key);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Set(_key, _value);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Removes a header by key.</summary>
public sealed class RemoveHeaderTransformer : IBufferTransformer
{
    private string _key = string.Empty;

    public string Name => "remove-header";

    public void Configure(TransformerParams @params)
        => _key = @params.GetRequiredString("key");

    public bool ShouldApply(IMessageContext context)
        => context.Headers.Contains(_key);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Remove(_key);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Renames a header by copying its value to a new key and removing the original.</summary>
public sealed class RenameHeaderTransformer : IBufferTransformer
{
    private string _fromKey = string.Empty;
    private string _toKey   = string.Empty;

    public string Name => "rename-header";

    public void Configure(TransformerParams @params)
    {
        _fromKey = @params.GetRequiredString("fromKey");
        _toKey   = @params.GetRequiredString("toKey");
    }

    public bool ShouldApply(IMessageContext context)
        => context.Headers.Contains(_fromKey);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        if (context.Headers.TryGet(_fromKey, out var value) && value is not null)
        {
            context.Headers.Set(_toKey, value);
            context.Headers.Remove(_fromKey);
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Appends a value to a header (multi-value safe).</summary>
public sealed class AppendHeaderTransformer : IBufferTransformer
{
    private string _key   = string.Empty;
    private string _value = string.Empty;

    public string Name => "append-header";

    public void Configure(TransformerParams @params)
    {
        _key   = @params.GetRequiredString("key");
        _value = @params.GetRequiredString("value");
    }

    public bool ShouldApply(IMessageContext context) => true;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Append(_key, _value);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Injects a correlation ID header if not already present.
/// Uses Guid.NewGuid "N" format (32 hex chars, no dashes).
///
/// Params: { "headerName": "X-Correlation-Id" }  — headerName is optional.
/// </summary>
public sealed class CorrelationIdTransformer : IBufferTransformer
{
    public const string DefaultHeaderName = "X-Correlation-Id";
    private string _headerName = DefaultHeaderName;

    public string Name => "correlation-id";

    public void Configure(TransformerParams @params)
        => _headerName = @params.GetString("headerName") ?? DefaultHeaderName;

    public bool ShouldApply(IMessageContext context)
        => !context.Headers.Contains(_headerName);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Set(_headerName, Guid.NewGuid().ToString("N"));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Injects X-Request-Id if absent. Params: {} (no params required).</summary>
public sealed class RequestIdTransformer : IBufferTransformer
{
    private const string RequestIdHeader = "X-Request-Id";

    public string Name => "request-id";

    public void Configure(TransformerParams @params) { /* no params */ }

    public bool ShouldApply(IMessageContext context) => true;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        if (!context.Headers.Contains(RequestIdHeader))
            context.Headers.Set(RequestIdHeader, Guid.NewGuid().ToString("N"));
        return ValueTask.CompletedTask;
    }
}
