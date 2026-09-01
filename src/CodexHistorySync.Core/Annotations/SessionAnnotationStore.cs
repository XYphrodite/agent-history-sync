using System.Text.Json;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Annotations;

public interface ISessionAnnotationStore
{
    Task<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>> LoadAsync(
        CancellationToken cancellationToken);

    Task SaveAsync(SessionAnnotationKey key, SessionAnnotation annotation, CancellationToken cancellationToken);

    /// <summary>Forgets one annotation. A session that is gone should not keep a title.</summary>
    Task DeleteAsync(SessionAnnotationKey key, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps one file per annotated session under
/// <c>%LOCALAPPDATA%\CodexHistorySync\annotations</c>, each written through a temporary file and
/// replaced into place the way <see cref="State.LocalStateStore"/> writes state.
///
/// One file per session rather than one file for all of them: an annotation is what travels
/// between machines, and a session is the unit that is edited, published, and merged. A shared
/// document would make two machines that named two different sessions collide over one object.
/// </summary>
public sealed class SessionAnnotationStore : ISessionAnnotationStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _directory;

    public SessionAnnotationStore(string? localAppDataDirectory = null)
    {
        var root = localAppDataDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Local application data directory is unavailable.");
        }

        _directory = Path.Combine(root, "CodexHistorySync", "annotations");
    }

    /// <summary>The directory the annotation files live in. It may not exist yet.</summary>
    public string Directory => _directory;

    /// <summary>The file one annotation is kept in, named so no two sessions can share it.</summary>
    public string PathFor(SessionAnnotationKey key) =>
        Path.Combine(_directory, FileName(key));

    public static string FileName(SessionAnnotationKey key) =>
        $"{key.Agent.ToString().ToLowerInvariant()}-{key.SessionId}.json";

    public async Task<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>> LoadAsync(
        CancellationToken cancellationToken)
    {
        var annotations = new Dictionary<SessionAnnotationKey, SessionAnnotation>();
        if (!System.IO.Directory.Exists(_directory)) return annotations;

        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // One unreadable file costs its own title and nothing else: a newer build may have
            // written an agent or a source this one has never heard of.
            if (TryRead(text, out var key, out var annotation) &&
                string.Equals(FileName(key), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            {
                annotations[key] = annotation;
            }
        }

        return annotations;
    }

    public async Task SaveAsync(
        SessionAnnotationKey key,
        SessionAnnotation annotation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        Validate(key, annotation);

        System.IO.Directory.CreateDirectory(_directory);
        var path = PathFor(key);
        var temporaryPath = Path.Combine(_directory, $".{FileName(key)}.{Guid.NewGuid():N}.tmp");

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
                await JsonSerializer.SerializeAsync(temporary, Document(key, annotation), JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await temporary.FlushAsync(cancellationToken).ConfigureAwait(false);
                temporary.Flush(flushToDisk: true);
            }

            if (File.Exists(path)) File.Replace(temporaryPath, path, destinationBackupFileName: null);
            else File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public Task DeleteAsync(SessionAnnotationKey key, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(key.Agent) || !SafeNameComponent.IsValid(key.SessionId)) return Task.CompletedTask;

        try
        {
            File.Delete(PathFor(key));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A title that could not be removed is not worth failing a session deletion over.
        }

        return Task.CompletedTask;
    }

    /// <summary>The bytes one annotation is published as, and the same bytes on every machine.</summary>
    public static byte[] Serialize(SessionAnnotationKey key, SessionAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        Validate(key, annotation);
        return JsonSerializer.SerializeToUtf8Bytes(Document(key, annotation), JsonOptions);
    }

    /// <summary>
    /// Reads what <see cref="Serialize"/> wrote. False for anything this build cannot use, which
    /// is how an entry from a newer build is skipped instead of failing the whole read.
    /// </summary>
    public static bool TryRead(
        string text,
        out SessionAnnotationKey key,
        out SessionAnnotation annotation)
    {
        key = default;
        annotation = null!;

        AnnotationDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<AnnotationDocument>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (document is null ||
            document.SchemaVersion != CurrentSchemaVersion ||
            !Enum.TryParse<ManagedAgent>(document.Agent, ignoreCase: false, out var agent) ||
            !Enum.TryParse<SessionAnnotationSource>(document.Source, ignoreCase: false, out var source) ||
            !SafeNameComponent.IsValid(document.SessionId) ||
            string.IsNullOrWhiteSpace(document.Title) ||
            document.Title.Length > SessionAnnotation.MaximumTitleLength ||
            document.Description is { Length: > SessionAnnotation.MaximumDescriptionLength } ||
            string.IsNullOrWhiteSpace(document.DigestHash))
            return false;

        key = new SessionAnnotationKey(agent, document.SessionId);
        annotation = new SessionAnnotation(
            document.Title,
            document.Description,
            source,
            document.DigestHash,
            document.Model,
            document.UpdatedAt);
        return true;
    }

    private static AnnotationDocument Document(SessionAnnotationKey key, SessionAnnotation annotation) => new(
        CurrentSchemaVersion,
        key.Agent.ToString(),
        key.SessionId,
        annotation.Title,
        annotation.Description,
        annotation.Source.ToString(),
        annotation.DigestHash,
        annotation.Model,
        annotation.UpdatedAt);

    private static void Validate(SessionAnnotationKey key, SessionAnnotation annotation)
    {
        if (!Enum.IsDefined(key.Agent))
        {
            throw new ArgumentException("Session annotation agent is invalid.", nameof(key));
        }

        if (!SafeNameComponent.IsValid(key.SessionId))
        {
            throw new ArgumentException("Session annotation session ID is invalid.", nameof(key));
        }

        if (!Enum.IsDefined(annotation.Source))
        {
            throw new ArgumentException("Session annotation source is invalid.", nameof(annotation));
        }

        if (string.IsNullOrWhiteSpace(annotation.Title) ||
            annotation.Title.Length > SessionAnnotation.MaximumTitleLength)
        {
            throw new ArgumentException("Session annotation title is invalid.", nameof(annotation));
        }

        if (annotation.Description is { Length: > SessionAnnotation.MaximumDescriptionLength })
        {
            throw new ArgumentException("Session annotation description is too long.", nameof(annotation));
        }

        if (string.IsNullOrWhiteSpace(annotation.DigestHash))
        {
            throw new ArgumentException("Session annotation digest hash is missing.", nameof(annotation));
        }
    }

    /// <summary>
    /// The stored shape, holding the agent and the source as text so an unknown value arrives as
    /// data to skip rather than as a deserialization failure.
    /// </summary>
    private sealed record AnnotationDocument(
        int SchemaVersion,
        string Agent,
        string SessionId,
        string Title,
        string? Description,
        string Source,
        string DigestHash,
        string? Model,
        DateTimeOffset UpdatedAt);
}
