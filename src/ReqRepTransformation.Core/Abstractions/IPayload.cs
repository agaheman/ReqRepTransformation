using System.Text.Json.Nodes;
using System.IO.Pipelines;

namespace ReqRepTransformation.Core.Abstractions;

/// <summary>
/// Zero-copy, zero-double-serialization payload abstraction.
///
/// Design contract:
/// - JSON is parsed exactly ONCE (first call to GetJsonAsync caches the JsonNode).
/// - All JSON transforms operate on the same cached JsonNode.
/// - Serialization to bytes happens ONCE at pipeline exit via FlushAsync.
/// - Binary/streaming payloads bypass JSON entirely; only GetPipeReaderAsync is valid.
/// - IBufferTransform may call GetJsonAsync, GetBufferAsync.
/// - IStreamTransform may ONLY call GetPipeReaderAsync.
/// </summary>
public interface IPayload
{
    /// <summary>True if the body has any content.</summary>
    bool HasBody { get; }

    /// <summary>True if Content-Type is application/json or application/graphql.</summary>
    bool IsJson { get; }

    /// <summary>True if the body must be treated as a stream (binary, multipart, file upload).</summary>
    bool IsStreaming { get; }

    /// <summary>The raw content type string (e.g., "application/json; charset=utf-8").</summary>
    string? ContentType { get; }

    /// <summary>
    /// Returns the parsed JSON body as a mutable JsonNode.
    /// Parsed lazily on first call; cached for all subsequent calls.
    /// All transforms share the same node reference — mutate in place.
    /// Throws InvalidOperationException if IsJson is false.
    /// </summary>
    ValueTask<JsonNode?> GetJsonAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the raw body as a pooled ReadOnlyMemory buffer.
    /// Throws InvalidOperationException if IsStreaming is true.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> GetBufferAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the PipeReader for streaming body access.
    /// This is the ONLY method IStreamTransform implementations may call.
    /// </summary>
    ValueTask<PipeReader> GetPipeReaderAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the cached JsonNode. Signals dirty state for flush.
    /// Calling mutate on the returned node directly is preferred over calling this.
    /// </summary>
    ValueTask SetJsonAsync(JsonNode node, CancellationToken ct = default);

    /// <summary>Replaces the raw body buffer. Clears any cached JSON.</summary>
    ValueTask SetBufferAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);

    /// <summary>Replaces the body with a stream (for response body swap scenarios).</summary>
    ValueTask ReplaceStreamAsync(Stream stream, CancellationToken ct = default);

    /// <summary>
    /// Serializes the current in-memory state (JsonNode / buffer) back to bytes.
    /// Called once by the pipeline executor at exit — never by individual transforms.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> FlushAsync(CancellationToken ct = default);
}
