using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ReqRepTransformation.AspNetCore.DI;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Transforms.Body;
using ReqRepTransformation.Transforms.Headers;
using Xunit;

namespace ReqRepTransformation.AspNetCore.Tests;

public sealed class GatewayTransformMiddlewareTests
{
    private static TestServer CreateTestServer(
        ITransformationDetailProvider provider,
        RequestDelegate? endpoint = null)
    {
        var host = new WebHostBuilder()
            .ConfigureServices(s =>
            {
                s.AddReqRepTransformationAspNet();
                s.AddSingleton(provider);
            })
            .Configure(app =>
            {
                app.UseReqRepTransformation();
                app.Run(endpoint ?? (async ctx =>
                {
                    ctx.Response.StatusCode  = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync("""{"result":"ok"}""");
                }));
            });
        return new TestServer(host);
    }

    private static ITransformationDetailProvider ProviderWith(
        TransformEntry[]? request  = null,
        TransformEntry[]? response = null)
    {
        var detail = new TransformationDetail
        {
            RequestTransformations  = request  ?? Array.Empty<TransformEntry>(),
            ResponseTransformations = response ?? Array.Empty<TransformEntry>(),
            FailureMode             = FailureMode.LogAndSkip,
            HasExplicitFailureMode  = true
        };
        var p = Substitute.For<ITransformationDetailProvider>();
        p.GetTransformationDetailAsync(Arg.Any<IMessageContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(detail));
        return p;
    }

    [Fact]
    public async Task Middleware_InjectsCorrelationId_WhenAbsent()
    {
        var provider = ProviderWith(request: new[] { TransformEntry.At(10, new CorrelationIdTransform()) });

        using var server = CreateTestServer(provider, async ctx =>
        {
            var id = ctx.Request.Headers["X-Correlation-Id"].ToString();
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsJsonAsync(new { correlationId = id });
        });

        var response = await server.CreateClient().GetAsync("/api/test");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Middleware_MutatesJsonBody_WithGatewayMetadata()
    {
        var provider = ProviderWith(request: new[] { TransformEntry.At(10, new JsonGatewayMetadataTransform()) });

        using var server = CreateTestServer(provider, async ctx =>
        {
            ctx.Response.StatusCode  = 200;
            ctx.Response.ContentType = "application/json";
            using var reader = new StreamReader(ctx.Request.Body);
            await ctx.Response.WriteAsync(await reader.ReadToEndAsync());
        });

        var req = new StringContent("""{"order":"ABC"}""", Encoding.UTF8, "application/json");
        var res = await server.CreateClient().PostAsync("/api/orders", req);

        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        json.RootElement.TryGetProperty("_gateway", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Middleware_AddsResponseHeader_FromResponseTransform()
    {
        var provider = ProviderWith(
            response: new[] { TransformEntry.At(10, new AddHeaderTransform("X-Gateway-Version", "1.0")) });

        using var server = CreateTestServer(provider);
        var res = await server.CreateClient().GetAsync("/test");

        res.Headers.TryGetValues("X-Gateway-Version", out var vals).Should().BeTrue();
        vals!.First().Should().Be("1.0");
    }

    [Fact]
    public async Task Middleware_PassesThrough_WhenNoTransforms()
    {
        var provider = ProviderWith();
        using var server = CreateTestServer(provider);

        var res  = await server.CreateClient().GetAsync("/health");
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Be("""{"result":"ok"}""");
    }

    [Fact]
    public async Task Middleware_ExecutesRequestTransforms_InOrderAsc()
    {
        var callOrder = new List<int>();

        // Register at Order 30 first, then 10 — middleware must sort ASC
        var t10 = new OrderTrackingTransform(1, callOrder);
        var t30 = new OrderTrackingTransform(3, callOrder);
        var t20 = new OrderTrackingTransform(2, callOrder);

        var provider = ProviderWith(request: new[]
        {
            TransformEntry.At(30, t30),
            TransformEntry.At(10, t10),
            TransformEntry.At(20, t20)
        });

        using var server = CreateTestServer(provider);
        await server.CreateClient().GetAsync("/test");

        callOrder.Should().Equal(1, 2, 3);
    }

    private sealed class OrderTrackingTransform : IBufferTransform
    {
        private readonly int _id;
        private readonly List<int> _list;
        public OrderTrackingTransform(int id, List<int> list) { _id = id; _list = list; }
        public string Name => $"order-tracker-{_id}";
        public bool ShouldApply(IMessageContext _) => true;
        public ValueTask ApplyAsync(IMessageContext _, CancellationToken ct)
        { _list.Add(_id); return ValueTask.CompletedTask; }
    }
}
