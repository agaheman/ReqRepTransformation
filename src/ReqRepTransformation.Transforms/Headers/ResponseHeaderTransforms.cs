using System.Text.Json.Nodes;
using ReqRepTransformation.Core.Abstractions;

namespace ReqRepTransformation.Transforms.Headers;

/// <summary>
/// Removes internal/sensitive headers from responses before they reach the client.
/// Typically applied response-side to strip headers like X-Internal-*, Server, X-Powered-By.
/// </summary>
public sealed class RemoveInternalResponseHeadersTransform : IBufferTransform
{
    private static readonly string[] DefaultInternalHeaders =
    {
        "X-Internal-Token",
        "X-Backend-Version",
        "X-Upstream-Address",
        "Server",
        "X-Powered-By",
        "X-AspNet-Version",
        "X-AspNetMvc-Version"
    };

    private readonly IReadOnlyList<string> _headersToRemove;

    public RemoveInternalResponseHeadersTransform(IEnumerable<string>? headersToRemove = null)
    {
        _headersToRemove = headersToRemove?.ToArray() ?? DefaultInternalHeaders;
    }

    public string Name => "remove-internal-response-headers";

    public bool ShouldApply(IMessageContext context)
        => context.Side == Core.Models.MessageSide.Response;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        for (int i = 0; i < _headersToRemove.Count; i++)
            context.Headers.Remove(_headersToRemove[i]);

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Adds gateway identification headers to the response.
/// Helps clients identify which gateway version processed the request.
/// </summary>
public sealed class GatewayResponseTagTransform : IBufferTransform
{
    private readonly string _version;
    private readonly string _instanceId;

    public GatewayResponseTagTransform(string version = "1.0", string? instanceId = null)
    {
        _version = version;
        _instanceId = instanceId ?? Environment.MachineName;
    }

    public string Name => "gateway-response-tag";

    public bool ShouldApply(IMessageContext context)
        => context.Side == Core.Models.MessageSide.Response;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Set("X-Gateway-Version", _version);
        context.Headers.Set("X-Processed-By", _instanceId);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Example IStreamTransform: adds metadata headers to streaming upload requests
/// without ever touching the body (file upload passthrough).
/// </summary>
public sealed class UploadMetadataHeaderTransform : IStreamTransform
{
    public string Name => "upload-metadata-header";

    public bool ShouldApply(IMessageContext context)
        => context.Payload.IsStreaming
        && context.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Set("X-Upload-Gateway", "reqrep/1.0");
        context.Headers.Set("X-Upload-Timestamp", DateTimeOffset.UtcNow.ToString("O"));
        return ValueTask.CompletedTask;
    }
}
