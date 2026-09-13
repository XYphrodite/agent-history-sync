using System.Data;
using System.Reflection;
using System.Text.Json;
using CodexHistorySync.Remote;
using Npgsql;

namespace CodexHistorySync.Server;

public sealed class PostgresStore(NpgsqlDataSource source)
{
    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var locked = Command(connection, "SELECT pg_advisory_xact_lock(73103013)");
        await locked.ExecuteNonQueryAsync(ct);
        await using var createVersion = Command(connection,
            "CREATE TABLE IF NOT EXISTS schema_version (singleton boolean PRIMARY KEY CHECK (singleton), version integer NOT NULL)");
        await createVersion.ExecuteNonQueryAsync(ct);
        await using var readVersion = Command(connection, "SELECT version FROM schema_version WHERE singleton = true");
        var version = await readVersion.ExecuteScalarAsync(ct);
        if (version is not null && (int)version != 1) throw new InvalidDataException("Unsupported database schema.");
        if (version is null)
        {
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexHistorySync.Server.schema.sql")!;
            using var reader = new StreamReader(stream);
            await using var schema = Command(connection, await reader.ReadToEndAsync(ct));
            await schema.ExecuteNonQueryAsync(ct);
            await using var stamp = Command(connection, "INSERT INTO schema_version VALUES (true, 1)");
            await stamp.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT version FROM schema_version WHERE singleton = true");
        return await command.ExecuteScalarAsync(ct) is 1;
    }

