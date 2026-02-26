using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Abstractions;

/// <summary>
/// Base transformer contract — shared name, configure, and ShouldApply.
/// Do NOT implement this directly; implement <see cref="IBufferTransformer"/>
/// or <see cref="IStreamTransformer"/> which carry the correctly-typed context.
/// </summary>
public interface ITransformer
{
    /// <summary>
    /// Stable kebab-case name. Used for structured logging and OTEL spans.
    /// Examples: "add-header", "jwt-forward", "path-prefix-rewrite".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Called once after DI resolution. Transformer reads its typed configuration
    /// from <paramref name="params"/>. Must be called before <c>ApplyAsync</c>.
    /// </summary>
    void Configure(TransformerParams @params);

    /// <summary>
    /// Synchronous, allocation-free guard. Return false to skip execution entirely.
    /// </summary>
    bool ShouldApply(IMessageContext context);
}

/// <summary>
/// Transformer that operates on buffered bodies (JSON, XML, form, plain-text)
/// and/or headers and URI — anything that does NOT require streaming.
///
/// Compile-time payload discipline:
/// <see cref="ApplyAsync"/> receives <see cref="IBufferMessageContext"/> whose
/// <see cref="IBufferMessageContext.Payload"/> is typed as <see cref="IBufferPayload"/>.
/// <c>GetPipeReaderAsync</c> does not exist on that type — calling it is a compile error.
///
/// Registration:
/// <code>services.AddKeyedTransient&lt;ITransformer, MyTransformer&gt;(key);</code>
/// </summary>
public interface IBufferTransformer : ITransformer
{
    /// <summary>
    /// Execute the transformation. <paramref name="context"/> exposes only buffered
    /// payload access — pipe reader is not available at the type level.
    /// </summary>
    ValueTask ApplyAsync(IBufferMessageContext context, CancellationToken ct);
}

/// <summary>
/// Transformer that operates on streaming bodies (binary, multipart, file upload).
/// NEVER buffer the body — only <see cref="IStreamPayload.GetPipeReaderAsync"/> is available.
///
/// Compile-time payload discipline:
/// <see cref="ApplyAsync"/> receives <see cref="IStreamMessageContext"/> whose
/// <see cref="IStreamMessageContext.Payload"/> is typed as <see cref="IStreamPayload"/>.
/// <c>GetJsonAsync</c> and <c>GetBufferAsync</c> do not exist on that type.
///
/// Registration:
/// <code>services.AddKeyedTransient&lt;ITransformer, MyTransformer&gt;(key);</code>
/// </summary>
public interface IStreamTransformer : ITransformer
{
    /// <summary>
    /// Execute the transformation. <paramref name="context"/> exposes only streaming
    /// payload access — buffer/JSON methods are not available at the type level.
    /// </summary>
    ValueTask ApplyAsync(IStreamMessageContext context, CancellationToken ct);
}

// ── Typed context views ───────────────────────────────────────────────────────

/// <summary>
/// <see cref="IMessageContext"/> view passed to <see cref="IBufferTransformer.ApplyAsync"/>.
/// <see cref="Payload"/> is typed as <see cref="IBufferPayload"/> — pipe reader inaccessible.
/// </summary>
public interface IBufferMessageContext : IMessageContext
{
    /// <summary>Buffered payload. JSON and raw-byte access available.</summary>
    new IBufferPayload Payload { get; }
}

/// <summary>
/// <see cref="IMessageContext"/> view passed to <see cref="IStreamTransformer.ApplyAsync"/>.
/// <see cref="Payload"/> is typed as <see cref="IStreamPayload"/> — JSON/buffer inaccessible.
/// </summary>
public interface IStreamMessageContext : IMessageContext
{
    /// <summary>Streaming payload. Only pipe reader and stream replacement available.</summary>
    new IStreamPayload Payload { get; }
}

// ── Backwards-compat aliases ──────────────────────────────────────────────────
[Obsolete("Use ITransformer instead.", error: false)]
public interface ITransformation : ITransformer { }
[Obsolete("Use IBufferTransformer instead.", error: false)]
public interface IBufferTransform : IBufferTransformer { }
[Obsolete("Use IStreamTransformer instead.", error: false)]
public interface IStreamTransform : IStreamTransformer { }
