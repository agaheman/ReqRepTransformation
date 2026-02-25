using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Abstractions;

/// <summary>
/// Resolves the <see cref="TransformationDetail"/> for each incoming request.
///
/// Implementation contract:
///   1. Load a list of <see cref="RouteTransformerEntry"/> records — typically from a DB —
///      via <c>GetCurrentRouteTransformers(method, path)</c>.
///   2. For each entry, resolve the <c>ITransformer</c> keyed service using
///      <see cref="RouteTransformerEntry.TransformerKey"/> and pass
///      <see cref="RouteTransformerEntry.ParamsJson"/> to it.
///   3. Build and return a <see cref="TransformationDetail"/>.
///
/// Cache aggressively by method + normalized path (replace numeric/GUID segments with {id}).
/// </summary>
public interface ITransformationDetailProvider
{
    ValueTask<TransformationDetail> GetTransformationDetailAsync(
        IMessageContext context,
        CancellationToken ct = default);
}
