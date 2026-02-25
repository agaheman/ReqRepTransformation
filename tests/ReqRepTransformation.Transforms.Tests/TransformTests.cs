using System.Text.Json.Nodes;
using FluentAssertions;
using ReqRepTransformation.Core.Tests.Fakes;
using ReqRepTransformation.Transforms.Address;
using ReqRepTransformation.Transforms.Auth;
using ReqRepTransformation.Transforms.Body;
using ReqRepTransformation.Transforms.Headers;
using Xunit;

namespace ReqRepTransformation.Transforms.Tests;

// ──────────────────────────────────────────────────────────────────
// Header Transforms
// ──────────────────────────────────────────────────────────────────

public sealed class AddHeaderTransformTests
{
    [Fact]
    public async Task ApplyAsync_SetsHeader_WithGivenValue()
    {
        var ctx = MessageContextFake.Create();
        var sut = new AddHeaderTransform("X-Custom", "test-value");

        await sut.ApplyAsync(ctx, default);

        ctx.Headers.Get("X-Custom").Should().Be("test-value");
    }

    [Fact]
    public async Task ApplyAsync_Overwrites_ExistingHeader_WhenOverwriteTrue()
    {
        var ctx = MessageContextFake.Create(headers: new() { ["X-Custom"] = "old" });
        var sut = new AddHeaderTransform("X-Custom", "new", overwrite: true);

        await sut.ApplyAsync(ctx, default);

        ctx.Headers.Get("X-Custom").Should().Be("new");
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenHeaderExists_AndOverwriteFalse()
    {
        var ctx = MessageContextFake.Create(headers: new() { ["X-Custom"] = "existing" });
        var sut = new AddHeaderTransform("X-Custom", "new", overwrite: false);

        sut.ShouldApply(ctx).Should().BeFalse();
    }
}

public sealed class RemoveHeaderTransformTests
{
    [Fact]
    public async Task ApplyAsync_RemovesHeader()
    {
        var ctx = MessageContextFake.Create(headers: new() { ["X-Remove-Me"] = "value" });
        var sut = new RemoveHeaderTransform("X-Remove-Me");

        await sut.ApplyAsync(ctx, default);

        ctx.Headers.Contains("X-Remove-Me").Should().BeFalse();
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenHeaderAbsent()
    {
        var ctx = MessageContextFake.Create();
        var sut = new RemoveHeaderTransform("X-Absent");

        sut.ShouldApply(ctx).Should().BeFalse();
    }
}

public sealed class RenameHeaderTransformTests
{
    [Fact]
    public async Task ApplyAsync_RenamesHeader_CopyAndDelete()
    {
        var ctx = MessageContextFake.Create(headers: new() { ["X-Old-Name"] = "value123" });
        var sut = new RenameHeaderTransform("X-Old-Name", "X-New-Name");

        await sut.ApplyAsync(ctx, default);

        ctx.Headers.Get("X-New-Name").Should().Be("value123");
        ctx.Headers.Contains("X-Old-Name").Should().BeFalse();
    }
}

public sealed class CorrelationIdTransformTests
{
    [Fact]
    public async Task ApplyAsync_AddsCorrelationHeader_WhenMissing()
    {
        var ctx = MessageContextFake.Create();
        var sut = new CorrelationIdTransform();

        await sut.ApplyAsync(ctx, default);

        var header = ctx.Headers.Get("X-Correlation-Id");
        header.Should().NotBeNullOrEmpty();
        header!.Length.Should().Be(32); // Guid N format
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenHeaderAlreadyPresent()
    {
        var ctx = MessageContextFake.Create(headers: new() { ["X-Correlation-Id"] = "existing-id" });
        var sut = new CorrelationIdTransform();

        sut.ShouldApply(ctx).Should().BeFalse();
    }
}

// ──────────────────────────────────────────────────────────────────
// Address Transforms
// ──────────────────────────────────────────────────────────────────

public sealed class PathPrefixRewriteTransformTests
{
    [Fact]
    public async Task ApplyAsync_RewritesPath()
    {
        var ctx = MessageContextFake.Create(path: "/api/v1/users");
        var sut = new PathPrefixRewriteTransform("/api/v1", "/internal");

        await sut.ApplyAsync(ctx, default);

        ctx.Address.AbsolutePath.Should().Be("/internal/users");
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenPathDoesNotMatch()
    {
        var ctx = MessageContextFake.Create(path: "/health");
        var sut = new PathPrefixRewriteTransform("/api/v1", "/internal");

        sut.ShouldApply(ctx).Should().BeFalse();
    }
}

public sealed class AddQueryParamTransformTests
{
    [Fact]
    public async Task ApplyAsync_AddsQueryParam_WhenNoneExist()
    {
        var ctx = MessageContextFake.Create(path: "/api/items");
        var sut = new AddQueryParamTransform("version", "2");

        await sut.ApplyAsync(ctx, default);

        ctx.Address.Query.Should().Contain("version=2");
    }

