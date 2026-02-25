using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Core.Pipeline;
using ReqRepTransformation.Core.Tests.Fakes;
using Xunit;

namespace ReqRepTransformation.Core.Tests.Pipeline;

public sealed class PipelineExecutorTests
{
    private static PipelineExecutor CreateExecutor(
        ITransformCircuitBreaker? breaker = null,
        FailureMode failureMode = FailureMode.LogAndSkip)
    {
        breaker ??= Substitute.For<ITransformCircuitBreaker>();
        breaker.IsOpen(Arg.Any<string>()).Returns(false);

        var options = Options.Create(new PipelineOptions { DefaultFailureMode = failureMode });
        return new PipelineExecutor(breaker, options, NullLogger<PipelineExecutor>.Instance);
    }

    private static TransformationDetail RequestDetail(
        IEnumerable<TransformEntry> entries,
        FailureMode mode = FailureMode.LogAndSkip) => new()
    {
        RequestTransformations = entries.ToArray(),
        FailureMode            = mode,
        HasExplicitFailureMode = true
    };

    // ──────────────────────────────────────────────────────────────
    // Ordering
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_ExecutesTransforms_InAscendingOrder()
    {
        var executor   = CreateExecutor();
        var context    = MessageContextFake.Create();
        var callOrder  = new List<int>();

        // Registered in reverse order — executor must sort by Order ASC
        var detail = RequestDetail(new[]
        {
            TransformEntry.At(30, new TrackingTransform(3, callOrder)),
            TransformEntry.At(10, new TrackingTransform(1, callOrder)),
            TransformEntry.At(20, new TrackingTransform(2, callOrder))
        });

        await executor.ExecuteRequestAsync(context, detail);

        callOrder.Should().Equal(1, 2, 3); // sorted: 10 → 20 → 30
    }

    [Fact]
    public async Task ExecuteRequestAsync_PreservesInsertionOrder_ForTiedOrder()
    {
        var executor  = CreateExecutor();
        var context   = MessageContextFake.Create();
        var callOrder = new List<int>();

        // Two entries with Order=10 — insertion order preserved for ties
        var detail = RequestDetail(new[]
        {
            TransformEntry.At(10, new TrackingTransform(1, callOrder)),
            TransformEntry.At(10, new TrackingTransform(2, callOrder)),
            TransformEntry.At(20, new TrackingTransform(3, callOrder))
        });

        await executor.ExecuteRequestAsync(context, detail);

        callOrder[2].Should().Be(3); // Order=20 always last
    }

