using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Core.Pipeline;

namespace ReqRepTransformation.AspNetCore.Adapters;

/// <summary>
/// ASP.NET Core adapter implementing IMessageContext for the request side.
/// Wraps HttpContext.Request, adapting headers, URI, method and body.
///
/// Body is read via Request.BodyReader (PipeReader) — no StreamReader, no string conversion.
/// </summary>
internal sealed class AspNetRequestMessageContext : MessageContextBase
{
    private readonly HttpContext _httpContext;
    private readonly AspNetHeaderAdapter _headers;
    private readonly PayloadContext _payload;
    private Uri _address;

    public AspNetRequestMessageContext(HttpContext httpContext)
        : base(MessageSide.Request, httpContext.RequestAborted)
    {
        _httpContext = httpContext;

        _headers = new AspNetHeaderAdapter(httpContext.Request.Headers);

        _address = BuildUri(httpContext.Request);

        var contentType = httpContext.Request.ContentType;
        var hasBody = httpContext.Request.ContentLength > 0
                   || (httpContext.Request.ContentLength is null
                       && !string.IsNullOrEmpty(contentType));

        _payload = new PayloadContext(
            hasBody ? httpContext.Request.BodyReader : null,
            contentType,
            hasBody);
    }

    public override string Method
    {
        get => _httpContext.Request.Method;
        set => _httpContext.Request.Method = value;
    }

    public override Uri Address
    {
        get => _address;
        set
        {
            _address = value;
            ApplyAddressToRequest(_httpContext.Request, value);
        }
    }

    public override IMessageHeaders Headers => _headers;
    public override IPayload Payload => _payload;

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static Uri BuildUri(HttpRequest request)
    {
        var ub = new UriBuilder
        {
            Scheme = request.Scheme,
            Host   = request.Host.Host,
            Port   = request.Host.Port ?? (request.Scheme == "https" ? 443 : 80),
            Path   = request.Path.ToString(),
            Query  = request.QueryString.ToString().TrimStart('?')
        };
        return ub.Uri;
    }

    private static void ApplyAddressToRequest(HttpRequest request, Uri uri)
    {
        request.Scheme = uri.Scheme;
        request.Host   = new HostString(uri.Host, uri.Port);
        request.Path   = new PathString(uri.AbsolutePath);
        request.QueryString = new QueryString(
            string.IsNullOrEmpty(uri.Query) ? string.Empty : uri.Query);
    }
}
