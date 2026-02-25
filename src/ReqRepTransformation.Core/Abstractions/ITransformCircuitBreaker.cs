namespace ReqRepTransformation.Core.Abstractions;

/// <summary>
/// Per-transform circuit breaker contract.
/// Each transform has an isolated circuit identified by its Name.
/// The default implementation (SlidingWindowCircuitBreaker) uses a lock-free
/// sliding window with Interlocked operations — no Polly dependency.
/// </summary>
public interface ITransformCircuitBreaker
{
    /// <summary>
    /// Returns true if the circuit for the given transform is open (failing fast).
    /// Open circuits cause the transform to be skipped per the configured FailureMode.
    /// </summary>
    bool IsOpen(string transformName);

    /// <summary>Records a successful execution. May close a half-open circuit.</summary>
    void RecordSuccess(string transformName);

    /// <summary>Records a failed execution. May open the circuit if threshold exceeded.</summary>
    void RecordFailure(string transformName);

    /// <summary>Manually resets a circuit to closed state. Used for operational recovery.</summary>
    void Reset(string transformName);

    /// <summary>Returns current state information for observability purposes.</summary>
    CircuitState GetState(string transformName);
}

/// <summary>Circuit breaker state snapshot.</summary>
public enum CircuitState
{
    /// <summary>Circuit is operating normally. All executions proceed.</summary>
    Closed,

    /// <summary>Circuit is open. Executions are fast-failed.</summary>
    Open,

    /// <summary>Circuit is in trial mode. A single execution is allowed to test recovery.</summary>
    HalfOpen
}