    public async Task<StoreSetup?> ReadSetupAsync(string name, CancellationToken ct)
    {
        RequireName(name);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = Command(connection, "SELECT manifest, encrypted_index, revision FROM repositories WHERE name = $1", name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new StoreSetup((byte[])reader[0], (byte[])reader[1], reader.GetString(2)) : null;
    }

    public async Task<StoreSetup?> InitializeAsync(string name, StoreInitialization initialization, CancellationToken ct)
    {
        RequireName(name);
        ValidateManifest(initialization.Manifest);
        StoreProtocol.RequireEnvelope(initialization.Index, StoreProtocol.MaximumIndexBytes);
        var revision = Guid.NewGuid().ToString("N");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = Command(connection,
            "INSERT INTO repositories (name, manifest, encrypted_index, revision) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING",
            name, initialization.Manifest, initialization.Index, revision);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? new StoreSetup(initialization.Manifest, initialization.Index, revision) : null;
    }

    public async Task<StoreSnapshot?> ReadSnapshotAsync(string name, CancellationToken ct)
    {
        RequireName(name);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        string revision;
        byte[] index;
        await using (var command = Command(connection, "SELECT revision, encrypted_index FROM repositories WHERE name = $1", name))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            revision = reader.GetString(0);
            index = (byte[])reader[1];
        }
        var objects = new List<StoreObject>();
        await using (var command = Command(connection, "SELECT object_id, hash FROM object_refs WHERE repository_name = $1 ORDER BY object_id LIMIT 100001", name))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) objects.Add(new StoreObject(reader.GetString(0), reader.GetString(1)));
        }
        if (objects.Count > StoreProtocol.MaximumObjects) throw new InvalidDataException("Snapshot exceeds the object limit.");
        await transaction.CommitAsync(ct);
        return new StoreSnapshot(revision, index, objects);
    }

    public async Task UploadBlobAsync(string name, string hash, byte[] bytes, CancellationToken ct)
    {
        RequireName(name);
        RequireHash(hash);
        StoreProtocol.RequireEnvelope(bytes, StoreProtocol.MaximumBlobBytes);
        if (StoreProtocol.Hash(bytes) != hash) throw new InvalidDataException("Ciphertext checksum mismatch.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var exists = Command(connection, "SELECT 1 FROM repositories WHERE name = $1", name);
        if (await exists.ExecuteScalarAsync(ct) is null) throw new KeyNotFoundException();
        await using var command = Command(connection,
            "INSERT INTO blobs (repository_name, hash, ciphertext) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING", name, hash, bytes);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<byte[]?> ReadBlobAsync(string name, string hash, CancellationToken ct)
    {
        RequireName(name);
        RequireHash(hash);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = Command(connection, "SELECT ciphertext FROM blobs WHERE repository_name = $1 AND hash = $2", name, hash);
        return await command.ExecuteScalarAsync(ct) as byte[];
    }

    public async Task<StorePublicationResult> PublishAsync(string name, StorePublication publication, CancellationToken ct)
    {
        RequireName(name);
        StoreProtocol.Validate(publication);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var readHead = Command(connection, "SELECT revision FROM repositories WHERE name = $1 FOR UPDATE", name);
        var current = await readHead.ExecuteScalarAsync(ct) as string ?? throw new KeyNotFoundException();
        if (current != publication.ExpectedRevision) return new StorePublicationResult(false, current);

        var deleted = publication.Changes.Where(x => x.Hash is null).Select(x => x.ObjectId).ToArray();
        if (deleted.Length > 0)
        {
            await using var delete = Command(connection, "DELETE FROM object_refs WHERE repository_name = $1 AND object_id = ANY($2)", name, deleted);
            await delete.ExecuteNonQueryAsync(ct);
        }
        var changed = publication.Changes.Where(x => x.Hash is not null).ToArray();
        if (changed.Length > 0)
        {
            // One round trip for a batch, not one per session. The FK validates every blob.
            await using var upsert = Command(connection, """
                INSERT INTO object_refs (repository_name, object_id, hash)
                SELECT $1, item.object_id, item.hash FROM unnest($2::text[], $3::text[]) AS item(object_id, hash)
                ON CONFLICT (repository_name, object_id) DO UPDATE SET hash = excluded.hash
                """, name, changed.Select(x => x.ObjectId).ToArray(), changed.Select(x => x.Hash!).ToArray());
            await upsert.ExecuteNonQueryAsync(ct);
        }
        await using var count = Command(connection, "SELECT count(*) FROM object_refs WHERE repository_name = $1", name);
        if ((long)(await count.ExecuteScalarAsync(ct))! > StoreProtocol.MaximumObjects)
            throw new InvalidDataException("Snapshot exceeds the object limit.");
        var next = Guid.NewGuid().ToString("N");
        if (publication.Index is null)
        {
            await using var update = Command(connection, "UPDATE repositories SET revision = $2, updated_at = now() WHERE name = $1", name, next);
            await update.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var update = Command(connection,
                "UPDATE repositories SET revision = $2, encrypted_index = $3, updated_at = now() WHERE name = $1", name, next, publication.Index);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new StorePublicationResult(true, next);
    }

    private static void ValidateManifest(byte[]? manifest)
    {
        try { ValidateManifestFields(manifest); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Invalid setup manifest.", exception); }
    }

    private static void ValidateManifestFields(byte[]? manifest)
    {
        if (manifest is not { Length: > 0 and <= 65536 }) throw new InvalidDataException("Invalid setup manifest.");
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 ||
            root.GetProperty("schemaVersion").GetInt32() != 1 ||
            !StoreProtocol.IsRevision(root.GetProperty("repositoryId").GetString()) ||
            root.GetProperty("authenticator").GetBytesFromBase64().Length != 32)
            throw new InvalidDataException("Invalid setup manifest.");
        var argon = root.GetProperty("argon2Parameters");
        if (argon.EnumerateObject().Count() != 4 || argon.GetProperty("salt").GetBytesFromBase64().Length != 16 ||
            argon.GetProperty("memoryKiB").GetInt32() <= 0 || argon.GetProperty("iterations").GetInt32() <= 0 ||
            argon.GetProperty("parallelism").GetInt32() <= 0)
            throw new InvalidDataException("Invalid setup parameters.");
    }

    private static void RequireName(string name)
    {
        if (!StoreProtocol.IsName(name)) throw new InvalidDataException("Invalid repository name.");
    }
    private static void RequireHash(string hash)
    {
        if (!StoreProtocol.IsHash(hash)) throw new InvalidDataException("Invalid ciphertext hash.");
    }
    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command;
    }
}