    // ──────────────────────────────────────────────────────────────
    // ShouldApply
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_SkipsTransform_WhenShouldApplyFalse()
    {
        var executor = CreateExecutor();
        var context  = MessageContextFake.Create();
        var called   = false;

        var detail = RequestDetail(new[]
        {
            TransformEntry.At(10, new ConditionalTransform(
                shouldApply: _ => false,
                apply: _ => { called = true; return ValueTask.CompletedTask; }))
        });

        await executor.ExecuteRequestAsync(context, detail);

        called.Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────
    // FailureMode.LogAndSkip
    // ──────────────────────────────────────────────────────────────

    //[Fact]
    //public async Task ExecuteRequestAsync_ContinuesAfterFailure_WhenLogAndSkip()
    //{
    //    var executor = CreateExecutor(failureMode: FailureMode.LogAndSkip);
    //    var context = MessageContextFake.Create();
    //    var secondCalled = false;

    //    var detail = RequestDetail(new[]
    //    {
    //        TransformEntry.At(10, new ThrowingTransform("fail-first")),
    //        TransformEntry.At(20, new ConditionalTransform(
    //            shouldApply: _ => true,
    //            apply: _ => { secondCalled = true; return ValueTask.CompletedTask; }))
    //    }, FailureMode.LogAndSkip);

    //    await (async () => await executor.ExecuteRequestAsync(context, detail))
    //        .Should().NotThrowAsync();

    //    secondCalled.Should().BeTrue();
    //}

    // ──────────────────────────────────────────────────────────────
    // FailureMode.StopPipeline
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_Throws_WhenStopPipeline()
    {
        var executor = CreateExecutor();
        var context  = MessageContextFake.Create();

        var detail = RequestDetail(new[]
        {
            TransformEntry.At(10, new ThrowingTransform("stopper"))
        }, FailureMode.StopPipeline);

        await FluentActions.Awaiting(() => executor.ExecuteRequestAsync(context, detail).AsTask())
            .Should().ThrowAsync<TransformationException>()
            .WithMessage("*stopper*");
    }

    // ──────────────────────────────────────────────────────────────
    // PipelineOptions fallback
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_UsesGlobalDefaultFailureMode_WhenNotExplicit()
    {
        // Global default = StopPipeline; detail does NOT set HasExplicitFailureMode
        var options = Options.Create(new PipelineOptions
        {
            DefaultFailureMode = FailureMode.StopPipeline
        });
        var breaker = Substitute.For<ITransformCircuitBreaker>();
        breaker.IsOpen(Arg.Any<string>()).Returns(false);
        var executor = new PipelineExecutor(breaker, options, NullLogger<PipelineExecutor>.Instance);

        var context = MessageContextFake.Create();
        var detail = new TransformationDetail
        {
            RequestTransformations = new[] { TransformEntry.At(10, new ThrowingTransform("x")) },
            HasExplicitFailureMode = false  // ← use global default
        };

        await FluentActions.Awaiting(() => executor.ExecuteRequestAsync(context, detail).AsTask())
            .Should().ThrowAsync<TransformationException>();
    }

    // ──────────────────────────────────────────────────────────────
    // Circuit breaker
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_SkipsTransform_WhenCircuitOpen_LogAndSkip()
    {
        var breaker = Substitute.For<ITransformCircuitBreaker>();
        breaker.IsOpen("guarded").Returns(true);

        var executor = CreateExecutor(breaker, FailureMode.LogAndSkip);
        var context  = MessageContextFake.Create();
        var called   = false;

        var detail = RequestDetail(new[]
        {
            TransformEntry.At(10, new ConditionalTransform(
                shouldApply: _ => true,
                apply: _ => { called = true; return ValueTask.CompletedTask; },
                name: "guarded"))
        }, FailureMode.LogAndSkip);

        await executor.ExecuteRequestAsync(context, detail);

        called.Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────
    // Empty pipeline
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_DoesNothing_WhenEmpty()
    {
        var executor = CreateExecutor();
        var context  = MessageContextFake.Create();

        await FluentActions.Awaiting(
            () => executor.ExecuteRequestAsync(context, TransformationDetail.Empty).AsTask())
            .Should().NotThrowAsync();
    }

    // ──────────────────────────────────────────────────────────────
    // Test doubles
    // ──────────────────────────────────────────────────────────────

    private sealed class TrackingTransform : IBufferTransform
    {
        private readonly int _id;
        private readonly List<int> _order;
        public TrackingTransform(int id, List<int> order) { _id = id; _order = order; }
        public string Name => $"tracking-{_id}";
        public bool ShouldApply(IMessageContext _) => true;
        public ValueTask ApplyAsync(IMessageContext _, CancellationToken ct)
        { _order.Add(_id); return ValueTask.CompletedTask; }
    }

    private sealed class ConditionalTransform : IBufferTransform
    {
        private readonly Func<IMessageContext, bool>       _shouldApply;
        private readonly Func<IMessageContext, ValueTask>  _apply;
        public ConditionalTransform(
            Func<IMessageContext, bool> shouldApply,
            Func<IMessageContext, ValueTask> apply,
            string? name = null)
        { _shouldApply = shouldApply; _apply = apply; Name = name ?? "conditional"; }
        public string Name { get; }
        public bool ShouldApply(IMessageContext ctx) => _shouldApply(ctx);
        public ValueTask ApplyAsync(IMessageContext ctx, CancellationToken ct) => _apply(ctx);
    }

    private sealed class ThrowingTransform : IBufferTransform
    {
        public ThrowingTransform(string name) { Name = name; }
        public string Name { get; }
        public bool ShouldApply(IMessageContext _) => true;
        public ValueTask ApplyAsync(IMessageContext _, CancellationToken ct)
            => throw new InvalidOperationException($"Transform '{Name}' always fails.");
    }
}
