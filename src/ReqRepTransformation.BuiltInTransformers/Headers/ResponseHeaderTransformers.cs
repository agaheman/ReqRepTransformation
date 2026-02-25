using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.BuiltInTransformers.Headers;

// ─────────────────────────────────────────────────────────────────────────────
// Params schemas
// ─────────────────────────────────────────────────────────────────────────────
// RemoveInternalResponseHeadersTransformer → { "headers": "X-Internal-Token|Server|X-Powered-By" }
//   headers: optional pipe-separated list; defaults to well-known internal headers
// GatewayResponseTagTransformer            → { "version": "1.0", "instanceId": "gw-node-1" }
//   version, instanceId: optional; instanceId defaults to Environment.MachineName
// UploadMetadataHeaderTransformer          → {}
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Removes internal/backend headers from responses before they reach the client.</summary>
public sealed class RemoveInternalResponseHeadersTransformer : IBufferTransformer
{
    private static readonly string[] _defaults =
    {
        "X-Internal-Token", "X-Backend-Version", "X-Upstream-Address",
        "Server", "X-Powered-By", "X-AspNet-Version", "X-AspNetMvc-Version"
    };

    private IReadOnlyList<string> _headers = _defaults;

    public string Name => "remove-internal-response-headers";

    public void Configure(TransformerParams @params)
    {
        var list = @params.GetStringList("headers");
        _headers = list.Count > 0 ? list : _defaults;
    }

    public bool ShouldApply(IMessageContext context)
        => context.Side == Core.Models.MessageSide.Response;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        for (int i = 0; i < _headers.Count; i++)
            context.Headers.Remove(_headers[i]);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Adds X-Gateway-Version and X-Processed-By to the response.</summary>
public sealed class GatewayResponseTagTransformer : IBufferTransformer
{
    private string _version    = "1.0";
    private string _instanceId = Environment.MachineName;

    public string Name => "gateway-response-tag";

    public void Configure(TransformerParams @params)
    {
        _version    = @params.GetString("version")    ?? "1.0";
        _instanceId = @params.GetString("instanceId") ?? Environment.MachineName;
    }

    public bool ShouldApply(IMessageContext context)
        => context.Side == Core.Models.MessageSide.Response;

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Set("X-Gateway-Version", _version);
        context.Headers.Set("X-Processed-By",    _instanceId);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// IStreamTransformer: tags streaming upload requests with metadata headers.
/// Never touches the body — safe for multipart/binary uploads.
/// Params: {} (no params required)
/// </summary>
public sealed class UploadMetadataHeaderTransformer : IStreamTransformer
{
    public string Name => "upload-metadata-header";

    public void Configure(TransformerParams @params) { /* no params */ }

    public bool ShouldApply(IMessageContext context)
        => context.Payload.IsStreaming
        && context.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);

    public ValueTask ApplyAsync(IMessageContext context, CancellationToken ct)
    {
        context.Headers.Set("X-Upload-Gateway",   "reqrep/1.0");
        context.Headers.Set("X-Upload-Timestamp", DateTimeOffset.UtcNow.ToString("O"));
        return ValueTask.CompletedTask;
    }
}
