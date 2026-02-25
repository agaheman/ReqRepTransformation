using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Abstractions;

/// <summary>
/// Single point of truth for resolving which transformations apply to a given request.
/// Implementations may load configuration from appsettings.json, a database, Redis cache,
/// feature flags, or any other source. The pipeline calls this exactly once per request.
///
/// Implementations are expected to cache aggressively (by method + path) to avoid
/// repeated I/O on every request.
/// </summary>
public interface ITransformationDetailProvider
{
    /// <summary>
    /// Resolves the transformation detail for the given message context.
    /// Called at the very start of the pipeline, before any transform executes.
    /// </summary>
    /// <param name="context">The incoming message context (method, path, headers available).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="TransformationDetail"/> describing which transforms to apply,
    /// in what order, with what timeout and failure mode.
    /// Return <see cref="TransformationDetail.Empty"/> to pass through with no transforms.
    /// </returns>
    ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context,
        CancellationToken ct = default);
}
