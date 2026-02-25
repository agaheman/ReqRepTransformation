namespace ReqRepTransformation.Core.Abstractions;

/// <summary>
/// Core transformation contract. Both request-side and response-side transforms
/// implement this interface. Side is determined by registration in TransformationDetail,
/// not by the interface itself.
/// </summary>
public interface ITransformation
{
    /// <summary>
    /// Unique, stable name used for circuit breaker keying, logging, and tracing.
    /// Convention: kebab-case. Examples: "correlation-id-inject", "jwt-forward", "path-v1-rewrite".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Called before ApplyAsync to decide whether this transform should run for the
    /// given message. Returning false skips execution entirely — no logging, no circuit
    /// breaker involvement, no allocation.
    /// Must be synchronous and allocation-free.
    /// </summary>
    bool ShouldApply(IMessageContext context);

    /// <summary>
    /// Executes the transformation on the message.
    /// The CancellationToken is a pre-linked token combining the request abort token
    /// and the per-transform timeout from PipelineOptions / TransformationDetail.
    /// </summary>
    ValueTask ApplyAsync(IMessageContext context, CancellationToken ct);
}

/// <summary>
/// Marker interface for transforms that operate on buffered bodies.
/// Safe for: JSON, XML, Form-encoded, Text, and any header/address modification.
/// The pipeline buffers the request body before calling ApplyAsync.
/// </summary>
public interface IBufferTransform : ITransformation { }

/// <summary>
/// Marker interface for transforms that operate on streaming bodies ONLY.
/// Implementations MUST NEVER call GetJsonAsync or GetBufferAsync on IPayload.
/// The pipeline will NOT buffer the body for IStreamTransform implementations.
/// Use for: file uploads, large binary downloads, and passthrough transforms that
/// only mutate headers while a streaming body flows through untouched.
/// </summary>
public interface IStreamTransform : ITransformation { }
