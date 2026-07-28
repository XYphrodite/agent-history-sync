using System.Text.Json;

namespace CodexHistorySync.Core.State;

public sealed class LocalStateStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _localAppDataDirectory;

    public LocalStateStore(string? localAppDataDirectory = null)
    {
        _localAppDataDirectory = localAppDataDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(_localAppDataDirectory))
        {
            throw new InvalidOperationException("Local application data directory is unavailable.");
        }
    }

    public string GetStatePath(string repositoryId) =>
        Path.Combine(_localAppDataDirectory, "CodexHistorySync", "repositories", ValidateRepositoryId(repositoryId), "state.json");

    public async Task SaveAsync(DeviceState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);

        var statePath = GetStatePath(state.RepositoryId);
        var directory = Path.GetDirectoryName(statePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(statePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var temporary = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(temporary, state, JsonOptions, cancellationToken).ConfigureAwait(false);
                await temporary.FlushAsync(cancellationToken).ConfigureAwait(false);
                temporary.Flush(flushToDisk: true);
            }

            if (File.Exists(statePath))
            {
                File.Replace(temporaryPath, statePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, statePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<DeviceState> LoadAsync(string repositoryId, CancellationToken cancellationToken)
    {
        var statePath = GetStatePath(repositoryId);
        await using var stateFile = new FileStream(
            statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var state = await JsonSerializer.DeserializeAsync<DeviceState>(stateFile, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Device state is empty.");

        ValidateState(state);
        if (!StringComparer.Ordinal.Equals(state.RepositoryId, repositoryId))
        {
            throw new InvalidDataException("Device state repository ID does not match its path.");
        }

        return state;
    }

    private static void ValidateState(DeviceState state)
    {
        if (state.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported device state schema version: {state.SchemaVersion}.");
        }

        ValidateRepositoryId(state.RepositoryId);
        ArgumentNullException.ThrowIfNull(state.Objects);
        var duplicateObjectId = state.Objects
            .GroupBy(version => version.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateObjectId is not null)
        {
            throw new InvalidDataException($"Device state contains duplicate object ID '{duplicateObjectId.Key.Value}'.");
        }
    }

    private static string ValidateRepositoryId(string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) ||
            repositoryId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            repositoryId is "." or "..")
        {
            throw new ArgumentException("Repository ID is invalid.", nameof(repositoryId));
        }

        return repositoryId;
    }
}
