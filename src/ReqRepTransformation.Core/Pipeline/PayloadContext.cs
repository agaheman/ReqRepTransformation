using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Infrastructure.Memory;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Pipeline;

/// <summary>
/// Concrete implementation of IPayload.
///
/// Zero-double-serialization guarantee:
/// - _cachedJson is populated exactly once on first GetJsonAsync() call.
/// - SetJsonAsync() replaces the reference in-memory.
/// - FlushAsync() serializes the JsonNode exactly once at pipeline exit.
/// - IsJsonDirty tracks whether any transform has touched the JSON,
///   avoiding re-serialization if no transform mutated the body.
///
/// Thread-safety:
/// - _jsonInitSemaphore(1,1) ensures only one concurrent JSON parse.
/// - All other state is write-once or replaced atomically.
/// </summary>
public sealed class PayloadContext : IPayload, IAsyncDisposable
{
    // ──────────────────────────────────────────────────────────────
    // Fields
    // ──────────────────────────────────────────────────────────────

    private readonly PipeReader? _pipeReader;
    private readonly SemaphoreSlim _jsonInitSemaphore = new(1, 1);

    private JsonNode? _cachedJson;
    private ReadOnlyMemory<byte> _buffer;
    private Stream? _replacedStream;
    private bool _isJsonDirty;
    private bool _isBufferDirty;
    private bool _pipeReaderConsumed;

    // ──────────────────────────────────────────────────────────────
    // Constructor
    // ──────────────────────────────────────────────────────────────

    /// <param name="pipeReader">The PipeReader to read body from. Null for response bodies with no body.</param>
    /// <param name="contentType">The raw Content-Type header value.</param>
    /// <param name="hasBody">Whether the message has a body at all.</param>
    public PayloadContext(
        PipeReader? pipeReader,
        string? contentType,
        bool hasBody)
    {
        _pipeReader = pipeReader;
        ContentType = contentType;
        HasBody = hasBody;
        IsJson = hasBody && IsJsonContentType(contentType);
        IsStreaming = hasBody && IsStreamingContentType(contentType);
    }

    /// <summary>Creates a PayloadContext from an in-memory buffer (for response body or pre-buffered requests).</summary>
    public static PayloadContext FromBuffer(
        ReadOnlyMemory<byte> buffer,
        string? contentType)
    {
        var ctx = new PayloadContext(null, contentType, buffer.Length > 0);
        ctx._buffer = buffer;
        ctx._pipeReaderConsumed = true; // no reader to consume
        return ctx;
    }

