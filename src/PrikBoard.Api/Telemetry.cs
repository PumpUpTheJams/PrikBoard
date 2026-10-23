using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;

namespace PrikBoard.Api;

/// <summary>
/// Logging through Serilog (levels and console output in the <c>Serilog</c> section of appsettings.json), and, only when
/// <c>OpenTelemetry:Endpoint</c> is set, logs, traces and metrics to that OTLP collector.
/// </summary>
public static class Telemetry
{
    public const string ServiceName = "prikboard-api";

    /// <summary>A request that takes longer than this is logged as a warning.</summary>
    private static readonly TimeSpan SlowRequest = TimeSpan.FromSeconds(1);

    public static WebApplicationBuilder AddPrikBoardLogging(this WebApplicationBuilder builder)
    {
        var otlp = OtlpSettings.From(builder.Configuration);
        // The callback form builds a logger per host, so test factories running side by side don't share one.
        builder.Services.AddSerilog((services, log) =>
        {
            log.ReadFrom.Configuration(builder.Configuration).ReadFrom.Services(services).Enrich.FromLogContext();
            if (otlp is not null)
            {
                log.WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = otlp.Endpoint;
                    options.Protocol = otlp.Http ? OtlpProtocol.HttpProtobuf : OtlpProtocol.Grpc;
                    options.Headers = otlp.Headers;
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = ServiceName,
                        ["service.version"] = AppVersion.Number,
                    };
                    // The sink's own calls to the collector are not traced.
                    options.OnBeginSuppressInstrumentation = SuppressInstrumentationScope.Begin;
                });
            }
        });
        if (otlp is null) return builder;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: AppVersion.Number))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddOtlpExporter(options => otlp.Configure(options, "traces")))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(options => otlp.Configure(options, "metrics")));
        return builder;
    }

    /// <summary>
    /// One line for failed (5xx) and slow requests, nothing for the rest; unhandled exceptions are logged by the exception
    /// handler too, with their stack trace. Live connections stay open for as long as the board does, so they never count
    /// as slow.
    /// </summary>
    public static IApplicationBuilder UsePrikBoardRequestLogging(this IApplicationBuilder app) =>
        app.UseSerilogRequestLogging(options => options.GetLevel = (context, elapsedMs, error) =>
            error is not null || context.Response.StatusCode >= StatusCodes.Status500InternalServerError ? LogEventLevel.Error
            : elapsedMs > SlowRequest.TotalMilliseconds && !context.Request.Path.StartsWithSegments("/api/live") ? LogEventLevel.Warning
            : LogEventLevel.Verbose);

    /// <summary>
    /// <c>OpenTelemetry:Endpoint</c> (the collector's base URL), <c>Protocol</c> (<c>grpc</c>, the default, or
    /// <c>http/protobuf</c>) and <c>Headers</c> (<c>key=value,key2=value2</c>, for an API key).
    /// </summary>
    private sealed record OtlpSettings(string Endpoint, bool Http, Dictionary<string, string> Headers)
    {
        public static OtlpSettings? From(IConfiguration configuration)
        {
            if (configuration["OpenTelemetry:Endpoint"] is not { Length: > 0 } endpoint) return null;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"OpenTelemetry:Endpoint must be an absolute URL, not {endpoint}.");
            }
            var http = configuration["OpenTelemetry:Protocol"] switch
            {
                null or "" or "grpc" => false,
                "http/protobuf" => true,
                var other => throw new InvalidOperationException($"OpenTelemetry:Protocol must be grpc or http/protobuf, not {other}."),
            };
            var headers = (configuration["OpenTelemetry:Headers"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(header => header.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(pair => pair is [{ Length: > 0 }, _])
                .ToDictionary(pair => pair[0], pair => pair[1]);
            return new OtlpSettings(endpoint.TrimEnd('/'), http, headers);
        }

        /// <summary>Unlike the environment variables, an endpoint set in code is used as is, so HTTP needs the signal's path.</summary>
        public void Configure(OtlpExporterOptions options, string signal)
        {
            options.Protocol = Http ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
            options.Endpoint = new Uri(Http ? $"{Endpoint}/v1/{signal}" : Endpoint);
            options.Headers = string.Join(',', Headers.Select(header => $"{header.Key}={header.Value}"));
        }
    }
}