    [Fact]
    public async Task ApplyAsync_AppendsQueryParam_ToExistingQuery()
    {
        var ctx = MessageContextFake.Create(path: "/api/items?page=1");
        var sut = new AddQueryParamTransform("version", "2");

        await sut.ApplyAsync(ctx, default);

        ctx.Address.Query.Should().Contain("page=1");
        ctx.Address.Query.Should().Contain("version=2");
    }
}

public sealed class MethodOverrideTransformTests
{
    [Fact]
    public async Task ApplyAsync_ChangesMethod()
    {
        var ctx = MessageContextFake.Create(method: "POST");
        var sut = new MethodOverrideTransform("PUT");

        await sut.ApplyAsync(ctx, default);

        ctx.Method.Should().Be("PUT");
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenCurrentMethodDoesNotMatch_OnlyIfFilter()
    {
        var ctx = MessageContextFake.Create(method: "GET");
        var sut = new MethodOverrideTransform("POST", onlyIfCurrentMethod: "DELETE");

        sut.ShouldApply(ctx).Should().BeFalse();
    }
}

// ──────────────────────────────────────────────────────────────────
// JSON Body Transforms
// ──────────────────────────────────────────────────────────────────

public sealed class JsonFieldAddTransformTests
{
    [Fact]
    public async Task ApplyAsync_AddsField_ToJsonBody()
    {
        var node = JsonNode.Parse("""{"name":"Alice"}""")!;
        var ctx = MessageContextFake.CreateWithJson(node);
        var sut = new JsonFieldAddTransform("status", JsonValue.Create("active")!);

        await sut.ApplyAsync(ctx, default);

        var result = await ctx.Payload.GetJsonAsync();
        result!["status"]!.GetValue<string>().Should().Be("active");
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenNotJson()
    {
        var ctx = MessageContextFake.Create(method: "GET");
        var sut = new JsonFieldAddTransform("x", JsonValue.Create(1)!);

        sut.ShouldApply(ctx).Should().BeFalse();
    }
}

public sealed class JsonFieldRemoveTransformTests
{
    [Fact]
    public async Task ApplyAsync_RemovesField_FromJsonBody()
    {
        var node = JsonNode.Parse("""{"name":"Bob","internal_id":"secret-123"}""")!;
        var ctx = MessageContextFake.CreateWithJson(node);
        var sut = new JsonFieldRemoveTransform("internal_id");

        await sut.ApplyAsync(ctx, default);

        var result = await ctx.Payload.GetJsonAsync() as JsonObject;
        result!.ContainsKey("internal_id").Should().BeFalse();
        result["name"]!.GetValue<string>().Should().Be("Bob");
    }
}

public sealed class JsonFieldRenameTransformTests
{
    [Fact]
    public async Task ApplyAsync_RenamesField_InJsonBody()
    {
        var node = JsonNode.Parse("""{"user_id": 99}""")!;
        var ctx = MessageContextFake.CreateWithJson(node);
        var sut = new JsonFieldRenameTransform("user_id", "userId");

        await sut.ApplyAsync(ctx, default);

        var result = await ctx.Payload.GetJsonAsync() as JsonObject;
        result!.ContainsKey("user_id").Should().BeFalse();
        result["userId"]!.GetValue<int>().Should().Be(99);
    }
}

// ──────────────────────────────────────────────────────────────────
// JWT Transforms
// ──────────────────────────────────────────────────────────────────

public sealed class JwtForwardTransformTests
{
    [Fact]
    public void ShouldApply_ReturnsTrue_WhenAuthorizationHeaderPresent()
    {
        var ctx = MessageContextFake.Create(
            headers: new() { ["Authorization"] = "Bearer eyJhbGc..." });
        var sut = new JwtForwardTransform();

        sut.ShouldApply(ctx).Should().BeTrue();
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenAuthorizationHeaderAbsent()
    {
        var ctx = MessageContextFake.Create();
        var sut = new JwtForwardTransform();

        sut.ShouldApply(ctx).Should().BeFalse();
    }
}

public sealed class StripAuthorizationTransformTests
{
    [Fact]
    public async Task ApplyAsync_RemovesAuthorizationHeader()
    {
        var ctx = MessageContextFake.Create(
            headers: new() { ["Authorization"] = "Bearer token" });
        var sut = new StripAuthorizationTransform();

        await sut.ApplyAsync(ctx, default);

        ctx.Headers.Contains("Authorization").Should().BeFalse();
    }
}
