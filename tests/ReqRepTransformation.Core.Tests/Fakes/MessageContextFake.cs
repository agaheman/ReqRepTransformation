using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Core.Pipeline;

namespace ReqRepTransformation.Core.Tests.Fakes;

public sealed class MessageContextFake : IMessageContext
{
    public string Method { get; set; } = "GET";
    public Uri    Address { get; set; } = new("http://localhost/test");
    public IMessageHeaders Headers { get; } = new FakeHeaderDictionary();
    public IPayload Payload { get; }
    public CancellationToken Cancellation { get; } = CancellationToken.None;
    public MessageSide Side { get; }

    private MessageContextFake(IPayload payload, MessageSide side)
    {
        Payload = payload;
        Side    = side;
    }

    public static MessageContextFake Create(
        Uri? uri  = null,
        MessageSide side = MessageSide.Request)
    {
        var ctx = new MessageContextFake(new EmptyPayload(), side);
        if (uri is not null) ctx.Address = uri;
        return ctx;
    }

    public static MessageContextFake CreateWithJson(JsonNode node)
    {
        var json    = node.ToJsonString();
        var bytes   = System.Text.Encoding.UTF8.GetBytes(json);
        var payload = new FakeJsonPayload(bytes);
        return new MessageContextFake(payload, MessageSide.Request);
    }

    public static MessageContextFake CreateWithBuffer(
        ReadOnlyMemory<byte> buffer,
        string contentType = "application/octet-stream",
        MessageSide side   = MessageSide.Request)
        => new(new FakeBufferPayload(buffer, contentType), side);
}

internal sealed class EmptyPayload : IPayload
{
    public bool HasBody => false;
    public bool IsJson => false;
    public bool IsStreaming => false;
    public string? ContentType => null;
    public ValueTask<JsonNode?> GetJsonAsync(CancellationToken ct = default) => ValueTask.FromResult<JsonNode?>(null);
    public ValueTask<ReadOnlyMemory<byte>> GetBufferAsync(CancellationToken ct = default) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
    public ValueTask<PipeReader> GetPipeReaderAsync(CancellationToken ct = default) => ValueTask.FromResult(PipeReader.Create(Stream.Null));
    public ValueTask SetJsonAsync(JsonNode node, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask SetBufferAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask ReplaceStreamAsync(Stream stream, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<ReadOnlyMemory<byte>> FlushAsync(CancellationToken ct = default) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
}

internal sealed class FakeJsonPayload : IPayload
{
    private readonly ReadOnlyMemory<byte> _original;
    private JsonNode? _node;
    private bool _dirty;

    public FakeJsonPayload(ReadOnlyMemory<byte> bytes) => _original = bytes;

    public bool HasBody => true;
    public bool IsJson => true;
    public bool IsStreaming => false;
    public string? ContentType => "application/json";

    public ValueTask<JsonNode?> GetJsonAsync(CancellationToken ct = default)
    {
        _node ??= JsonNode.Parse(_original.Span);
        return ValueTask.FromResult(_node);
    }

    public ValueTask SetJsonAsync(JsonNode node, CancellationToken ct = default)
    {
        _node  = node;
        _dirty = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>> FlushAsync(CancellationToken ct = default)
    {
        if (_dirty && _node is not null)
            return ValueTask.FromResult(
                (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes(_node.ToJsonString()));
        return ValueTask.FromResult(_original);
    }

    public ValueTask<ReadOnlyMemory<byte>> GetBufferAsync(CancellationToken ct = default)
        => ValueTask.FromResult(_original);
    public ValueTask<PipeReader> GetPipeReaderAsync(CancellationToken ct = default)
        => ValueTask.FromResult(PipeReader.Create(new MemoryStream(_original.ToArray())));
    public ValueTask SetBufferAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => ValueTask.CompletedTask;
    public ValueTask ReplaceStreamAsync(Stream stream, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}

internal sealed class FakeBufferPayload : IPayload
{
    private readonly ReadOnlyMemory<byte> _buffer;
    public bool HasBody => _buffer.Length > 0;
    public bool IsJson => ContentType?.StartsWith("application/json") == true;
    public bool IsStreaming => ContentType?.StartsWith("multipart/") == true
                            || ContentType == "application/octet-stream";
    public string? ContentType { get; }
    public FakeBufferPayload(ReadOnlyMemory<byte> buffer, string contentType)
    { _buffer = buffer; ContentType = contentType; }
    public ValueTask<JsonNode?> GetJsonAsync(CancellationToken ct = default) => ValueTask.FromResult<JsonNode?>(null);
    public ValueTask<ReadOnlyMemory<byte>> GetBufferAsync(CancellationToken ct = default) => ValueTask.FromResult(_buffer);
    public ValueTask<PipeReader> GetPipeReaderAsync(CancellationToken ct = default)
        => ValueTask.FromResult(PipeReader.Create(new MemoryStream(_buffer.ToArray())));
    public ValueTask SetJsonAsync(JsonNode node, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask SetBufferAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask ReplaceStreamAsync(Stream stream, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<ReadOnlyMemory<byte>> FlushAsync(CancellationToken ct = default) => ValueTask.FromResult(_buffer);
}

public sealed class FakeHeaderDictionary : IMessageHeaders
{
    private readonly Dictionary<string, List<string>> _store
        = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Keys => _store.Keys;
    public bool Contains(string key) => _store.ContainsKey(key) && _store[key].Count > 0;
    public string? Get(string key) => _store.TryGetValue(key, out var v) ? v.FirstOrDefault() : null;
    public IEnumerable<string> GetValues(string key) => _store.TryGetValue(key, out var v) ? v : Enumerable.Empty<string>();
    public void Set(string key, string value) { _store[key] = new List<string> { value }; }
    public void Append(string key, string value)
    {
        if (!_store.TryGetValue(key, out var list)) { list = new(); _store[key] = list; }
        list.Add(value);
    }
    public void Remove(string key) => _store.Remove(key);
    public bool TryGet(string key, out string? value)
    {
        value = Get(key);
        return value is not null;
    }
}
