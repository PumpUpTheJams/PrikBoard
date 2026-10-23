using System.Collections.Concurrent;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Auth;
using Serilog.Core;
using Serilog.Events;

namespace PrikBoard.Api.Tests;

/// <summary>What a first start logs: a self-hoster's first look at the log should show no failures or false alarms.</summary>
public class StartupLogTests
{
    [Fact]
    public async Task A_first_start_without_a_database_logs_only_the_setup_key()
    {
        var sink = new Collected();
        await using var empty = new PrikBoardApiFactory();
        await using var started = empty.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));

        (await started.CreateClient().GetAsync("/api/setup")).EnsureSuccessStatusCode();

        Assert.Equal("", string.Join('\n', sink.Warnings.Where(w => !w.StartsWith(typeof(SetupKey).FullName!))));
    }

    private sealed class Collected : ILogEventSink
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level < LogEventLevel.Warning) return;
            var source = logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string name } ? name : "";
            Warnings.Enqueue($"{source}: {logEvent.RenderMessage()}");
        }
    }
}
