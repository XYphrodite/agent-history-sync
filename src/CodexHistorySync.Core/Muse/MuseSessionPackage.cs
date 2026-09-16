using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Muse;

public static class MuseSessionPackage
{
    public const int SchemaVersion = 1;
    public const string LogicalIdPrefix = "mu-";
    public const string SessionFileName = "session.jsonl";

    private static readonly Regex UuidPattern = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static bool IsMuseLogicalId(string value) =>
        value.StartsWith(LogicalIdPrefix, StringComparison.Ordinal) &&
        UuidPattern.IsMatch(value[LogicalIdPrefix.Length..]);

    public static string ToLogicalId(string sessionId)
    {
        if (!UuidPattern.IsMatch(sessionId))
            throw new ArgumentException("Muse session id must be a UUID.", nameof(sessionId));
        return LogicalIdPrefix + sessionId.ToLowerInvariant();
    }

    public static string SessionIdFromLogicalId(string logicalId)
    {
        if (!IsMuseLogicalId(logicalId))
            throw new ArgumentException("Not a Muse logical object id.", nameof(logicalId));
        return logicalId[LogicalIdPrefix.Length..];
    }

    public static IReadOnlyList<string>? ListSynchronizableFiles(string sessionDirectory)
    {
        var sessionFile = Path.Combine(sessionDirectory, SessionFileName);
        if (!File.Exists(sessionFile)) return null;
        var info = new FileInfo(sessionFile);
        if (info.Length == 0) return null;
        return [SessionFileName];
    }

    public static byte[] BuildFromDirectory(string sessionDirectory)
    {
        var sessionName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sessionDirectory));
        if (!UuidPattern.IsMatch(sessionName))
            throw new InvalidDataException("Muse session directory name is not a UUID.");

        var files = ListSynchronizableFiles(sessionDirectory)
            ?? throw new InvalidDataException("Muse session holds no synchronizable state (session.jsonl is required).");

        var entries = new List<PackagedFileDto>(files.Count);
        foreach (var relativePath in files)
        {
            var fullPath = Path.Combine(sessionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                throw new InvalidDataException($"Muse session file {relativePath} is missing.");
            var content = File.ReadAllBytes(fullPath);
            // Normalize absolute paths that would differ between machines (workspace_root, cwd, socket)
            // Replace with placeholder to avoid perpetual diff
            if (StringComparer.Ordinal.Equals(relativePath, SessionFileName))
                content = NormalizeSessionContent(content);
            entries.Add(new PackagedFileDto(relativePath, Convert.ToBase64String(content)));
        }

        var package = new PackageDto(SchemaVersion, sessionName.ToLowerInvariant(), entries);
        return JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
    }

    public static ContentHash HashPackage(byte[] package) =>
        new(Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant());

    public static PackageInfo Parse(byte[] package)
    {
        PackageDto? dto;
        try { dto = JsonSerializer.Deserialize<PackageDto>(package, JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("Muse session package is malformed.", ex); }
        if (dto is null || dto.V != SchemaVersion) throw new InvalidDataException("Muse session package schema is unsupported.");
        if (!UuidPattern.IsMatch(dto.Id)) throw new InvalidDataException("Muse session package id is invalid.");
        if (dto.Files.Count == 0) throw new InvalidDataException("Muse session package is incomplete.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<PackagedFile>(dto.Files.Count);
        foreach (var entry in dto.Files)
        {
            if (!IsAllowedRelativePath(entry.Path) || !seen.Add(entry.Path))
                throw new InvalidDataException($"Muse session package path {entry.Path} is not allowed.");
            byte[] content;
            try { content = Convert.FromBase64String(entry.ContentBase64); }
            catch (FormatException ex) { throw new InvalidDataException("Muse session package content is malformed.", ex); }
            files.Add(new PackagedFile(entry.Path, content));
        }

        if (files.All(f => f.Path != SessionFileName))
            throw new InvalidDataException("Muse session package carries no session.jsonl.");

        return new PackageInfo(dto.Id.ToLowerInvariant(), files);
    }

    public static void Materialize(PackageInfo package, MusePaths paths)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(paths);

        // Find the session directory that contains this sessionId (search by UUID)
        // For new sessions, create under sessions/<uuid>/session.jsonl (flat, scanner will find it)
        var sessionId = package.SessionId;
        var existing = FindExistingSessionDirectory(paths.Sessions, sessionId);
        var directory = existing ?? Path.Combine(paths.Sessions, sessionId);
        Directory.CreateDirectory(directory);

        foreach (var file in package.Files)
        {
            var content = file.Content;
            if (StringComparer.Ordinal.Equals(file.Path, SessionFileName))
                content = DenormalizeSessionContent(content, directory);
            ReplaceAtomically(Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar)), content);
        }
    }

    private static string? FindExistingSessionDirectory(string sessionsRoot, string sessionId)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(sessionsRoot, SessionFileName, SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(file);
                if (dir is null) continue;
                var dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (StringComparer.OrdinalIgnoreCase.Equals(dirName, sessionId))
                    return dir;
            }
        }
        catch { }
        return null;
    }

    private static byte[] NormalizeSessionContent(byte[] content)
    {
        // Replace absolute workspace paths with placeholder to avoid cross-machine diff
        // This is a simple string replacement, not a full JSON parse, to keep the package stable
        try
        {
            var text = Encoding.UTF8.GetString(content);
            // Replace common absolute path patterns
            text = text.Replace("/mnt/c/Repos/agent-sync", "$WORKSPACE_ROOT");
            text = text.Replace("/home/gamer/.local/share/muse", "$MUSE_HOME");
            return Encoding.UTF8.GetBytes(text);
        }
        catch
        {
            return content;
        }
    }

    private static byte[] DenormalizeSessionContent(byte[] content, string sessionDirectory)
    {
        try
        {
            var text = Encoding.UTF8.GetString(content);
            text = text.Replace("$WORKSPACE_ROOT", Path.GetDirectoryName(sessionDirectory) ?? "");
            text = text.Replace("$MUSE_HOME", Path.GetDirectoryName(Path.GetDirectoryName(sessionDirectory) ?? "") ?? "");
            return Encoding.UTF8.GetBytes(text);
        }
        catch
        {
            return content;
        }
    }

    private static bool IsAllowedRelativePath(string path) =>
        StringComparer.Ordinal.Equals(path, SessionFileName);

    private static void ReplaceAtomically(string destination, byte[] content)
    {
        var temp = destination + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(temp, content);
            File.Move(temp, destination, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public sealed record PackageInfo(string SessionId, IReadOnlyList<PackagedFile> Files);
    public sealed record PackagedFile(string Path, byte[] Content);

    private sealed record PackageDto(int V, string Id, IReadOnlyList<PackagedFileDto> Files);
    private sealed record PackagedFileDto(string Path, string ContentBase64);
}
