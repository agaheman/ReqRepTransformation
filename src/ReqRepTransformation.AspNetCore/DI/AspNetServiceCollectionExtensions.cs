using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ReqRepTransformation.AspNetCore.Middleware;
using ReqRepTransformation.Core.DI;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.AspNetCore.DI;

/// <summary>
/// Extension methods for registering the ASP.NET Core integration.
/// </summary>
public static class AspNetServiceCollectionExtensions
{
    /// <summary>
    /// Registers all ReqRepTransformation services including the ASP.NET Core middleware.
    /// Calls AddReqRepTransformationCore internally — do not call both.
    ///
    /// <para>Usage in Program.cs:</para>
    /// <code>
    /// builder.Services
    ///     .AddReqRepTransformationAspNet(opt => opt.DefaultFailureMode = FailureMode.LogAndSkip)
    ///     .AddSingleton&lt;ITransformationDetailProvider, MyProvider&gt;();
    /// </code>
    /// </summary>
    public static IServiceCollection AddReqRepTransformationAspNet(
        this IServiceCollection services,
        Action<PipelineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddReqRepTransformationCore(configure);

        return services;
    }
}

/// <summary>
/// IApplicationBuilder extensions for middleware registration.
/// </summary>
public static class AspNetApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the ReqRepTransformation middleware to the pipeline.
    ///
    /// <para>Placement guidelines:</para>
    /// <list type="bullet">
    /// <item>After UseAuthentication() / UseAuthorization() if JWT transforms need the token.</item>
    /// <item>Before UseRouting() if path rewrites must be visible to the router.</item>
    /// <item>After UseRouting() if you need route data inside transforms (context.GetRouteData()).</item>
    /// </list>
    /// </summary>
    public static IApplicationBuilder UseReqRepTransformation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<GatewayTransformMiddleware>();
    }
}
