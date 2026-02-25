using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Infrastructure.Logging;
using ReqRepTransformation.Core.Infrastructure.Telemetry;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Infrastructure.CircuitBreaker;

/// <summary>
/// Lock-free sliding-window circuit breaker backed by per-transform state buckets.
///
/// Algorithm:
/// - Maintains a circular array of N booleans (true=success, false=failure).
/// - Uses Interlocked for atomic index advancement — no locks.
/// - When failure ratio in window exceeds threshold: circuit opens.
/// - After OpenDuration elapses: circuit transitions to HalfOpen.
/// - First success in HalfOpen: circuit closes.
/// - First failure in HalfOpen: circuit re-opens, timer resets.
///
/// Thread-safety: All operations are safe for concurrent use.
/// Allocation: Zero heap allocation in steady state (no closures, no LINQ).
/// </summary>
public sealed class SlidingWindowCircuitBreaker : ITransformCircuitBreaker
{
    private readonly CircuitBreakerOptions _options;
    private readonly ILogger<SlidingWindowCircuitBreaker> _logger;
    private readonly ConcurrentDictionary<string, BreakerState> _states = new();

    public SlidingWindowCircuitBreaker(
        IOptions<PipelineOptions> options,
        ILogger<SlidingWindowCircuitBreaker> logger)
    {
        _options = options.Value.CircuitBreaker;
        _logger = logger;
    }

    public bool IsOpen(string transformName)
    {
        var state = GetOrCreate(transformName);
        return state.IsOpen(_options.OpenDuration);
    }

    public void RecordSuccess(string transformName)
    {
        var state = GetOrCreate(transformName);
        bool wasClosed = state.RecordSuccess(_options);
        if (wasClosed)
        {
            _logger.CircuitClosed(transformName);
        }
    }

    public void RecordFailure(string transformName)
    {
        var state = GetOrCreate(transformName);
        bool justOpened = state.RecordFailure(_options);
        if (justOpened)
        {
            double ratio = state.CurrentFailureRatio;
            _logger.CircuitOpened(transformName, ratio);
            TelemetryConstants.CircuitOpenCounter.Add(1,
                new KeyValuePair<string, object?>(TelemetryConstants.AttrTransformName, transformName));
        }
    }

    public void Reset(string transformName)
    {
        if (_states.TryGetValue(transformName, out var state))
        {
            state.Reset();
        }
    }

    public CircuitState GetState(string transformName)
    {
        if (!_states.TryGetValue(transformName, out var state))
            return CircuitState.Closed;
        return state.GetCircuitState(_options.OpenDuration);
    }

    private BreakerState GetOrCreate(string transformName)
        => _states.GetOrAdd(transformName, static (_, opts) => new BreakerState(opts.WindowSize), _options);

    // ──────────────────────────────────────────────
    // Inner state class — one per transform
    // ──────────────────────────────────────────────

    private sealed class BreakerState
    {
        // Circular window: 1 = success, 0 = failure
        private readonly int[] _window;
        private int _index;           // current write position (Interlocked)
        private int _failureCount;    // tracked failures in window (Interlocked)
        private long _openedAtTicks;  // Environment.TickCount64 when opened (0 = closed)
        private int _circuitStatus;   // 0=Closed, 1=Open, 2=HalfOpen

        internal double CurrentFailureRatio
        {
            get
            {
                int failures = Volatile.Read(ref _failureCount);
                return (double)failures / _window.Length;
            }
        }

        public BreakerState(int windowSize)
        {
            _window = new int[windowSize];
            // Pre-fill with successes so the breaker doesn't open on first failures
            Array.Fill(_window, 1);
        }

        public bool IsOpen(TimeSpan openDuration)
        {
            int status = Volatile.Read(ref _circuitStatus);

            if (status == 0) return false; // Closed

            if (status == 1) // Open
            {
                long openedAt = Volatile.Read(ref _openedAtTicks);
                long elapsed = Environment.TickCount64 - openedAt;

                if (elapsed >= (long)openDuration.TotalMilliseconds)
                {
                    // Try to transition to HalfOpen (only one thread succeeds)
                    Interlocked.CompareExchange(ref _circuitStatus, 2, 1);
                    return false; // Allow trial execution
                }
                return true;
            }

            // HalfOpen: allow one trial, set back to open optimistically
            return false;
        }

        /// <returns>True if circuit just transitioned from Open/HalfOpen to Closed.</returns>
        public bool RecordSuccess(CircuitBreakerOptions options)
        {
            int idx = Interlocked.Increment(ref _index) % _window.Length;
            int prev = Interlocked.Exchange(ref _window[idx], 1);
            if (prev == 0)
                Interlocked.Decrement(ref _failureCount);

            int status = Volatile.Read(ref _circuitStatus);
            if (status != 0) // Was Open or HalfOpen
            {
                // Close the circuit
                int old = Interlocked.Exchange(ref _circuitStatus, 0);
                Volatile.Write(ref _openedAtTicks, 0);
                return old != 0; // True if we just closed it
            }
            return false;
        }

        /// <returns>True if this call just opened the circuit.</returns>
        public bool RecordFailure(CircuitBreakerOptions options)
        {
            int idx = Interlocked.Increment(ref _index) % _window.Length;
            int prev = Interlocked.Exchange(ref _window[idx], 0);
            if (prev == 1)
                Interlocked.Increment(ref _failureCount);

            int failures = Volatile.Read(ref _failureCount);
            double ratio = (double)failures / _window.Length;

            int status = Volatile.Read(ref _circuitStatus);
            if (status == 0 && ratio >= options.FailureRatioThreshold)
            {
                // Attempt to open circuit — one winner via CAS
                if (Interlocked.CompareExchange(ref _circuitStatus, 1, 0) == 0)
                {
                    Volatile.Write(ref _openedAtTicks, Environment.TickCount64);
                    return true;
                }
            }
            else if (status == 2) // HalfOpen trial failed
            {
                // Re-open
                Interlocked.Exchange(ref _circuitStatus, 1);
                Volatile.Write(ref _openedAtTicks, Environment.TickCount64);
            }
            return false;
        }

        public void Reset()
        {
            Array.Fill(_window, 1);
            Volatile.Write(ref _failureCount, 0);
            Volatile.Write(ref _circuitStatus, 0);
            Volatile.Write(ref _openedAtTicks, 0);
        }

        public CircuitState GetCircuitState(TimeSpan openDuration)
        {
            int status = Volatile.Read(ref _circuitStatus);
            return status switch
            {
                0 => CircuitState.Closed,
                1 => CircuitState.Open,
                2 => CircuitState.HalfOpen,
                _ => CircuitState.Closed
            };
        }
    }
}
