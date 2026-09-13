using System.Text.Json;
using System.Threading.RateLimiting;
using CodexHistorySync.Remote;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace CodexHistorySync.Server;

public static class ServerApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        // Request paths and exception details must not accidentally become a data log.
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:8080");
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = StoreProtocol.MaximumBlobBytes;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        });
        var hosts = (builder.Configuration["Storage:AllowedHosts"] ?? "localhost;127.0.0.1;[::1]")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 || hosts.Any(x => x.Contains('*') || Uri.CheckHostName(x.Trim('[', ']')) == UriHostNameType.Unknown))
            throw new InvalidDataException("Explicit API host names are required; wildcards are not allowed.");
        foreach (var host in hosts) _ = new StoreEndpoint($"https://{host}/v1/repositories/host-check");
        var allowedHosts = hosts.Select(x => x.Trim('[', ']')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connectionString = builder.Configuration["Storage:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var passwordFile = builder.Configuration["Storage:PasswordFile"] ?? throw new InvalidDataException("A database password file is required.");
            connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = builder.Configuration["Storage:DatabaseHost"] ?? "postgres",
                Database = builder.Configuration["Storage:Database"] ?? "agent_sync",
                Username = builder.Configuration["Storage:DatabaseUser"] ?? "agent_sync",
                Password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n'),
                IncludeErrorDetail = false,
                // Password authentication on the private Docker network, not Kerberos.
                GssEncryptionMode = GssEncryptionMode.Disable,
                MaxPoolSize = 8
            }.ConnectionString;
        }
        var databaseOptions = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Large bytea transfers can exceed Npgsql's 30-second default under load.
            // Keep this below the five-minute request deadline; cancellation still aborts SQL.
            CommandTimeout = 240,
            IncludeErrorDetail = false
        };
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(databaseOptions.ConnectionString));
        builder.Services.AddSingleton<PostgresStore>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddConcurrencyLimiter("storage", limiter =>
            {
                limiter.PermitLimit = 2;
                limiter.QueueLimit = 0;
                limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
        });
        var app = builder.Build();
        // Endpoint metadata must be available before applying the storage concurrency policy.
        app.UseRouting();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (context.Request.Headers.ContainsKey("Origin") ||
                context.Request.Headers["Sec-Fetch-Site"].Any(x => x is not (null or "none")) ||
                !allowedHosts.Contains(context.Request.Host.Host.Trim('[', ']')))
            {
                context.Response.StatusCode = 403;
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            context.RequestAborted = timeout.Token;
            try { await next(context); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!context.Response.HasStarted) context.Response.StatusCode = 408;
                else context.Abort();
            }
            catch (Exception exception)
            {
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.StatusCode = exception switch
                {
                    StorePayloadTooLargeException => 413,
                    BadHttpRequestException bad => bad.StatusCode,
                    KeyNotFoundException => 404,
                    InvalidDataException or JsonException or FormatException or InvalidOperationException or ArgumentException => 400,
                    PostgresException { SqlState: "23503" or "23514" } => 400,
                    NpgsqlException => 503,
                    _ => 500
                };
                await context.Response.WriteAsJsonAsync(new { error = "Storage request failed.", status = context.Response.StatusCode });
            }
        });
        app.UseRateLimiter();
        app.MapGet("/healthz", () => Results.Ok(new { status = "alive" }));
        app.MapGet("/readyz", async (PostgresStore store, CancellationToken ct) =>
            await store.IsReadyAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
        app.MapGet("/v1/info", () => new StoreInfo(StoreProtocol.Version));
        var group = app.MapGroup("/v1/repositories/{name}").RequireRateLimiting("storage");
        group.MapPut("", async (string name, HttpRequest request, PostgresStore store, CancellationToken ct) =>
        {
            var input = await ReadJsonAsync<StoreInitialization>(request, ct);
            var result = await store.InitializeAsync(name, input, ct);
            return result is null ? Results.Conflict() : Results.Json(result, statusCode: 201);
        });
        group.MapGet("/setup", async (string name, PostgresStore store, CancellationToken ct) =>
            await store.ReadSetupAsync(name, ct) is { } setup ? Results.Ok(setup) : Results.NotFound());
        group.MapGet("/snapshot", async (string name, PostgresStore store, CancellationToken ct) =>
            await store.ReadSnapshotAsync(name, ct) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());
        group.MapPut("/blobs/{hash}", async (string name, string hash, HttpRequest request, PostgresStore store, CancellationToken ct) =>
        {
            RequireContentType(request, "application/octet-stream");
            var bytes = await StoreProtocol.ReadBoundedAsync(request.Body, StoreProtocol.MaximumBlobBytes, ct);
            await store.UploadBlobAsync(name, hash, bytes, ct);
            return Results.NoContent();
        });
        group.MapGet("/blobs/{hash}", async (string name, string hash, PostgresStore store, CancellationToken ct) =>
            await store.ReadBlobAsync(name, hash, ct) is { } bytes ? Results.Bytes(bytes, "application/octet-stream") : Results.NotFound());
        group.MapPost("/publish", async (string name, HttpRequest request, PostgresStore store, CancellationToken ct) =>
        {
            var publication = await ReadJsonAsync<StorePublication>(request, ct);
            var result = await store.PublishAsync(name, publication, ct);
            return Results.Json(result, statusCode: result.Published ? 200 : 409);
        });
        return app;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpRequest request, CancellationToken ct)
    {
        RequireContentType(request, "application/json");
        var bytes = await StoreProtocol.ReadBoundedAsync(request.Body, StoreProtocol.MaximumJsonBytes, ct);
        return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Empty request.");
    }

    private static void RequireContentType(HttpRequest request, string expected)
    {
        if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), expected, StringComparison.OrdinalIgnoreCase))
            throw new BadHttpRequestException("Unsupported content type.", 415);
    }
}
