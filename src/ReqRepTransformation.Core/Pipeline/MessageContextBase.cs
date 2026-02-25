using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Pipeline;

/// <summary>
/// Base class providing common state for IMessageContext adapter implementations.
/// AspNetMessageContext and HttpClientMessageContext both inherit from this.
/// </summary>
public abstract class MessageContextBase : IMessageContext
{
    protected MessageContextBase(
        MessageSide side,
        CancellationToken cancellation)
    {
        Side = side;
        Cancellation = cancellation;
    }

    public abstract string Method { get; set; }
    public abstract Uri Address { get; set; }
    public abstract IMessageHeaders Headers { get; }
    public abstract IPayload Payload { get; }

    public CancellationToken Cancellation { get; }
    public MessageSide Side { get; }
}
