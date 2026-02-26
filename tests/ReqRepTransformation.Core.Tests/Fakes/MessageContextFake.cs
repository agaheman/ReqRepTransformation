using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Core.Pipeline;

namespace ReqRepTransformation.Core.Tests.Fakes;

// ─────────────────────────────────────────────────────────────────────────────
// MessageContextFake implements IBufferMessageContext so that the typed cast
// in PipelineExecutor.DispatchApplyAsync succeeds without a real ASP.NET host.
//
// Payload fakes implement both IBufferPayload and IStreamPayload — the pipeline
// casts to the narrower interface based on which transformer sub-type is running.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class MessageContextFake : IBufferMessageContext, IStreamMessageContext
{
    public string Method { get; set; } = "GET";
    public Uri    Address { get; set; } = new("http://localhost/test");
    public IMessageHeaders Headers { get; } = new FakeHeaderDictionary();
    public CancellationToken Cancellation { get; } = CancellationToken.None;
    public MessageSide Side { get; }

    // ── Typed payload routing ──────────────────────────────────────────────────
    // The underlying fake implements both IBufferPayload and IStreamPayload.
    // Each typed interface property presents the correct narrowed view.
    // The public property gives test code direct access without a cast.
    private readonly FakePayload _payload;

    /// <summary>Direct payload access for test assertions (GetJsonAsync, GetBufferAsync, etc.).</summary>
    public IBufferPayload Payload                    => _payload;
    IPayload        IMessageContext.Payload          => _payload;
    IBufferPayload  IBufferMessageContext.Payload    => _payload;
    IStreamPayload  IStreamMessageContext.Payload    => _payload;

    private MessageContextFake(FakePayload payload, MessageSide side)
    {
        _payload = payload;
        Side     = side;
    }

    public static MessageContextFake Create(
        Uri? uri  = null,
        MessageSide side = MessageSide.Request)
    {
        var ctx = new MessageContextFake(FakePayload.Empty(), side);
        if (uri is not null) ctx.Address = uri;
        return ctx;
    }

    public static MessageContextFake CreateWithJson(JsonNode node)
        => new(FakePayload.FromJson(node), MessageSide.Request);

    public static MessageContextFake CreateWithBuffer(
        ReadOnlyMemory<byte> buffer,
        string contentType = "application/octet-stream",
        MessageSide side   = MessageSide.Request)
        => new(FakePayload.FromBuffer(buffer, contentType), side);
}

// ─────────────────────────────────────────────────────────────────────────────
// Single fake payload that implements IBufferPayload AND IStreamPayload.
// MessageContextFake casts to the narrower interface per transformer type.
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class FakePayload : IBufferPayload, IStreamPayload
{
    // ── IPayload ──────────────────────────────────────────────────────────────
    public bool    HasBody     { get; private init; }
    public bool    IsJson      { get; private init; }
    public bool    IsStreaming { get; private init; }
    public string? ContentType { get; private init; }

    private ReadOnlyMemory<byte> _buffer;
    private JsonNode?            _node;
    private bool                 _dirty;

    private FakePayload() { }

    public static FakePayload Empty() => new()
    {
        HasBody = false, IsJson = false, IsStreaming = false, ContentType = null
    };

    public static FakePayload FromJson(JsonNode node)
    {
        var p = new FakePayload
        {
            HasBody = true, IsJson = true, IsStreaming = false,
            ContentType = "application/json"
        };
        p._node   = node;
        p._buffer = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
        return p;
    }

    public static FakePayload FromBuffer(ReadOnlyMemory<byte> buffer, string contentType)
        => new()
        {
            HasBody     = buffer.Length > 0,
            IsJson      = contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase),
            IsStreaming = contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
                       || contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase),
            ContentType = contentType,
            _buffer     = buffer
        };

    // ── IBufferPayload ────────────────────────────────────────────────────────

    public ValueTask<JsonNode?> GetJsonAsync(CancellationToken ct = default)
    {
        if (_node is null && _buffer.Length > 0)
            _node = JsonNode.Parse(_buffer.Span);
        return ValueTask.FromResult(_node);
    }

    public ValueTask<ReadOnlyMemory<byte>> GetBufferAsync(CancellationToken ct = default)
        => ValueTask.FromResult(_buffer);

    public ValueTask SetJsonAsync(JsonNode node, CancellationToken ct = default)
    {
        _node  = node;
        _dirty = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetBufferAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _buffer = buffer;
        _node   = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>> FlushAsync(CancellationToken ct = default)
    {
        if (_dirty && _node is not null)
            return ValueTask.FromResult(
                (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes(_node.ToJsonString()));
        return ValueTask.FromResult(_buffer);
    }

    // ── IStreamPayload ────────────────────────────────────────────────────────

    public ValueTask<PipeReader> GetPipeReaderAsync(CancellationToken ct = default)
        => ValueTask.FromResult(PipeReader.Create(new MemoryStream(_buffer.ToArray())));

    public ValueTask ReplaceStreamAsync(Stream stream, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}

// ── FakeHeaderDictionary ──────────────────────────────────────────────────────

public sealed class FakeHeaderDictionary : IMessageHeaders
{
    private readonly Dictionary<string, List<string>> _store
        = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Keys => _store.Keys;

    public bool Contains(string key)
        => _store.TryGetValue(key, out var v) && v.Count > 0;

    public string? Get(string key)
        => _store.TryGetValue(key, out var v) ? v.FirstOrDefault() : null;

    public IEnumerable<string> GetValues(string key)
        => _store.TryGetValue(key, out var v) ? v : Enumerable.Empty<string>();

    public void Set(string key, string value)
        => _store[key] = new List<string> { value };

    public void Append(string key, string value)
    {
        if (!_store.TryGetValue(key, out var list))
        {
            list = new List<string>();
            _store[key] = list;
        }
        list.Add(value);
    }

    public void Remove(string key) => _store.Remove(key);

    public bool TryGet(string key, out string? value)
    {
        value = Get(key);
        return value is not null;
    }
}
