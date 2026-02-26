using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Core.Pipeline;
using ReqRepTransformation.Core.Tests.Fakes;
using Xunit;

namespace ReqRepTransformation.Core.Tests.Pipeline;

public sealed class PipelineExecutorTests
{
    // ── Factory helpers ───────────────────────────────────────────────────────

    private static PipelineExecutor CreateExecutor(
        FailureMode failureMode = FailureMode.LogAndSkip)
    {
        var options = Options.Create(new PipelineOptions { DefaultFailureMode = failureMode });
        return new PipelineExecutor(options, NullLogger<PipelineExecutor>.Instance);
    }

    private static TransformationDetail RequestDetail(
        IEnumerable<TransformEntry> entries,
        FailureMode mode = FailureMode.LogAndSkip) => new()
    {
        RequestTransformations = entries.ToArray(),
        FailureMode            = mode,
        HasExplicitFailureMode = true
    };

    // ── Ordering ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_ExecutesTransforms_InAscendingOrder()
    {
        var executor  = CreateExecutor();
        var context   = MessageContextFake.Create();
        var callOrder = new List<int>();

        var detail = RequestDetail(new[]
        {
            TransformEntry.At(30, new TrackingTransformer(3, callOrder)),
            TransformEntry.At(10, new TrackingTransformer(1, callOrder)),
            TransformEntry.At(20, new TrackingTransformer(2, callOrder))
        });

        await executor.ExecuteRequestAsync(context, detail);

        callOrder.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ExecuteRequestAsync_PreservesInsertionOrder_ForTiedOrders()
    {
        var executor  = CreateExecutor();
        var context   = MessageContextFake.Create();
        var callOrder = new List<int>();

        var detail = RequestDetail(new[]
        {
            TransformEntry.At(10, new TrackingTransformer(1, callOrder)),
            TransformEntry.At(10, new TrackingTransformer(2, callOrder)),
            TransformEntry.At(20, new TrackingTransformer(3, callOrder))
        });

        await executor.ExecuteRequestAsync(context, detail);

        callOrder[2].Should().Be(3);
    }

    // ── ShouldApply ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_SkipsTransform_WhenShouldApplyFalse()
    {
        var executor = CreateExecutor();
        var context  = MessageContextFake.Create();
        var called   = false;

        var detail = RequestDetail(new[]
        {
            TransformEntry.At(10, new ConditionalTransformer(
                shouldApply: _ => false,
                apply: _ => { called = true; return ValueTask.CompletedTask; }))
        });

        await executor.ExecuteRequestAsync(context, detail);

        called.Should().BeFalse();
    }

    // ── FailureMode.LogAndSkip ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_ContinuesAfterFailure_WhenLogAndSkip()
    {
        var executor     = CreateExecutor(failureMode: FailureMode.LogAndSkip);
        var context      = MessageContextFake.Create();
        var secondCalled = false;

        var detail = RequestDetail(new[]
        {
            TransformEntry.At(10, new ThrowingTransformer("fail-first")),
            TransformEntry.At(20, new ConditionalTransformer(
                shouldApply: _ => true,
                apply: _ => { secondCalled = true; return ValueTask.CompletedTask; }))
        }, FailureMode.LogAndSkip);

        await FluentActions
            .Awaiting(() => executor.ExecuteRequestAsync(context, detail).AsTask())
            .Should().NotThrowAsync();

        secondCalled.Should().BeTrue();
    }

    // ── FailureMode.StopPipeline ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_Throws_WhenStopPipeline()
    {
        var executor = CreateExecutor();
        var context  = MessageContextFake.Create();

        var detail = RequestDetail(new[]
        {
            TransformEntry.At(10, new ThrowingTransformer("stopper"))
        }, FailureMode.StopPipeline);

        await FluentActions
            .Awaiting(() => executor.ExecuteRequestAsync(context, detail).AsTask())
            .Should().ThrowAsync<TransformationException>()
            .WithMessage("*stopper*");
    }

    // ── PipelineOptions global fallback ───────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_UsesGlobalDefaultFailureMode_WhenNotExplicit()
    {
        var executor = new PipelineExecutor(
            Options.Create(new PipelineOptions { DefaultFailureMode = FailureMode.StopPipeline }),
            NullLogger<PipelineExecutor>.Instance);

        var context = MessageContextFake.Create();
        var detail  = new TransformationDetail
        {
            RequestTransformations = new[] { TransformEntry.At(10, new ThrowingTransformer("x")) },
            HasExplicitFailureMode = false
        };

        await FluentActions
            .Awaiting(() => executor.ExecuteRequestAsync(context, detail).AsTask())
            .Should().ThrowAsync<TransformationException>();
    }

    // ── Empty pipeline ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_DoesNothing_WhenEmpty()
    {
        var executor = CreateExecutor();
        var context  = MessageContextFake.Create();

        await FluentActions
            .Awaiting(() => executor.ExecuteRequestAsync(context, TransformationDetail.Empty).AsTask())
            .Should().NotThrowAsync();
    }

    // ── Configure / TransformerParams round-trip ──────────────────────────────

    [Fact]
    public async Task ExecuteRequestAsync_ReadsParamsJson_AfterConfigure()
    {
        var executor     = CreateExecutor();
        var context      = MessageContextFake.Create();
        string? captured = null;

        var transformer = new CapturingTransformer(v => captured = v);
        transformer.Configure(new TransformerParams("""{"headerName":"X-Test-Header"}"""));

        var detail = RequestDetail(new[] { TransformEntry.At(10, transformer) });

        await executor.ExecuteRequestAsync(context, detail);

        captured.Should().Be("X-Test-Header");
    }

    // ── Issue 1: compile-time payload discipline ──────────────────────────────
    // IBufferTransformer.ApplyAsync receives IBufferMessageContext — only buffered
    // payload methods (GetJsonAsync, GetBufferAsync) exist on that type.
    // IStreamTransformer.ApplyAsync receives IStreamMessageContext — only
    // GetPipeReaderAsync exists on that type.
    // The typed dispatch in PipelineExecutor.DispatchApplyAsync enforces this.

    [Fact]
    public async Task ExecuteRequestAsync_DispatchesTypedBufferContext_ToBufferTransformer()
    {
        var executor  = CreateExecutor();
        var context   = MessageContextFake.Create();
        IBufferMessageContext? received = null;

        var transformer = new ContextCapturingTransformer(ctx => received = ctx);
        var detail      = RequestDetail(new[] { TransformEntry.At(10, transformer) });

        await executor.ExecuteRequestAsync(context, detail);

        // Transformer received a narrowed IBufferMessageContext — not the raw IMessageContext.
        // Payload is typed as IBufferPayload: GetPipeReaderAsync does not exist on it.
        received.Should().NotBeNull();
        received.Should().BeAssignableTo<IBufferMessageContext>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test doubles
    // All implement IBufferTransformer with the correct typed ApplyAsync signature.
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class TrackingTransformer : IBufferTransformer
    {
        private readonly int       _id;
        private readonly List<int> _order;

        public TrackingTransformer(int id, List<int> order) { _id = id; _order = order; }

        public string Name => $"tracking-{_id}";
        public void Configure(TransformerParams @params) { }
        public bool ShouldApply(IMessageContext _) => true;

        public ValueTask ApplyAsync(IBufferMessageContext _, CancellationToken ct)
        {
            _order.Add(_id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConditionalTransformer : IBufferTransformer
    {
        private readonly Func<IMessageContext, bool>                _shouldApply;
        private readonly Func<IBufferMessageContext, ValueTask>     _apply;

        public ConditionalTransformer(
            Func<IMessageContext, bool>            shouldApply,
            Func<IBufferMessageContext, ValueTask> apply,
            string?                                name = null)
        {
            _shouldApply = shouldApply;
            _apply       = apply;
            Name         = name ?? "conditional";
        }

        public string Name { get; }
        public void Configure(TransformerParams @params) { }
        public bool ShouldApply(IMessageContext ctx) => _shouldApply(ctx);
        public ValueTask ApplyAsync(IBufferMessageContext ctx, CancellationToken ct) => _apply(ctx);
    }

    private sealed class ThrowingTransformer : IBufferTransformer
    {
        public ThrowingTransformer(string name) { Name = name; }

        public string Name { get; }
        public void Configure(TransformerParams @params) { }
        public bool ShouldApply(IMessageContext _) => true;

        public ValueTask ApplyAsync(IBufferMessageContext _, CancellationToken ct)
            => throw new InvalidOperationException($"Transform '{Name}' always fails.");
    }

    private sealed class CapturingTransformer : IBufferTransformer
    {
        private readonly Action<string?> _capture;
        private string? _headerName;

        public CapturingTransformer(Action<string?> capture) => _capture = capture;

        public string Name => "capturing";

        public void Configure(TransformerParams @params)
            => _headerName = @params.GetString("headerName");

        public bool ShouldApply(IMessageContext _) => true;

        public ValueTask ApplyAsync(IBufferMessageContext _, CancellationToken ct)
        {
            _capture(_headerName);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ContextCapturingTransformer : IBufferTransformer
    {
        private readonly Action<IBufferMessageContext> _capture;

        public ContextCapturingTransformer(Action<IBufferMessageContext> capture)
            => _capture = capture;

        public string Name => "context-capturing";
        public void Configure(TransformerParams @params) { }
        public bool ShouldApply(IMessageContext _) => true;

        public ValueTask ApplyAsync(IBufferMessageContext ctx, CancellationToken ct)
        {
            _capture(ctx);
            return ValueTask.CompletedTask;
        }
    }
}
