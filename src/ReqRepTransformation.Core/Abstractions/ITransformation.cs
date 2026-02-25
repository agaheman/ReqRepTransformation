using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Abstractions;

/// <summary>
/// Core transformer contract. Implementations are registered as keyed DI services
/// and resolved by <see cref="RouteTransformerEntry.TransformerKey"/>.
///
/// Lifecycle: <c>Transient</c> — a new instance is created per resolution, then
/// <see cref="Configure"/> is called with the route-specific <see cref="TransformerParams"/>
/// before the instance is added to the pipeline.
///
/// Keyed registration pattern:
/// <code>
/// services.AddKeyedTransient&lt;ITransformer, AddHeaderTransformer&gt;(TransformerKeys.AddHeader);
/// </code>
///
/// Resolution pattern:
/// <code>
/// var transformer = sp.GetRequiredKeyedService&lt;ITransformer&gt;(entry.TransformerKey);
/// transformer.Configure(new TransformerParams(entry.ParamsJson));
/// </code>
/// </summary>
public interface ITransformer
{
    /// <summary>
    /// Unique, stable name used for circuit-breaker keying, structured logging, and OTEL spans.
    /// Convention: kebab-case. Should reflect the transformer type, not instance params.
    /// Examples: "add-header", "jwt-forward", "path-prefix-rewrite".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Called once after DI resolution, before the transformer is added to the pipeline.
    /// The transformer reads its typed parameters from <paramref name="params"/>.
    /// Must be called exactly once per instance.
    /// </summary>
    void Configure(TransformerParams @params);

    /// <summary>
    /// Called synchronously before <see cref="ApplyAsync"/> to decide whether this
    /// transformer should run for the given message context.
    /// Returning false skips <see cref="ApplyAsync"/> entirely — no logging, no circuit
    /// breaker involvement, no allocation overhead.
    /// Must be synchronous and allocation-free.
    /// </summary>
    bool ShouldApply(IMessageContext context);

    /// <summary>
    /// Executes the transformation on the message.
    /// The <paramref name="ct"/> is a pre-linked token combining the request abort signal
    /// and the per-transformer timeout from <c>PipelineOptions</c> / <c>TransformationDetail</c>.
    /// </summary>
    ValueTask ApplyAsync(IMessageContext context, CancellationToken ct);
}

/// <summary>
/// Marker interface for transformers that operate on buffered bodies.
/// Safe for: JSON, XML, form-encoded, plain-text, and any header/address modification.
/// The pipeline ensures the request body is buffered before calling <see cref="ITransformer.ApplyAsync"/>.
/// </summary>
public interface IBufferTransformer : ITransformer { }

/// <summary>
/// Marker interface for transformers that operate on streaming bodies ONLY.
/// Implementations MUST NEVER call <c>GetJsonAsync</c> or <c>GetBufferAsync</c> on <see cref="IPayload"/>.
/// The pipeline will NOT buffer the body for <see cref="IStreamTransformer"/> implementations.
/// Use for: file uploads, large binary downloads, and passthrough transforms that only
/// mutate headers while a streaming body flows through untouched.
/// </summary>
public interface IStreamTransformer : ITransformer { }

// ── Backwards-compat aliases (removed after migration) ──────────────────────────
// Old code that still references ITransformation / IBufferTransform / IStreamTransform
// continues to compile via these type aliases pointing at the new interfaces.
[Obsolete("Use ITransformer instead.", error: false)]
public interface ITransformation : ITransformer { }
[Obsolete("Use IBufferTransformer instead.", error: false)]
public interface IBufferTransform : IBufferTransformer { }
[Obsolete("Use IStreamTransformer instead.", error: false)]
public interface IStreamTransform : IStreamTransformer { }
