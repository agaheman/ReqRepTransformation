namespace ReqRepTransformation.Core.Models;

/// <summary>
/// Represents a single transformer registration on a route, as loaded from the database.
///
/// This is the bridge between the persistence layer and the DI container:
///   - <see cref="TransformerKey"/> is used to resolve <c>ITransformer</c> from the keyed DI container.
///   - <see cref="ParamsJson"/> carries the JSON configuration that the transformer reads at runtime.
///   - <see cref="Order"/> determines the execution sequence within the pipeline side.
///   - <see cref="Side"/> indicates whether this runs on the request or response leg.
///
/// Providers (e.g. <c>SampleTransformationDetailProvider</c>) build a list of these records —
/// one per DB row — and pass them to <c>TransformationDetailBuilder</c> which resolves the
/// actual <c>ITransformer</c> instances from DI.
/// </summary>
public sealed record RouteTransformerEntry
{
    /// <summary>
    /// Keyed service registration key for resolving <c>ITransformer</c> from DI.
    /// Must match a value registered via <c>AddKeyedTransient&lt;ITransformer, ConcreteTransformer&gt;(key)</c>.
    /// See <c>TransformerKeys</c> in <c>ReqRepTransformation.BuiltInTransformers</c> for built-in constants.
    /// </summary>
    public required string TransformerKey { get; init; }

    /// <summary>
    /// JSON-serialised configuration parameters for this transformer.
    /// Loaded from a database column (e.g. JSONB or TEXT).
    /// Null or empty JSON = use transformer defaults.
    /// </summary>
    public string? ParamsJson { get; init; }

    /// <summary>
    /// Execution order within the pipeline side. Sorted ASC by <c>PipelineExecutor</c>.
    /// Convention: use multiples of 10 (10, 20, 30...).
    /// </summary>
    public int Order { get; init; }

    /// <summary>Which side of the pipeline this transformer executes on.</summary>
    public TransformerSide Side { get; init; } = TransformerSide.Request;

    /// <summary>
    /// Convenience factory for fluent construction.
    /// </summary>
    public static RouteTransformerEntry Create(
        string transformerKey,
        TransformerSide side,
        int order,
        string? paramsJson = null)
        => new()
        {
            TransformerKey = transformerKey,
            Side           = side,
            Order          = order,
            ParamsJson     = paramsJson
        };
}

/// <summary>Which side of the HTTP exchange the transformer executes on.</summary>
public enum TransformerSide
{
    Request  = 0,
    Response = 1
}
