using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Core.Pipeline;
using Xunit;

namespace ReqRepTransformation.Core.Tests.Pipeline;

public sealed class PayloadContextTests
{
    // ──────────────────────────────────────────────────────────────
    // JSON parsing
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetJsonAsync_ParsesJsonBody_OnFirstCall()
    {
        // Arrange
        var json = """{"userId": 42, "name": "Alice"}""";
        var buffer = Encoding.UTF8.GetBytes(json).AsMemory();
        var sut = PayloadContext.FromBuffer(buffer, "application/json");

        // Act
        var node = await sut.GetJsonAsync();

        // Assert
        node.Should().NotBeNull();
        node!["userId"]!.GetValue<int>().Should().Be(42);
        node["name"]!.GetValue<string>().Should().Be("Alice");
    }

    [Fact]
    public async Task GetJsonAsync_ReturnsSameCachedNode_OnSubsequentCalls()
    {
        // Arrange
        var buffer = Encoding.UTF8.GetBytes("""{"x":1}""").AsMemory();
        var sut = PayloadContext.FromBuffer(buffer, "application/json");

        // Act
        var node1 = await sut.GetJsonAsync();
        var node2 = await sut.GetJsonAsync();
        var node3 = await sut.GetJsonAsync();

        // Assert — same reference (cached)
        node1.Should().BeSameAs(node2);
        node2.Should().BeSameAs(node3);
    }

    [Fact]
    public async Task GetJsonAsync_ThrowsPayloadAccessViolation_WhenNotJson()
    {
        // Arrange
        var sut = PayloadContext.FromBuffer(
            Encoding.UTF8.GetBytes("<xml/>").AsMemory(),
            "application/xml");

        // Act
        var act = async () => await sut.GetJsonAsync();

        // Assert
        await act.Should().ThrowAsync<PayloadAccessViolationException>();
    }

    // ──────────────────────────────────────────────────────────────
    // Zero-double-serialization
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task FlushAsync_SerializesJsonNode_ExactlyOnce_WhenMutated()
    {
        // Arrange
        var original = """{"name":"Bob"}""";
        var sut = PayloadContext.FromBuffer(
            Encoding.UTF8.GetBytes(original).AsMemory(),
            "application/json");

        // Act — get node, mutate in-place
        var node = await sut.GetJsonAsync() as JsonObject;
        node!["age"] = 30;

        // SetJsonAsync to mark dirty
        await sut.SetJsonAsync(node);
        var flushed = await sut.FlushAsync();

        // Assert — contains mutation
        var result = JsonDocument.Parse(flushed.ToArray());
        result.RootElement.GetProperty("name").GetString().Should().Be("Bob");
        result.RootElement.GetProperty("age").GetInt32().Should().Be(30);
    }

    [Fact]
    public async Task FlushAsync_ReturnsOriginalBuffer_WhenBodyNotMutated()
    {
        // Arrange
        var original = Encoding.UTF8.GetBytes("""{"x":1}""").AsMemory();
        var sut = PayloadContext.FromBuffer(original, "application/json");

        // Act — do not get json or mutate
        var flushed = await sut.FlushAsync();

        // Assert — same bytes returned
        flushed.ToArray().Should().Equal(original.ToArray());
    }

    // ──────────────────────────────────────────────────────────────
    // Streaming
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsStreaming_IsTrue_ForOctetStream()
    {
        var sut = new PayloadContext(null, "application/octet-stream", true);
        sut.IsStreaming.Should().BeTrue();
    }

    [Fact]
    public void IsStreaming_IsTrue_ForMultipart()
    {
        var sut = new PayloadContext(null, "multipart/form-data; boundary=xyz", true);
        sut.IsStreaming.Should().BeTrue();
    }

    [Fact]
    public async Task GetBufferAsync_ThrowsPayloadAccessViolation_ForStreamingPayload()
    {
        var sut = new PayloadContext(null, "application/octet-stream", true);

        var act = async () => await sut.GetBufferAsync();

        await act.Should().ThrowAsync<PayloadAccessViolationException>();
    }

    // ──────────────────────────────────────────────────────────────
    // No-body
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void HasBody_IsFalse_WhenNoBody()
    {
        var sut = new PayloadContext(null, null, false);
        sut.HasBody.Should().BeFalse();
        sut.IsJson.Should().BeFalse();
        sut.IsStreaming.Should().BeFalse();
    }
}
