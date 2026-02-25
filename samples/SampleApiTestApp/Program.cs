using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ReqRepTransformation.AspNetCore.DI;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Infrastructure.Telemetry;
using ReqRepTransformation.Core.Models;
using SampleApiTestApp.Providers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "ReqRepTransformation Sample API", Version = "v1" });
});

// ── JWT (demo — use real keys in production) ─────────────────────
//builder.Services
//    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
//    .AddJwtBearer(options =>
//    {
//        options.TokenValidationParameters = new TokenValidationParameters
//        {
//            ValidateIssuer           = false,
//            ValidateAudience         = false,
//            ValidateLifetime         = false,
//            ValidateIssuerSigningKey = false,
//            SignatureValidator = (token, _) =>
//                new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token)
//        };
//    });

// ── Memory Cache (used by SampleTransformationDetailProvider) ────
builder.Services.AddMemoryCache();

// ── ReqRepTransformation ─────────────────────────────────────────
builder.Services.AddReqRepTransformationAspNet(options =>
{
    options.DefaultTimeout     = TimeSpan.FromSeconds(15);
    options.DefaultFailureMode = FailureMode.LogAndSkip;   // global fallback
    options.CircuitBreaker     = new CircuitBreakerOptions
    {
        WindowSize            = 20,
        FailureRatioThreshold = 0.50,
        OpenDuration          = TimeSpan.FromSeconds(30)
    };
});

// ── Register your ITransformationDetailProvider ──────────────────
// Replace SampleTransformationDetailProvider with a DB-backed one in production.
builder.Services.AddSingleton<ITransformationDetailProvider, SampleTransformationDetailProvider>();

// ── OpenTelemetry ────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("SampleApiTestApp", serviceVersion: "1.0.0"))
    .WithTracing(tracing => tracing
        .AddSource(TelemetryConstants.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
//app.UseAuthentication();
//app.UseAuthorization();

// After auth (JWT available) — before MapControllers (rewrites affect routing)
app.UseReqRepTransformation();

app.MapControllers();
app.Run();
