using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Abstractions;

/// <summary>
/// Central port representing a mutable HTTP message.
/// Transforms operate exclusively on this interface — no ASP.NET or HttpClient
/// types are referenced inside transformer implementations.
///
/// Payload access is intentionally absent here. Typed payload access is available
/// through the covariant sub-interfaces:
///   <see cref="IBufferMessageContext.Payload"/> → <see cref="IBufferPayload"/>  (for IBufferTransformer)
///   <see cref="IStreamMessageContext.Payload"/> → <see cref="IStreamPayload"/>  (for IStreamTransformer)
///
/// The pipeline resolves the correct typed view before calling ApplyAsync, so
/// the wrong payload method (e.g. GetJsonAsync inside an IStreamTransformer)
/// is a compile-time error, not a runtime surprise.
/// </summary>
public interface IMessageContext
{
    /// <summary>HTTP method (GET, POST, PUT, PATCH, DELETE, …).</summary>
    string Method { get; set; }

    /// <summary>Full request URI including scheme, host, path, and query.</summary>
    Uri Address { get; set; }

    /// <summary>Mutable header collection for request or response headers.</summary>
    IMessageHeaders Headers { get; }

    /// <summary>
    /// Untyped payload — exposed for infrastructure code (middleware, pipeline executor)
    /// that needs content-type checks (IsJson, IsStreaming) without body access.
    /// Transformers receive the typed sub-interface, not this property directly.
    /// </summary>
    IPayload Payload { get; }

    /// <summary>Cancellation token linked to the underlying transport abort signal.</summary>
    CancellationToken Cancellation { get; }

    /// <summary>Which side of the HTTP exchange this context represents.</summary>
    MessageSide Side { get; }
}
