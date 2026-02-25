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
/// Thrown when IStreamTransform attempts to access buffered payload methods,
/// or when IBufferTransform attempts to access streaming methods on a streaming payload.
/// </summary>
public sealed class PayloadAccessViolationException : InvalidOperationException
{
    public PayloadAccessViolationException(string message) : base(message) { }
}
