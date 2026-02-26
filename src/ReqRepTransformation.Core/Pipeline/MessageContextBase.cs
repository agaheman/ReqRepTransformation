using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Pipeline;

/// <summary>
/// Base class for all <see cref="IMessageContext"/> adapter implementations.
///
/// Implements both <see cref="IBufferMessageContext"/> and <see cref="IStreamMessageContext"/>
/// so the concrete subclass (AspNetRequestMessageContext, etc.) can be passed to the
/// pipeline executor, which then presents the correctly-narrowed typed view to each
/// transformer's ApplyAsync method.
///
/// Concrete subclasses expose their <see cref="PayloadContext"/> (which itself implements
/// both <see cref="IBufferPayload"/> and <see cref="IStreamPayload"/>) via the abstract
/// <see cref="PayloadCore"/> property. The two explicit interface properties route to it.
/// </summary>
public abstract class MessageContextBase : IBufferMessageContext, IStreamMessageContext
{
    protected MessageContextBase(MessageSide side, CancellationToken cancellation)
    {
        Side         = side;
        Cancellation = cancellation;
    }

    public abstract string         Method  { get; set; }
    public abstract Uri            Address { get; set; }
    public abstract IMessageHeaders Headers { get; }

    // ── Typed payload routing ─────────────────────────────────────────────────
    // Subclasses expose their PayloadContext (which implements both payload interfaces).
    // The two explicit interface properties present the correct narrowed view.

    /// <summary>
    /// The underlying PayloadContext — implements IBufferPayload and IStreamPayload.
    /// Subclasses return their concrete PayloadContext field here.
    /// </summary>
    protected abstract PayloadContext PayloadCore { get; }

    // IMessageContext.Payload — untyped, for infrastructure code (pipeline executor)
    IPayload IMessageContext.Payload => PayloadCore;

    // IBufferMessageContext.Payload — narrows to IBufferPayload for IBufferTransformer
    IBufferPayload IBufferMessageContext.Payload => PayloadCore;

    // IStreamMessageContext.Payload — narrows to IStreamPayload for IStreamTransformer
    IStreamPayload IStreamMessageContext.Payload => PayloadCore;

    public CancellationToken Cancellation { get; }
    public MessageSide       Side         { get; }
}
