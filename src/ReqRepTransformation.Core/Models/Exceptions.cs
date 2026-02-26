namespace ReqRepTransformation.Core.Models;

/// <summary>
/// Thrown when FailureMode is StopPipeline and a transform fails or
/// its circuit breaker is open.
/// </summary>
public sealed class TransformationException : Exception
{
    public string TransformName { get; }
    public MessageSide Side { get; }

    public TransformationException(
        string transformName,
        MessageSide side,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        TransformName = transformName;
        Side = side;
    }
}

/// <summary>
/// Thrown when an <c>IStreamTransformer</c> attempts to access buffered payload methods,
/// or when an <c>IBufferTransformer</c> attempts to access streaming methods.
/// With typed context dispatch this exception is a last-resort guard — the compile-time
/// type system prevents the wrong method being called in correctly-implemented transformers.
/// </summary>
public sealed class PayloadAccessViolationException : InvalidOperationException
{
    public PayloadAccessViolationException(string message) : base(message) { }
}
