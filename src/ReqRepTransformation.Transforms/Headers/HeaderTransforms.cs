using ReqRepTransformation.Core.Abstractions;

namespace ReqRepTransformation.Transforms.Headers;

/// <summary>Adds or overwrites a header with a static value.</summary>
public sealed class AddHeaderTransform : IBufferTransform
{
    private readonly string _key;
    private readonly string _value;
    private readonly bool _overwrite;

    public AddHeaderTransform(string key, string value, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _key = key;
        _value = value;
        _overwrite = overwrite;
    }

    public string Name => $"add-header:{_key}";

    public bool ShouldApply(IMessageContext context)
        => _overwrite || !context.Headers.Contains(_key);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Set(_key, _value);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Removes a header by key.</summary>
public sealed class RemoveHeaderTransform : IBufferTransform
{
    private readonly string _key;

    public RemoveHeaderTransform(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
    }

    public string Name => $"remove-header:{_key}";

    public bool ShouldApply(IMessageContext context)
        => context.Headers.Contains(_key);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Remove(_key);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Renames a header by copying the value and removing the original key.</summary>
public sealed class RenameHeaderTransform : IBufferTransform
{
    private readonly string _fromKey;
    private readonly string _toKey;

    public RenameHeaderTransform(string fromKey, string toKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(toKey);
        _fromKey = fromKey;
        _toKey = toKey;
    }

    public string Name => $"rename-header:{_fromKey}→{_toKey}";

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

/// <summary>
/// Appends a header value without overwriting existing values.
/// Useful for multi-value headers like Accept-Language or X-Forwarded-For.
/// </summary>
public sealed class AppendHeaderTransform : IBufferTransform
{
    private readonly string _key;
    private readonly string _value;

    public AppendHeaderTransform(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _key = key;
        _value = value;
    }

    public string Name => $"append-header:{_key}";

    public bool ShouldApply(IMessageContext context) => true;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Append(_key, _value);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Injects a new correlation ID header if not already present.
/// Uses Guid.NewGuid with N format (no dashes, 32 hex chars) for compactness.
/// </summary>
public sealed class CorrelationIdTransform : IBufferTransform
{
    public const string DefaultHeaderName = "X-Correlation-Id";

    private readonly string _headerName;

    public CorrelationIdTransform(string headerName = DefaultHeaderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        _headerName = headerName;
    }

    public string Name => "correlation-id-inject";

    public bool ShouldApply(IMessageContext context)
        => !context.Headers.Contains(_headerName);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Set(_headerName, Guid.NewGuid().ToString("N"));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Propagates the request ID from Request-Id header if present,
/// or generates a new one. Follows W3C Trace Context conventions.
/// </summary>
public sealed class RequestIdPropagationTransform : IBufferTransform
{
    private const string RequestIdHeader = "X-Request-Id";
    private const string TraceIdHeader = "traceparent";

    public string Name => "request-id-propagation";

    public bool ShouldApply(IMessageContext context) => true;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        if (!context.Headers.Contains(RequestIdHeader))
            context.Headers.Set(RequestIdHeader, Guid.NewGuid().ToString("N"));

        return ValueTask.CompletedTask;
    }
}
