using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Infrastructure.CircuitBreaker;
using ReqRepTransformation.Core.Models;
using Xunit;

namespace ReqRepTransformation.Core.Tests.Infrastructure;

public sealed class SlidingWindowCircuitBreakerTests
{
    private static SlidingWindowCircuitBreaker Create(
        int windowSize = 10,
        double threshold = 0.5,
        TimeSpan? openDuration = null)
    {
        var options = Options.Create(new PipelineOptions
        {
            CircuitBreaker = new CircuitBreakerOptions
            {
                WindowSize = windowSize,
                FailureRatioThreshold = threshold,
                OpenDuration = openDuration ?? TimeSpan.FromSeconds(30)
            }
        });
        return new SlidingWindowCircuitBreaker(options, NullLogger<SlidingWindowCircuitBreaker>.Instance);
    }

    // ──────────────────────────────────────────────────────────────
    // Initial state
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsOpen_IsFalse_Initially()
    {
        var sut = Create();
        sut.IsOpen("my-transform").Should().BeFalse();
    }

    [Fact]
    public void GetState_ReturnsClosed_Initially()
    {
        var sut = Create();
        sut.GetState("my-transform").Should().Be(CircuitState.Closed);
    }

    // ──────────────────────────────────────────────────────────────
    // Opening circuit
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Circuit_Opens_WhenFailureRatioExceedsThreshold()
    {
        var sut = Create(windowSize: 10, threshold: 0.5);
        const string name = "test-transform";

        // Record 5 failures (50% of window) — just at threshold, should open
        for (int i = 0; i < 5; i++)
            sut.RecordFailure(name);

        sut.IsOpen(name).Should().BeTrue();
        sut.GetState(name).Should().Be(CircuitState.Open);
    }

    [Fact]
    public void Circuit_StaysClosed_WhenFailureRatioBelow_Threshold()
    {
        var sut = Create(windowSize: 10, threshold: 0.6);
        const string name = "test-transform";

        // 5 failures = 50% — below 60% threshold
        for (int i = 0; i < 5; i++)
            sut.RecordFailure(name);

        sut.IsOpen(name).Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────
    // Recovery
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Circuit_Closes_AfterRecordSuccess_FromHalfOpen()
    {
        var sut = Create(windowSize: 4, threshold: 0.5, openDuration: TimeSpan.FromMilliseconds(1));
        const string name = "recover-transform";

        // Open the circuit
        sut.RecordFailure(name);
        sut.RecordFailure(name);

        sut.IsOpen(name).Should().BeTrue();

        // Wait for OpenDuration to elapse → HalfOpen
        Thread.Sleep(5);

        // IsOpen returns false in HalfOpen (allows one trial)
        sut.IsOpen(name).Should().BeFalse();

        // Trial succeeds → circuit closes
        sut.RecordSuccess(name);

        sut.IsOpen(name).Should().BeFalse();
        sut.GetState(name).Should().Be(CircuitState.Closed);
    }

    // ──────────────────────────────────────────────────────────────
    // Manual reset
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClosesOpenCircuit_Immediately()
    {
        var sut = Create(windowSize: 4, threshold: 0.5);
        const string name = "reset-test";

        sut.RecordFailure(name);
        sut.RecordFailure(name);
        sut.IsOpen(name).Should().BeTrue();

        sut.Reset(name);

        sut.IsOpen(name).Should().BeFalse();
        sut.GetState(name).Should().Be(CircuitState.Closed);
    }

    // ──────────────────────────────────────────────────────────────
    // Thread safety
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void RecordSuccess_And_RecordFailure_AreSafe_UnderConcurrency()
    {
        var sut = Create(windowSize: 100, threshold: 0.8);
        const string name = "concurrent-transform";

        var tasks = Enumerable.Range(0, 200).Select(i =>
            Task.Run(() =>
            {
                if (i % 3 == 0) sut.RecordFailure(name);
                else sut.RecordSuccess(name);
            }));

        var act = async () => await Task.WhenAll(tasks);
        act.Should().NotThrowAsync();
    }
}
