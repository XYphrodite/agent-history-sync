using CodexHistorySync.Remote;
using CodexHistorySync.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CodexHistorySync.Server.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENT_SYNC_TEST_POSTGRES")))
            Skip = "Set AGENT_SYNC_TEST_POSTGRES to a disposable PostgreSQL database (see docs/server.md).";
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string schema = "test_" + Guid.NewGuid().ToString("N");
    private NpgsqlDataSource? admin;
    private WebApplication? app;
    public NpgsqlDataSource Source { get; private set; } = null!;
    public PostgresStore Store => app!.Services.GetRequiredService<PostgresStore>();
    public string Origin { get; private set; } = "";
    private string connectionString = "";
    public StoreEndpoint Endpoint(string? name = null) => new(Origin + "/v1/repositories/" + (name ?? Guid.NewGuid().ToString("N")));

    public async Task InitializeAsync()
    {
        var value = Environment.GetEnvironmentVariable("AGENT_SYNC_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(value)) return;
        admin = NpgsqlDataSource.Create(value);
        await using var create = admin.CreateCommand($"CREATE SCHEMA {schema}");
        await create.ExecuteNonQueryAsync();
        connectionString = new NpgsqlConnectionStringBuilder(value) { SearchPath = schema }.ConnectionString;
        await StartAsync();
    }

    private async Task StartAsync()
    {
        app = ServerApplication.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:ConnectionString"] = connectionString,
                ["Storage:AllowedHosts"] = "127.0.0.1"
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        });
        Source = app.Services.GetRequiredService<NpgsqlDataSource>();
        await Store.MigrateAsync(CancellationToken.None);
        await app.StartAsync();
        Origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    public async Task RestartAsync()
    {
        await app!.StopAsync();
        await app.DisposeAsync();
        await StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        if (admin is not null)
        {
            // Only our freshly generated schema; never drop the supplied database or public schema.
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }
}