    /// <summary>Creates a PayloadContext from an existing JsonNode (for tests or known-JSON responses).</summary>
    public static PayloadContext FromJson(JsonNode node, string contentType = "application/json")
    {
        var ctx = new PayloadContext(null, contentType, true);
        ctx._cachedJson = node;
        ctx._pipeReaderConsumed = true;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────
    // IPayload implementation
    // ──────────────────────────────────────────────────────────────

    public bool HasBody { get; }
    public bool IsJson { get; }
    public bool IsStreaming { get; }
    public string? ContentType { get; }

    public async ValueTask<JsonNode?> GetJsonAsync(CancellationToken ct = default)
    {
        if (!IsJson)
            throw new PayloadAccessViolationException(
                $"GetJsonAsync called on a non-JSON payload (Content-Type: {ContentType}). " +
                $"Check IPayload.IsJson before calling this method.");

        if (_cachedJson is not null)
            return _cachedJson;

        await _jsonInitSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring semaphore
            if (_cachedJson is not null)
                return _cachedJson;

            var buffer = await ReadPipeReaderToBufferAsync(ct).ConfigureAwait(false);

            if (buffer.Length == 0)
            {
                _cachedJson = null;
                return null;
            }

            _cachedJson = JsonNode.Parse(buffer.Span, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            return _cachedJson;
        }
        finally
        {
            _jsonInitSemaphore.Release();
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> GetBufferAsync(CancellationToken ct = default)
    {
        if (IsStreaming)
            throw new PayloadAccessViolationException(
                "GetBufferAsync called on a streaming payload. " +
                "Use GetPipeReaderAsync() for streaming bodies.");

        if (_buffer.Length > 0)
            return _buffer;

        _buffer = await ReadPipeReaderToBufferAsync(ct).ConfigureAwait(false);
        return _buffer;
    }

    public ValueTask<PipeReader> GetPipeReaderAsync(CancellationToken ct = default)
    {
        if (!IsStreaming && _pipeReader is null)
            throw new PayloadAccessViolationException(
                "GetPipeReaderAsync called but no PipeReader is available. " +
                "This payload is a buffered type — use GetBufferAsync() or GetJsonAsync().");

        return ValueTask.FromResult(_pipeReader!);
    }

    public ValueTask SetJsonAsync(JsonNode node, CancellationToken ct = default)
    {
        _cachedJson = node;
        _isJsonDirty = true;
        // Clear any cached buffer — JSON is now the source of truth
        _buffer = ReadOnlyMemory<byte>.Empty;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetBufferAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _buffer = buffer;
        _isBufferDirty = true;
        // Invalidate cached JSON — buffer is now the source of truth
        _cachedJson = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask ReplaceStreamAsync(Stream stream, CancellationToken ct = default)
    {
        _replacedStream = stream;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Serializes the current state to bytes for writing to the wire.
    /// Called ONCE at pipeline exit. Never called by transforms.
    ///
    /// Priority: ReplacedStream > DirtyJson > DirtyBuffer > Original Buffer.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>> FlushAsync(CancellationToken ct = default)
    {
        if (_replacedStream is not null)
        {
            using var ms = PooledMemoryManager.GetStream("reqrep-flush");
            await _replacedStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray().AsMemory();
        }

        if (_isJsonDirty && _cachedJson is not null)
        {
            using var ms = PooledMemoryManager.GetStream("reqrep-flush-json");
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });
            _cachedJson.WriteTo(writer);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            return ms.ToArray().AsMemory();
        }

        if (_isBufferDirty && _buffer.Length > 0)
            return _buffer;

        // If we have a cached buffer from original read, return it
        if (_buffer.Length > 0)
            return _buffer;

        // Nothing mutated — return the original body by re-reading
        // (This path only reached when FlushAsync is called but no transform touched body)
        if (_pipeReader is not null && !_pipeReaderConsumed)
        {
            _buffer = await ReadPipeReaderToBufferAsync(ct).ConfigureAwait(false);
            return _buffer;
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    // ──────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────

    private async ValueTask<ReadOnlyMemory<byte>> ReadPipeReaderToBufferAsync(CancellationToken ct)
    {
        if (_pipeReaderConsumed || _pipeReader is null)
            return _buffer;

        using var ms = PooledMemoryManager.GetStream("reqrep-read-body");

        try
        {
            while (true)
            {
                var result = await _pipeReader.ReadAsync(ct).ConfigureAwait(false);
                var readBuffer = result.Buffer;

                foreach (var segment in readBuffer)
                    ms.Write(segment.Span);

                _pipeReader.AdvanceTo(readBuffer.End);

                if (result.IsCompleted || result.IsCanceled)
                    break;
            }
        }
        finally
        {
            _pipeReaderConsumed = true;
        }

        _buffer = ms.ToArray().AsMemory();
        return _buffer;
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (contentType is null) return false;
        var span = contentType.AsSpan();
        return span.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("application/graphql", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("application/ndjson", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStreamingContentType(string? contentType)
    {
        if (contentType is null) return false;
        var span = contentType.AsSpan();
        return span.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("application/protobuf", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("application/vnd.google.protobuf", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        _jsonInitSemaphore.Dispose();

        if (_replacedStream is not null)
            await _replacedStream.DisposeAsync().ConfigureAwait(false);
    }
}
