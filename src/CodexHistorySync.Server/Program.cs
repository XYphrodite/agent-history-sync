using CodexHistorySync.Server;

if (args is ["--health-check"])
{
    try
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(4) };
        using var response = await http.GetAsync("http://127.0.0.1:8080/readyz");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}

try
{
    await using var app = ServerApplication.Build(args);
    await app.Services.GetRequiredService<PostgresStore>().MigrateAsync(CancellationToken.None);
    await app.RunAsync();
    return 0;
}
catch (Exception exception)
{
    // Startup exceptions may contain a database address, password file path or provider detail.
    Console.Error.WriteLine($"Storage server failed to start ({exception.GetType().Name}). Check configuration and database availability.");
    return 1;
}
