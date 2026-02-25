using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Infrastructure.Logging;
using ReqRepTransformation.Core.Infrastructure.Memory;
using ReqRepTransformation.Core.Models;
using ReqRepTransformation.Core.Pipeline;
using ReqRepTransformation.AspNetCore.Adapters;

namespace ReqRepTransformation.AspNetCore.Middleware;

/// <summary>
/// ASP.NET Core middleware that runs the ReqRepTransformation pipeline.
///
/// Request flow:
/// 1. Wrap HttpContext in AspNetRequestMessageContext (IMessageContext adapter).
/// 2. Resolve TransformationDetail via ITransformationDetailProvider.
/// 3. Run request-side transforms (headers, address, body mutations).
/// 4. Swap Response.Body to capture downstream response.
/// 5. Call _next(context) — downstream middleware / endpoint runs.
/// 6. Capture response body bytes.
/// 7. Wrap captured body in AspNetResponseMessageContext.
/// 8. Run response-side transforms.
/// 9. Write final body back to the original Response.Body stream.
///
/// Registration: app.UseReqRepTransformation() — place after UseAuthentication,
/// before UseRouting if address rewrites are needed before routing.
/// </summary>
public sealed class GatewayTransformMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITransformationDetailProvider _detailProvider;
    private readonly IMessageTransformationPipeline _pipeline;
    private readonly ILogger<GatewayTransformMiddleware> _logger;

    public GatewayTransformMiddleware(
        RequestDelegate next,
        ITransformationDetailProvider detailProvider,
        IMessageTransformationPipeline pipeline,
        ILogger<GatewayTransformMiddleware> logger)
    {
        _next = next;
        _detailProvider = detailProvider;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // ── 1. Build request-side adapter ────────────────────────
        var requestContext = new AspNetRequestMessageContext(context);

        // ── 2. Resolve transformation detail ─────────────────────
        TransformationDetail detail;
        try
        {
            detail = await _detailProvider
                .GetTransformationDetailAsync(requestContext, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to resolve TransformationDetail for {Method} {Path}. Passing through without transforms.",
                context.Request.Method,
                context.Request.Path);

            await _next(context).ConfigureAwait(false);
            return;
        }

        _logger.DetailResolved(
            context.Request.Method,
            context.Request.Path,
            detail.RequestTransformations.Count,
            detail.ResponseTransformations.Count);

        // ── 3. Request-side transforms ────────────────────────────
        try
        {
            await _pipeline.ExecuteRequestAsync(requestContext, detail, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (TransformationException ex)
        {
            _logger.LogError(ex,
                "Request pipeline aborted by transform '{Transform}'",
                ex.TransformName);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync(
                $"Gateway error: request transformation failed in '{ex.TransformName}'.",
                context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        // ── 4. Swap Response.Body to capture downstream output ───
        var originalBody = context.Response.Body;

        // We need to flush the final body after transforms, so use a pooled stream
        await using var captureStream = PooledMemoryManager.GetStream("reqrep-response-capture");
        context.Response.Body = captureStream;

        // ── 5. Call downstream ────────────────────────────────────
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            // Always restore original body stream
            context.Response.Body = originalBody;
        }

        // ── 6. Capture response body ──────────────────────────────
        captureStream.Position = 0;
        var responseBodyBuffer = captureStream.ToArray().AsMemory();

        // ── 7. Build response-side adapter ────────────────────────
        var responseContext = new AspNetResponseMessageContext(
            context,
            responseBodyBuffer,
            context.Response.ContentType);

        // ── 8. Response-side transforms ───────────────────────────
        try
        {
            await _pipeline.ExecuteResponseAsync(responseContext, detail, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (TransformationException ex)
        {
            _logger.LogError(ex,
                "Response pipeline error in transform '{Transform}'. Serving original response.",
                ex.TransformName);
            // Fallback: write original captured body
            await WriteBodyAsync(originalBody, responseBodyBuffer, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        // ── 9. Write final body ───────────────────────────────────
        var finalBody = await responseContext
            .GetFinalBodyAsync(context.RequestAborted)
            .ConfigureAwait(false);

        // Update Content-Length if body was mutated
        if (finalBody.Length != responseBodyBuffer.Length)
        {
            context.Response.ContentLength = finalBody.Length;
        }

        await WriteBodyAsync(originalBody, finalBody, context.RequestAborted)
            .ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static async ValueTask WriteBodyAsync(
        Stream destination,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        if (body.Length > 0)
            await destination.WriteAsync(body, ct).ConfigureAwait(false);
    }
}
