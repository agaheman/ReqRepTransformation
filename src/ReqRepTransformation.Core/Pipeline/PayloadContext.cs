using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Infrastructure.Memory;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Pipeline;

/// <summary>
/// Concrete payload implementation.
/// Implements both <see cref="IBufferPayload"/> and <see cref="IStreamPayload"/>
/// so a single instance can be held by the adapter but presented to each
/// transformer as the narrower typed interface.
///
/// Issue 4 fix — SemaphoreSlim replaced with Interlocked sentinel:
///   The old <c>new SemaphoreSlim(1,1)</c> was per-instance (per-request), so it
///   was NOT a cross-request throughput killer. However, SemaphoreSlim still has
///   allocation cost, GC pressure, and async overhead on the contended path.
///   Replaced with a lock-free double-checked pattern using Interlocked.CompareExchange
///   on an int sentinel (_parseState: 0=unparsed, 1=parsing, 2=done).
///   In the overwhelmingly common single-threaded pipeline case, the exchange
///   succeeds immediately with zero allocation and zero async overhead.
///   On the rare concurrent call (parallel transform mode), the second caller
///   spins briefly with Task.Yield until the first parse completes.
///
/// Zero-double-serialization contract:
///   - JSON parsed exactly once on first GetJsonAsync call.
///   - All transforms share the same JsonNode reference — mutate in-place.
///   - FlushAsync serialises to bytes exactly once at pipeline exit.
/// </summary>
public sealed class PayloadContext : IBufferPayload, IStreamPayload, IAsyncDisposable
{
    // ── Parse state sentinel (Issue 4: replaces SemaphoreSlim) ───────────────
    // 0 = not yet parsed, 1 = parse in progress, 2 = parse complete
    private const int ParseUnstarted  = 0;
    private const int ParseInProgress = 1;
    private const int ParseDone       = 2;
    private int _parseState = ParseUnstarted;

    // ── Body state ────────────────────────────────────────────────────────────
    private readonly PipeReader? _pipeReader;
    private JsonNode?            _cachedJson;
    private ReadOnlyMemory<byte> _buffer;
    private Stream?              _replacedStream;
    private bool                 _isJsonDirty;
    private bool                 _isBufferDirty;
    private bool                 _pipeReaderConsumed;

    // ── IPayload ──────────────────────────────────────────────────────────────
    public bool     HasBody     { get; }
    public bool     IsJson      { get; }
    public bool     IsStreaming { get; }
    public string?  ContentType { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public PayloadContext(PipeReader? pipeReader, string? contentType, bool hasBody)
    {
        _pipeReader = pipeReader;
        ContentType = contentType;
        HasBody     = hasBody;
        IsJson      = hasBody && IsJsonContentType(contentType);
        IsStreaming = hasBody && IsStreamingContentType(contentType);
    }

    public static PayloadContext FromBuffer(ReadOnlyMemory<byte> buffer, string? contentType)
    {
        var ctx = new PayloadContext(null, contentType, buffer.Length > 0);
        ctx._buffer             = buffer;
        ctx._pipeReaderConsumed = true;
        return ctx;
    }

    public static PayloadContext FromJson(JsonNode node, string contentType = "application/json")
    {
        var ctx = new PayloadContext(null, contentType, true);
        ctx._cachedJson         = node;
        ctx._pipeReaderConsumed = true;
        ctx._parseState         = ParseDone;
        return ctx;
    }

    // ── IBufferPayload ────────────────────────────────────────────────────────

    public async ValueTask<JsonNode?> GetJsonAsync(CancellationToken ct = default)
    {
        if (!IsJson)
            throw new PayloadAccessViolationException(
                $"GetJsonAsync called on a non-JSON payload (Content-Type: {ContentType}). " +
                "Check IPayload.IsJson before calling this method.");

        // Fast path — already parsed (overwhelmingly the common case)
        if (_parseState == ParseDone)
            return _cachedJson;

        // Lock-free double-checked: only one concurrent caller parses.
        // In sequential pipeline mode this branch is never contended.
        if (Interlocked.CompareExchange(ref _parseState, ParseInProgress, ParseUnstarted) == ParseUnstarted)
        {
            try
            {
                var buffer = await ReadPipeReaderToBufferAsync(ct).ConfigureAwait(false);

                _cachedJson = buffer.Length == 0
                    ? null
                    : JsonNode.Parse(buffer.Span, documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling     = JsonCommentHandling.Skip
                    });
            }
            finally
            {
                // Publish result — makes _cachedJson visible to all subsequent readers
                Volatile.Write(ref _parseState, ParseDone);
            }
        }
        else
        {
            // Another concurrent call is parsing — spin with yields until done.
            // This only occurs in AllowParallelNonDependentTransforms mode, which
            // the docs explicitly warn against for JSON-mutating transforms.
            while (Volatile.Read(ref _parseState) != ParseDone)
                await Task.Yield();
        }

        return _cachedJson;
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

    public ValueTask SetJsonAsync(JsonNode node, CancellationToken ct = default)
    {
        _cachedJson   = node;
        _isJsonDirty  = true;
        _buffer       = ReadOnlyMemory<byte>.Empty;
        Volatile.Write(ref _parseState, ParseDone);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetBufferAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _buffer        = buffer;
        _isBufferDirty = true;
        _cachedJson    = null;
        Volatile.Write(ref _parseState, ParseUnstarted);
        return ValueTask.CompletedTask;
    }

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
            using var ms     = PooledMemoryManager.GetStream("reqrep-flush-json");
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });
            _cachedJson.WriteTo(writer);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            return ms.ToArray().AsMemory();
        }

        if (_isBufferDirty && _buffer.Length > 0)
            return _buffer;

        if (_buffer.Length > 0)
            return _buffer;

        if (_pipeReader is not null && !_pipeReaderConsumed)
        {
            _buffer = await ReadPipeReaderToBufferAsync(ct).ConfigureAwait(false);
            return _buffer;
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    // ── IStreamPayload ────────────────────────────────────────────────────────

    public ValueTask<PipeReader> GetPipeReaderAsync(CancellationToken ct = default)
    {
        if (!IsStreaming && _pipeReader is null)
            throw new PayloadAccessViolationException(
                "GetPipeReaderAsync called but no PipeReader is available. " +
                "This payload is buffered — use GetBufferAsync() or GetJsonAsync().");

        return ValueTask.FromResult(_pipeReader!);
    }

    public ValueTask ReplaceStreamAsync(Stream stream, CancellationToken ct = default)
    {
        _replacedStream = stream;
        return ValueTask.CompletedTask;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

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
                var buffer = result.Buffer;

                foreach (var segment in buffer)
                    ms.Write(segment.Span);

                _pipeReader.AdvanceTo(buffer.End);

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

    private static bool IsJsonContentType(string? ct)
    {
        if (ct is null) return false;
        var s = ct.AsSpan();
        return s.StartsWith("application/json",     StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("application/graphql",  StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("application/ndjson",   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStreamingContentType(string? ct)
    {
        if (ct is null) return false;
        var s = ct.AsSpan();
        return s.StartsWith("application/octet-stream",          StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("multipart/",                        StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("application/grpc",                  StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("application/protobuf",              StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("application/vnd.google.protobuf",   StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask DisposeAsync()
    {
        if (_replacedStream is not null)
            return _replacedStream.DisposeAsync();
        return ValueTask.CompletedTask;
    }
}
