using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Abstractions;

/// <summary>
/// Central port representing a mutable HTTP message.
/// Transforms operate exclusively on this interface — no ASP.NET or HttpClient
/// types are referenced inside transform implementations.
/// Works in both ASP.NET Core Middleware and HttpClient DelegatingHandler contexts.
/// </summary>
public interface IMessageContext
{
    /// <summary>HTTP method (GET, POST, PUT, PATCH, DELETE, etc.).</summary>
    string Method { get; set; }

    /// <summary>Full request URI including scheme, host, path and query.</summary>
    Uri Address { get; set; }

    /// <summary>Mutable header collection. Applies to request or response headers depending on context.</summary>
    IMessageHeaders Headers { get; }

    /// <summary>Lazy-loaded, zero-double-serialization payload abstraction.</summary>
    IPayload Payload { get; }

    /// <summary>Cancellation token linked to the underlying transport's abort token.</summary>
    CancellationToken Cancellation { get; }

    /// <summary>
    /// Side of the pipeline this context represents.
    /// Transforms can inspect this to behave differently on request vs response.
    /// </summary>
    MessageSide Side { get; }
}
