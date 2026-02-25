using System.Text.Json.Nodes;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Core.Pipeline;

namespace ReqRepTransformation.Core.Tests.Fakes;

/// <summary>
/// In-memory IMessageContext for unit tests.
/// No ASP.NET Core or HttpClient infrastructure required.
/// </summary>
public sealed class MessageContextFake : IMessageContext
{
    private readonly FakeHeaderDictionary _headers = new();
    private readonly PayloadContext _payload;

    private MessageContextFake(
        string method,
        Uri address,
        PayloadContext payload,
        MessageSide side,
        CancellationToken ct)
    {
        Method = method;
        Address = address;
        _payload = payload;
        Side = side;
        Cancellation = ct;
    }

    public string Method { get; set; }
    public Uri Address { get; set; }
    public IMessageHeaders Headers => _headers;
    public IPayload Payload => _payload;
    public CancellationToken Cancellation { get; }
    public MessageSide Side { get; }

    // ──────────────────────────────────────────────────────────────
    // Factory methods
    // ──────────────────────────────────────────────────────────────

    public static MessageContextFake Create(
        string method = "GET",
        string path = "/api/test",
        Dictionary<string, string>? headers = null,
        MessageSide side = MessageSide.Request,
        CancellationToken ct = default)
    {
        var ctx = new MessageContextFake(
            method,
            new Uri($"https://localhost{path}"),
            new PayloadContext(null, null, false),
            side,
            ct);

        if (headers is not null)
            foreach (var (k, v) in headers)
                ctx.Headers.Set(k, v);

        return ctx;
    }

    public static MessageContextFake CreateWithJson(
        JsonNode json,
        string method = "POST",
        string path = "/api/test",
        Dictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        var payload = PayloadContext.FromJson(json);
        var ctx = new MessageContextFake(
            method,
            new Uri($"https://localhost{path}"),
            payload,
            MessageSide.Request,
            ct);

        if (headers is not null)
            foreach (var (k, v) in headers)
                ctx.Headers.Set(k, v);

        return ctx;
    }

    public static MessageContextFake CreateWithBuffer(
        ReadOnlyMemory<byte> buffer,
        string contentType,
        string method = "POST",
        string path = "/api/test",
        CancellationToken ct = default)
    {
        var payload = PayloadContext.FromBuffer(buffer, contentType);
        return new MessageContextFake(
            method,
            new Uri($"https://localhost{path}"),
            payload,
            MessageSide.Request,
            ct);
    }
}

/// <summary>Simple in-memory header dictionary for fakes.</summary>
public sealed class FakeHeaderDictionary : IMessageHeaders
{
    private readonly Dictionary<string, List<string>> _headers
        = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Keys => _headers.Keys;

    public string? Get(string key)
        => _headers.TryGetValue(key, out var v) ? string.Join(",", v) : null;

    public IEnumerable<string> GetValues(string key)
        => _headers.TryGetValue(key, out var v) ? v : Enumerable.Empty<string>();

    public void Set(string key, string value)
        => _headers[key] = new List<string> { value };

    public void Append(string key, string value)
    {
        if (!_headers.TryGetValue(key, out var list))
            _headers[key] = list = new List<string>();
        list.Add(value);
    }

    public void Remove(string key)
        => _headers.Remove(key);

    public bool Contains(string key)
        => _headers.ContainsKey(key);

    public bool TryGet(string key, out string? value)
    {
        if (_headers.TryGetValue(key, out var list))
        {
            value = string.Join(",", list);
            return true;
        }
        value = null;
        return false;
    }
}
