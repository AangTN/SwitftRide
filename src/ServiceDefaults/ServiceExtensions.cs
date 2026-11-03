using System.Diagnostics;
using System.Security.Cryptography;
using System.Net;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

namespace SwiftRide.ServiceDefaults;

public static class ServiceExtensions
{
    public static IServiceCollection AddServiceDefaults(this IServiceCollection services, IConfiguration config,
        bool development, bool broker = true)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.CustomSchemaIds(t => t.FullName!.Replace('+', '.'));
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "Enter the access token."
            });
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            });
        });
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(config["Security:PublicKeyPath"]
            ?? throw new InvalidOperationException("Security:PublicKeyPath must point to a public PEM key.")));
        services.AddSingleton(rsa);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = development;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                ValidIssuer = config["Security:Issuer"] ?? "swiftride-auth",
                ValidAudience = config["Security:Audience"] ?? "swiftride-api",
                IssuerSigningKey = new RsaSecurityKey(rsa), ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                NameClaimType = "sub", RoleClaimType = "roles", ClockSkew = TimeSpan.Zero
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // Browsers cannot attach an Authorization header during the SignalR WebSocket upgrade.
                    if (context.HttpContext.Request.Path.StartsWithSegments("/api/trips/realtime"))
                        context.Token = context.Request.Query["access_token"];
                    return Task.CompletedTask;
                }
            };
        });
        services.AddAuthorization();
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            o.KnownProxies.Add(IPAddress.Parse(config["Gateway:TrustedProxy"] ?? "127.0.0.1"));
        });
        services.AddHealthChecks();
        if (broker && config.GetValue("RabbitMq:Enabled", true))
        {
            services.AddMassTransit(x => x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(config["RabbitMq:Host"] ?? "localhost", config["RabbitMq:VirtualHost"] ?? "/", h =>
                {
                    h.Username(config["RabbitMq:Username"] ?? "swiftride");
                    h.Password(config["RabbitMq:Password"]
                        ?? throw new InvalidOperationException("RabbitMq:Password is required."));
                });
                cfg.ConfigureEndpoints(context);
            }));
            services.Configure<MassTransitHostOptions>(o =>
            {
                o.WaitUntilStarted = true;
                o.StartTimeout = TimeSpan.FromSeconds(30);
                o.StopTimeout = TimeSpan.FromSeconds(15);
            });
        }
        return services;
    }

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseForwardedHeaders();
        app.Use(async (ctx, next) =>
        {
            var supplied = ctx.Request.Headers["X-Correlation-ID"].FirstOrDefault();
            var correlation = Guid.TryParse(supplied, out var id) ? id.ToString() : Guid.NewGuid().ToString();
            ctx.TraceIdentifier = correlation;
            ctx.Request.Headers["X-Correlation-ID"] = correlation;
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers["X-Correlation-ID"] = correlation;
                return Task.CompletedTask;
            });
            using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlation });
            await next(ctx);
        });
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        app.UseStatusCodePages(async status =>
        {
            await Results.Problem(statusCode: status.HttpContext.Response.StatusCode,
                extensions: new Dictionary<string, object?> { ["traceId"] = status.HttpContext.TraceIdentifier })
                .ExecuteAsync(status.HttpContext);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true }).AllowAnonymous();
        app.MapControllers();
        return app;
    }
}

public sealed class DependencyHealthCheck(Func<CancellationToken, Task> check) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try { await check(cancellationToken); return HealthCheckResult.Healthy(); }
        catch { return HealthCheckResult.Unhealthy("Dependency unavailable."); }
    }
}

public class ApiFailure(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var failure = exception as ApiFailure;
        if (failure is null)
            logger.LogError("Unhandled {ExceptionType}; trace {TraceId}", exception.GetType().Name, context.TraceIdentifier);
        context.Response.StatusCode = failure?.Status ?? 500;
        var details = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = context.Response.StatusCode, Title = failure?.Code ?? "internal_error",
            Detail = failure?.Message ?? "An unexpected error occurred.",
            Extensions = { ["traceId"] = context.TraceIdentifier }
        };
        if (!await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = details
        })) await context.Response.WriteAsJsonAsync(details, cancellationToken);
        return true;
    }
}
