using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Pipeline;

public interface IMessageTransformationPipeline
{
    ValueTask ExecuteRequestAsync(IMessageContext context, TransformationDetail detail, CancellationToken ct = default);
    ValueTask ExecuteResponseAsync(IMessageContext context, TransformationDetail detail, CancellationToken ct = default);
}

public sealed class TransformationPipeline : IMessageTransformationPipeline
{
    private readonly PipelineExecutor _executor;

    public TransformationPipeline(PipelineExecutor executor) => _executor = executor;

    public ValueTask ExecuteRequestAsync(IMessageContext context, TransformationDetail detail, CancellationToken ct = default)
        => _executor.ExecuteRequestAsync(context, detail);

    public ValueTask ExecuteResponseAsync(IMessageContext context, TransformationDetail detail, CancellationToken ct = default)
        => _executor.ExecuteResponseAsync(context, detail);
}
