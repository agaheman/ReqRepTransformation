using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Core.Pipeline;

namespace ReqRepTransformation.AspNetCore.Adapters;

/// <summary>
/// ASP.NET Core adapter implementing IMessageContext for the response side.
///
/// Response body interception strategy:
/// 1. Before _next(context), the original Response.Body stream is swapped for a
///    RecyclableMemoryStream (done by GatewayTransformMiddleware).
/// 2. After _next returns, the captured body bytes are passed to this context.
/// 3. Response transforms run and may mutate the body via IPayload.
/// 4. After transforms, GatewayTransformMiddleware writes the final bytes back.
/// </summary>
internal sealed class AspNetResponseMessageContext : MessageContextBase
{
    private readonly HttpContext _httpContext;
    private readonly AspNetHeaderAdapter _headers;
    private readonly PayloadContext _payload;
    private Uri _address;

    public AspNetResponseMessageContext(
        HttpContext httpContext,
        ReadOnlyMemory<byte> responseBody,
        string? responseContentType)
        : base(MessageSide.Response, httpContext.RequestAborted)
    {
        _httpContext = httpContext;
        _headers = new AspNetHeaderAdapter(httpContext.Response.Headers);

        // For response context, Address mirrors the original request address
        // (transforms may inspect it but not typically rewrite it)
        _address = new Uri(
            $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.Path}{httpContext.Request.QueryString}");

        _payload = PayloadContext.FromBuffer(responseBody, responseContentType);
    }

    public override string Method
    {
        get => _httpContext.Request.Method;
        set { /* Response side: method is read-only */ }
    }

    public override Uri Address
    {
        get => _address;
        set => _address = value; // Allowed for inspection; does not affect actual response routing
    }

    public override IMessageHeaders Headers => _headers;
    public override IPayload Payload => _payload;

    /// <summary>Returns the flushed response body after transforms have run.</summary>
    public ValueTask<ReadOnlyMemory<byte>> GetFinalBodyAsync(CancellationToken ct)
        => _payload.FlushAsync(ct);
}
