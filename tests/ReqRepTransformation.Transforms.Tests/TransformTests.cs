using System.Text.Json.Nodes;
using FluentAssertions;
using ReqRepTransformation.BuiltInTransformers.Address;
using ReqRepTransformation.BuiltInTransformers.Auth;
using ReqRepTransformation.BuiltInTransformers.Body;
using ReqRepTransformation.BuiltInTransformers.Headers;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Core.Tests.Fakes;
using Xunit;

namespace ReqRepTransformation.BuiltInTransformers.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Helper: builds a transformer, calls Configure, and returns it ready to use.
// ─────────────────────────────────────────────────────────────────────────────
file static class TransformerFactory
{
    public static T Build<T>(string paramsJson = "{}") where T : class, Core.Abstractions.ITransformer, new()
    {
        var t = new T();
        t.Configure(new TransformerParams(paramsJson));
        return t;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Header Transformers
// ─────────────────────────────────────────────────────────────────────────────

public sealed class AddHeaderTransformerTests
{
    [Fact]
    public async Task SetsHeader_WhenOverwriteTrue()
    {
        var t   = TransformerFactory.Build<AddHeaderTransformer>("""{"key":"X-Foo","value":"bar","overwrite":true}""");
        var ctx = MessageContextFake.Create();
        await t.ApplyAsync(ctx, default);
        ctx.Headers.Get("X-Foo").Should().Be("bar");
    }

    [Fact]
    public async Task DoesNotOverwrite_WhenOverwriteFalse()
    {
        var t   = TransformerFactory.Build<AddHeaderTransformer>("""{"key":"X-Foo","value":"new","overwrite":false}""");
        var ctx = MessageContextFake.Create();
        ctx.Headers.Set("X-Foo", "existing");
        t.ShouldApply(ctx).Should().BeFalse();
    }

    [Fact]
    public void ThrowsMissingParam_WhenKeyAbsent()
    {
        var t = new AddHeaderTransformer();
        var act = () => t.Configure(new TransformerParams("""{"value":"bar"}"""));
        act.Should().Throw<TransformerParamsMissingException>().Which.ParamKey.Should().Be("key");
    }
}

public sealed class RemoveHeaderTransformerTests
{
    [Fact]
    public async Task RemovesHeader_WhenPresent()
    {
        var t   = TransformerFactory.Build<RemoveHeaderTransformer>("""{"key":"X-Remove-Me"}""");
        var ctx = MessageContextFake.Create();
        ctx.Headers.Set("X-Remove-Me", "value");
        await t.ApplyAsync(ctx, default);
        ctx.Headers.Contains("X-Remove-Me").Should().BeFalse();
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenHeaderAbsent()
    {
        var t   = TransformerFactory.Build<RemoveHeaderTransformer>("""{"key":"X-Missing"}""");
        var ctx = MessageContextFake.Create();
        t.ShouldApply(ctx).Should().BeFalse();
    }
}

public sealed class RenameHeaderTransformerTests
{
    [Fact]
    public async Task MovesValue_ToNewKey()
    {
        var t   = TransformerFactory.Build<RenameHeaderTransformer>("""{"fromKey":"X-Old","toKey":"X-New"}""");
        var ctx = MessageContextFake.Create();
        ctx.Headers.Set("X-Old", "value");
        await t.ApplyAsync(ctx, default);
        ctx.Headers.Get("X-New").Should().Be("value");
        ctx.Headers.Contains("X-Old").Should().BeFalse();
    }
}

public sealed class CorrelationIdTransformerTests
{
    [Fact]
    public async Task InjectsCorrelationId_WhenAbsent()
    {
        var t   = TransformerFactory.Build<CorrelationIdTransformer>("""{"headerName":"X-Correlation-Id"}""");
        var ctx = MessageContextFake.Create();
        await t.ApplyAsync(ctx, default);
        ctx.Headers.Get("X-Correlation-Id").Should().HaveLength(32); // Guid N format
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenAlreadyPresent()
    {
        var t   = TransformerFactory.Build<CorrelationIdTransformer>("""{}""");
        var ctx = MessageContextFake.Create();
        ctx.Headers.Set("X-Correlation-Id", "existing");
        t.ShouldApply(ctx).Should().BeFalse();
    }

    [Fact]
    public async Task UsesCustomHeaderName_FromParams()
    {
        var t   = TransformerFactory.Build<CorrelationIdTransformer>("""{"headerName":"X-Custom-Correlation"}""");
        var ctx = MessageContextFake.Create();
        await t.ApplyAsync(ctx, default);
        ctx.Headers.Contains("X-Custom-Correlation").Should().BeTrue();
    }
}

public sealed class RemoveInternalResponseHeadersTransformerTests
{
    [Fact]
    public async Task RemovesAllListedHeaders()
    {
        var t   = TransformerFactory.Build<RemoveInternalResponseHeadersTransformer>(
            """{"headers":"Server|X-Powered-By"}""");
        var ctx = MessageContextFake.Create(side: Core.Models.MessageSide.Response);
        ctx.Headers.Set("Server", "nginx");
        ctx.Headers.Set("X-Powered-By", "ASP.NET");
        await t.ApplyAsync(ctx, default);
        ctx.Headers.Contains("Server").Should().BeFalse();
        ctx.Headers.Contains("X-Powered-By").Should().BeFalse();
    }

    [Fact]
    public async Task UsesDefaults_WhenNoHeadersParam()
    {
        var t   = TransformerFactory.Build<RemoveInternalResponseHeadersTransformer>("{}");
        var ctx = MessageContextFake.Create(side: Core.Models.MessageSide.Response);
        ctx.Headers.Set("Server", "nginx");
        await t.ApplyAsync(ctx, default);
        ctx.Headers.Contains("Server").Should().BeFalse();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Address Transformers
// ─────────────────────────────────────────────────────────────────────────────

public sealed class PathPrefixRewriteTransformerTests
{
    [Fact]
    public async Task RewritesPath_WhenPrefixMatches()
    {
        var t   = TransformerFactory.Build<PathPrefixRewriteTransformer>(
            """{"fromPrefix":"/api/v1","toPrefix":"/internal/v1"}""");
        var ctx = MessageContextFake.Create(uri: new Uri("http://host/api/v1/users/123"));
        await t.ApplyAsync(ctx, default);
        ctx.Address.AbsolutePath.Should().Be("/internal/v1/users/123");
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenPrefixDoesNotMatch()
    {
        var t   = TransformerFactory.Build<PathPrefixRewriteTransformer>(
            """{"fromPrefix":"/api/v2","toPrefix":"/v2"}""");
        var ctx = MessageContextFake.Create(uri: new Uri("http://host/api/v1/test"));
        t.ShouldApply(ctx).Should().BeFalse();
    }
}

public sealed class AddQueryParamTransformerTests
{
    [Fact]
    public async Task AppendsParam_ToExistingQuery()
    {
        var t   = TransformerFactory.Build<AddQueryParamTransformer>(
            """{"key":"api-version","value":"2024-01"}""");
        var ctx = MessageContextFake.Create(uri: new Uri("http://host/test?existing=1"));
        await t.ApplyAsync(ctx, default);
        ctx.Address.Query.Should().Contain("api-version=2024-01");
        ctx.Address.Query.Should().Contain("existing=1");
    }
}

public sealed class MethodOverrideTransformerTests
{
    [Fact]
    public async Task OverridesMethod_WhenConditionMet()
    {
        var t   = TransformerFactory.Build<MethodOverrideTransformer>(
            """{"newMethod":"PATCH","onlyIfCurrentMethod":"PUT"}""");
        var ctx = MessageContextFake.Create();
        ctx.Method = "PUT";
        await t.ApplyAsync(ctx, default);
        ctx.Method.Should().Be("PATCH");
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenCurrentMethodDoesNotMatch()
    {
        var t   = TransformerFactory.Build<MethodOverrideTransformer>(
            """{"newMethod":"PATCH","onlyIfCurrentMethod":"PUT"}""");
        var ctx = MessageContextFake.Create();
        ctx.Method = "POST";
        t.ShouldApply(ctx).Should().BeFalse();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// JSON Body Transformers
// ─────────────────────────────────────────────────────────────────────────────

public sealed class JsonFieldAddTransformerTests
{
    [Fact]
    public async Task AddsField_ToJsonBody()
    {
        var t   = TransformerFactory.Build<JsonFieldAddTransformer>(
            """{"fieldName":"status","value":"\"active\"","overwrite":true}""");
        var json = JsonNode.Parse("""{"id":1}""")!;
        var ctx  = MessageContextFake.CreateWithJson(json);
        await t.ApplyAsync(ctx, default);
        var result = await ctx.Payload.GetJsonAsync();
        result!["status"]!.GetValue<string>().Should().Be("active");
    }

    [Fact]
    public async Task DoesNotOverwrite_WhenOverwriteFalse()
    {
        var t   = TransformerFactory.Build<JsonFieldAddTransformer>(
            """{"fieldName":"status","value":"\"new\"","overwrite":false}""");
        var json = JsonNode.Parse("""{"status":"existing"}""")!;
        var ctx  = MessageContextFake.CreateWithJson(json);
        await t.ApplyAsync(ctx, default);
        var result = await ctx.Payload.GetJsonAsync();
        result!["status"]!.GetValue<string>().Should().Be("existing");
    }
}

public sealed class JsonFieldRemoveTransformerTests
{
    [Fact]
    public async Task RemovesField_WhenPresent()
    {
        var t   = TransformerFactory.Build<JsonFieldRemoveTransformer>("""{"fieldName":"_internal"}""");
        var json = JsonNode.Parse("""{"id":1,"_internal":"secret"}""")!;
        var ctx  = MessageContextFake.CreateWithJson(json);
        await t.ApplyAsync(ctx, default);
        var result = await ctx.Payload.GetJsonAsync();
        result!.AsObject().ContainsKey("_internal").Should().BeFalse();
    }
}

public sealed class JsonFieldRenameTransformerTests
{
    [Fact]
    public async Task RenamesField_CopyAndRemove()
    {
        var t   = TransformerFactory.Build<JsonFieldRenameTransformer>(
            """{"fromName":"userId","toName":"user_id"}""");
        var json = JsonNode.Parse("""{"userId":42}""")!;
        var ctx  = MessageContextFake.CreateWithJson(json);
        await t.ApplyAsync(ctx, default);
        var result = await ctx.Payload.GetJsonAsync();
        result!.AsObject().ContainsKey("userId").Should().BeFalse();
        result!["user_id"]!.GetValue<int>().Should().Be(42);
    }
}

public sealed class JsonGatewayMetadataTransformerTests
{
    [Fact]
    public async Task InjectsGatewayObject()
    {
        var t   = TransformerFactory.Build<JsonGatewayMetadataTransformer>("""{"version":"2.5"}""");
        var json = JsonNode.Parse("""{"order":"ABC"}""")!;
        var ctx  = MessageContextFake.CreateWithJson(json);
        await t.ApplyAsync(ctx, default);
        var result = await ctx.Payload.GetJsonAsync();
        result!["_gateway"].Should().NotBeNull();
        result!["_gateway"]!["version"]!.GetValue<string>().Should().Be("2.5");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Auth / JWT Transformers
// ─────────────────────────────────────────────────────────────────────────────

public sealed class JwtForwardTransformerTests
{
    [Fact]
    public void ShouldApply_ReturnsTrue_WhenAuthPresent()
    {
        var t   = TransformerFactory.Build<JwtForwardTransformer>("{}");
        var ctx = MessageContextFake.Create();
        ctx.Headers.Set("Authorization", "Bearer token");
        t.ShouldApply(ctx).Should().BeTrue();
    }

    [Fact]
    public void ShouldApply_ReturnsFalse_WhenAuthAbsent()
    {
        var t   = TransformerFactory.Build<JwtForwardTransformer>("{}");
        var ctx = MessageContextFake.Create();
        t.ShouldApply(ctx).Should().BeFalse();
    }
}

public sealed class StripAuthorizationTransformerTests
{
    [Fact]
    public async Task RemovesAuthorizationHeader()
    {
        var t   = TransformerFactory.Build<StripAuthorizationTransformer>("{}");
        var ctx = MessageContextFake.Create();
        ctx.Headers.Set("Authorization", "Bearer token");
        await t.ApplyAsync(ctx, default);
        ctx.Headers.Contains("Authorization").Should().BeFalse();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TransformerParams unit tests
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TransformerParamsTests
{
    [Fact]
    public void GetRequiredString_Throws_WhenKeyAbsent()
    {
        var p   = new TransformerParams("{}");
        var act = () => p.GetRequiredString("missing");
        act.Should().Throw<TransformerParamsMissingException>();
    }

    [Fact]
    public void GetBool_ReturnsFalse_ByDefault()
    {
        var p = new TransformerParams("{}");
        p.GetBool("flag").Should().BeFalse();
    }

    [Fact]
    public void GetBool_ParsesStringTrue()
    {
        var p = new TransformerParams("""{"flag":"true"}""");
        p.GetBool("flag").Should().BeTrue();
    }

    [Fact]
    public void GetPairMap_ParsesPipeSeparatedPairs()
    {
        var p   = new TransformerParams("""{"claimMap":"sub=X-User-Id|email=X-Email"}""");
        var map = p.GetPairMap("claimMap");
        map["sub"].Should().Be("X-User-Id");
        map["email"].Should().Be("X-Email");
    }

    [Fact]
    public void GetStringList_ReturnsSplitValues()
    {
        var p    = new TransformerParams("""{"headers":"Server|X-Powered-By|X-Foo"}""");
        var list = p.GetStringList("headers");
        list.Should().Equal("Server", "X-Powered-By", "X-Foo");
    }

    [Fact]
    public void Empty_ReturnsDefaultValues()
    {
        var p = TransformerParams.Empty;
        p.GetString("any").Should().BeNull();
        p.GetBool("any").Should().BeFalse();
        p.GetInt("any").Should().Be(0);
    }
}
