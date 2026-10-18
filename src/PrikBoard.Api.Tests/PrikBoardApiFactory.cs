using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PrikBoard.Api.Tests;

/// <summary>Runs the real API in memory against a throwaway SQLite database file.</summary>
public class PrikBoardApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"prikboard-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Pooling off so the file is released (and deletable) when the factory is disposed.
        builder.UseSetting("ConnectionStrings:PrikBoard", $"Data Source={_databasePath};Pooling=False");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        File.Delete(_databasePath);
    }
}
