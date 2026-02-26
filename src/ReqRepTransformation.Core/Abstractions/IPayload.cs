using System.IO.Pipelines;
using System.Text.Json.Nodes;

namespace ReqRepTransformation.Core.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Issue 1 fix: compile-time payload discipline via interface segregation.
//
// Previously a single IPayload exposed both GetJsonAsync and GetPipeReaderAsync,
// and the rule "IStreamTransformer must never call GetJsonAsync" was only
// enforced at runtime (a warning log + skip). That is documentation, not
// encapsulation.
//
// New design:
//   IPayload          — common read-only properties: HasBody, IsJson, IsStreaming, ContentType
//   IBufferPayload    — extends IPayload, adds GetJsonAsync / GetBufferAsync / Flush
//   IStreamPayload    — extends IPayload, adds GetPipeReaderAsync / Flush
//
// IBufferTransformer.ApplyAsync receives IMessageContext whose Payload is typed
// as IBufferPayload — GetPipeReaderAsync simply doesn't exist on the type.
// IStreamTransformer.ApplyAsync receives IMessageContext whose Payload is typed
// as IStreamPayload — GetJsonAsync simply doesn't exist on the type.
//
// Wrong method = compile error. No test needed. No documentation needed.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Common payload properties available to all transformers regardless of body type.
/// </summary>
public interface IPayload
{
    /// <summary>True if the message has a body with content.</summary>
    bool HasBody { get; }

    /// <summary>True if Content-Type is application/json (or graphql / ndjson).</summary>
    bool IsJson { get; }

    /// <summary>True if the body is a binary/multipart stream that must not be buffered.</summary>
    bool IsStreaming { get; }

    /// <summary>The raw Content-Type header value.</summary>
    string? ContentType { get; }
}

/// <summary>
/// Payload view for <see cref="IBufferTransformer"/> implementations.
/// Exposes buffered access (JSON + raw bytes) and write-back methods.
///
/// Compile-time guarantee: a transformer that declares <see cref="IBufferTransformer"/>
/// can only receive this interface — <see cref="IStreamPayload.GetPipeReaderAsync"/>
/// does not exist here and therefore cannot be called by mistake.
///
/// Zero-double-serialization contract:
/// - JSON is parsed exactly once on first <see cref="GetJsonAsync"/> call.
/// - All transforms share the same <see cref="System.Text.Json.Nodes.JsonNode"/> reference — mutate in-place.
/// - <see cref="FlushAsync"/> serialises to bytes exactly once, at pipeline exit.
/// </summary>
public interface IBufferPayload : IPayload
{
    /// <summary>
    /// Returns the parsed JSON body. Lazily parsed, then cached for the request lifetime.
    /// Throws <see cref="Models.PayloadAccessViolationException"/> if <see cref="IPayload.IsJson"/> is false.
    /// </summary>
    ValueTask<JsonNode?> GetJsonAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the raw body as bytes. Cached after first read.
    /// Throws <see cref="Models.PayloadAccessViolationException"/> if <see cref="IPayload.IsStreaming"/> is true.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> GetBufferAsync(CancellationToken ct = default);

    /// <summary>Replaces the cached JsonNode. Marks body dirty for flush.</summary>
    ValueTask SetJsonAsync(JsonNode node, CancellationToken ct = default);

    /// <summary>Replaces the raw body buffer. Clears cached JSON.</summary>
    ValueTask SetBufferAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);

    /// <summary>
    /// Serialises the current in-memory state to bytes for writing to the wire.
    /// Called once by the pipeline at exit — never by individual transforms.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> FlushAsync(CancellationToken ct = default);
}

/// <summary>
/// Payload view for <see cref="IStreamTransformer"/> implementations.
/// Exposes streaming (pipe) access only.
///
/// Compile-time guarantee: a transformer that declares <see cref="IStreamTransformer"/>
/// can only receive this interface — <see cref="IBufferPayload.GetJsonAsync"/> and
/// <see cref="IBufferPayload.GetBufferAsync"/> do not exist here and cannot be called.
/// </summary>
public interface IStreamPayload : IPayload
{
    /// <summary>
    /// Returns the <see cref="PipeReader"/> for streaming body access.
    /// This is the only body-read method available to stream transformers.
    /// </summary>
    ValueTask<PipeReader> GetPipeReaderAsync(CancellationToken ct = default);

    /// <summary>Replaces the body with an alternative stream.</summary>
    ValueTask ReplaceStreamAsync(Stream stream, CancellationToken ct = default);
}
