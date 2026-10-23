using System.Collections.Concurrent;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace PrikBoard.Api.Tests;

/// <summary>What a first start logs: a self-hoster's first look at the log should show no failures.</summary>
public class StartupMigrationTests
{
    [Fact]
    public async Task A_first_start_without_a_database_logs_no_errors()
    {
        var sink = new Collected();
        await using var empty = new PrikBoardApiFactory();
        await using var started = empty.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));

        (await started.CreateClient().GetAsync("/api/setup")).EnsureSuccessStatusCode();

        Assert.Equal("", string.Join('\n', sink.Errors));
    }

    private sealed class Collected : ILogEventSink
    {
        public ConcurrentQueue<string> Errors { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level >= LogEventLevel.Error) Errors.Enqueue(logEvent.RenderMessage());
        }
    }
}
