using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ReqRepTransformation.AspNetCore.DI;
using ReqRepTransformation.BuiltInTransformers;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Infrastructure.Telemetry;
using ReqRepTransformation.Core.Models;
using SampleApiTestApp.Providers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "ReqRepTransformation Sample API", Version = "v1" }));

// ── JWT (demo — use real signing keys in production) ─────────────────────────
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = false,
            ValidateAudience         = false,
            ValidateLifetime         = false,
            ValidateIssuerSigningKey = false,
            SignatureValidator = (token, _) =>
                new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token)
        };
    });

// ── Memory Cache (required by SampleTransformationDetailProvider) ────────────
builder.Services.AddMemoryCache();

// ── 1. Core pipeline + ASP.NET Core middleware ───────────────────────────────
// Resilience (retries, circuit breaking) belongs at the HttpClient layer — use Polly.
builder.Services.AddReqRepTransformationAspNet(options =>
{
    options.DefaultTimeout     = TimeSpan.FromSeconds(5);
    options.DefaultFailureMode = FailureMode.LogAndSkip;
});

// ── 2. All 22 built-in ITransformer implementations as keyed services ────────
//    + TransformationDetailBuilder singleton
builder.Services.AddBuiltInTransformers();

// ── 3. ITransformationDetailProvider (replace with a DB-backed one in prod) ──
builder.Services.AddSingleton<ITransformationDetailProvider, SampleTransformationDetailProvider>();

// ── OpenTelemetry ────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("SampleApiTestApp", serviceVersion: "1.0"))
    .WithTracing(tracing => tracing
        .AddSource(TelemetryConstants.ActivitySourceName)   // "ReqRepTransformation"
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// After auth (JWT available to transforms), before MapControllers
app.UseReqRepTransformation();

app.MapControllers();
app.Run();
