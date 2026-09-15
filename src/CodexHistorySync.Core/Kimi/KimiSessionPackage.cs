using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Kimi;

/// <summary>
/// Compact portable representation of a Kimi Code CLI session for encrypted sync.
/// Syncs state.json plus every agent wire (agents/&lt;id&gt;/wire.jsonl) and plan file; runtime-only
/// content (logs, notify, tasks, cron, upcoming-goals.json) stays home, the way Grok leaves
/// terminal logs behind.
///
/// state.json is normalized before packaging: the absolute agents.*.homedir paths are replaced
/// with a placeholder and substituted back at materialization time. Without that, the package hash
/// would differ between machines for the same session (a different user profile changes the path),
/// and every pull would see a perpetual local change and republish forever.
/// </summary>
public static class KimiSessionPackage
{
    public const int SchemaVersion = 1;
    public const string LogicalIdPrefix = "ki-";
    public const string HomeDirPlaceholder = "$KIMI_SESSION_DIR";
    public const string StateFileName = "state.json";
    public const string WireFileName = "wire.jsonl";
    public const string PlansDirectoryName = "plans";

    private const string AgentsDirectoryName = "agents";

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex UuidPattern = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WorkDirKeyPattern = new(
        @"^wd_[A-Za-z0-9-]+_[0-9a-f]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool IsKimiLogicalId(string value) =>
        value.StartsWith(LogicalIdPrefix, StringComparison.Ordinal) &&
        UuidPattern.IsMatch(value[LogicalIdPrefix.Length..]);

    public static string ToLogicalId(string sessionId)
    {
        if (!UuidPattern.IsMatch(sessionId))
            throw new ArgumentException("Kimi session id must be a UUID.", nameof(sessionId));
        return LogicalIdPrefix + sessionId.ToLowerInvariant();
    }

    public static string SessionIdFromLogicalId(string logicalId)
    {
        if (!IsKimiLogicalId(logicalId))
            throw new ArgumentException("Not a Kimi logical object id.", nameof(logicalId));
        return logicalId[LogicalIdPrefix.Length..];
    }

    /// <summary>
    /// The session files that make up one synchronizable object: state.json, every
    /// agents/&lt;id&gt;/wire.jsonl, and agents/&lt;id&gt;/plans/*.md, as repository-relative paths in a
    /// deterministic order. Returns null when the directory holds no synchronizable state at all
    /// (no state.json or no wire file), so the scanner defers it instead of publishing an empty
    /// package. Anything outside this list (logs, notify, tasks, cron, upcoming-goals.json) never
    /// enters a package.
    /// </summary>
    public static IReadOnlyList<string>? ListSynchronizableFiles(string sessionDirectory)
    {
        var statePath = Path.Combine(sessionDirectory, StateFileName);
        if (!File.Exists(statePath)) return null;

        var agentsRoot = Path.Combine(sessionDirectory, AgentsDirectoryName);
        if (!Directory.Exists(agentsRoot)) return null;

        var files = new List<string> { StateFileName };
        foreach (var agentDirectory in EnumerateDirectChildren(agentsRoot))
        {
            if (File.Exists(Path.Combine(agentDirectory, WireFileName)))
                files.Add($"{AgentsDirectoryName}/{Path.GetFileName(agentDirectory)}/{WireFileName}");
            var plansDirectory = Path.Combine(agentDirectory, PlansDirectoryName);
            if (Directory.Exists(plansDirectory))
            {
                foreach (var planPath in EnumerateDirectChildren(plansDirectory))
                    files.Add($"{AgentsDirectoryName}/{Path.GetFileName(agentDirectory)}/{PlansDirectoryName}/{Path.GetFileName(planPath)}");
            }
        }

        if (files.Count == 1) return null;
        return files;
    }

    public static byte[] BuildFromDirectory(string sessionDirectory, string workDirKey)
    {
        if (!WorkDirKeyPattern.IsMatch(workDirKey))
            throw new InvalidDataException("Kimi session workDirKey is malformed.");
        var sessionName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sessionDirectory));
        if (!sessionName.StartsWith(KimiPaths.SessionIdPrefix, StringComparison.Ordinal) ||
            !UuidPattern.IsMatch(sessionName[KimiPaths.SessionIdPrefix.Length..]))
            throw new InvalidDataException("Kimi session directory name is not session_<uuid>.");

        var files = ListSynchronizableFiles(sessionDirectory)
            ?? throw new InvalidDataException("Kimi session holds no synchronizable state (state.json and at least one wire.jsonl are required).");

        var entries = new List<PackagedFileDto>(files.Count);
        foreach (var relativePath in files)
        {
            var fullPath = Path.Combine(sessionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                throw new InvalidDataException($"Kimi session file {relativePath} is missing.");
            var content = File.ReadAllBytes(fullPath);
            if (StringComparer.Ordinal.Equals(relativePath, StateFileName))
                content = NormalizeState(content, sessionName[KimiPaths.SessionIdPrefix.Length..]).Content;
            entries.Add(new PackagedFileDto(relativePath, Convert.ToBase64String(content)));
        }

        var package = new PackageDto(SchemaVersion,
            sessionName[KimiPaths.SessionIdPrefix.Length..].ToLowerInvariant(),
            workDirKey,
            entries);
        return JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
    }

    public static ContentHash HashPackage(byte[] package) =>
        new(Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant());

    public static PackageInfo Parse(byte[] package)
    {
        PackageDto? dto;
        try { dto = JsonSerializer.Deserialize<PackageDto>(package, JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("Kimi session package is malformed.", exception); }
        if (dto is null || dto.V != SchemaVersion) throw new InvalidDataException("Kimi session package schema is unsupported.");
        if (!UuidPattern.IsMatch(dto.Id)) throw new InvalidDataException("Kimi session package id is invalid.");
        if (!WorkDirKeyPattern.IsMatch(dto.WorkDirKey)) throw new InvalidDataException("Kimi session package workDirKey is malformed.");
        if (dto.Files.Count == 0) throw new InvalidDataException("Kimi session package is incomplete.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<PackagedFile>(dto.Files.Count);
        foreach (var entry in dto.Files)
        {
            if (!IsAllowedRelativePath(entry.Path) || !seen.Add(entry.Path))
                throw new InvalidDataException($"Kimi session package path {entry.Path} is not allowed.");
            byte[] content;
            try { content = Convert.FromBase64String(entry.ContentBase64); }
            catch (FormatException exception) { throw new InvalidDataException("Kimi session package content is malformed.", exception); }
            files.Add(new PackagedFile(entry.Path, content));
        }

        if (files.All(file => file.Path != StateFileName))
            throw new InvalidDataException("Kimi session package carries no state.json.");
        if (files.All(file => !file.Path.EndsWith("/" + WireFileName, StringComparison.Ordinal)))
            throw new InvalidDataException("Kimi session package carries no wire.jsonl.");

        var state = NormalizeState(files.First(file => file.Path == StateFileName).Content, dto.Id);
        var cwd = state.Cwd
            ?? throw new InvalidDataException("Kimi session state carries no cwd.");
        return new PackageInfo(dto.Id.ToLowerInvariant(), dto.WorkDirKey, cwd, files);
    }

    /// <summary>
    /// Writes every packaged file into its session directory and merges the session into the
    /// shared index. Each file is replaced through a same-directory move, and the state.json
    /// placeholder is substituted with this machine's absolute session directory before writing.
    /// </summary>
    public static void Materialize(PackageInfo package, KimiPaths paths)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(paths);

        var sessionId = KimiPaths.SessionIdPrefix + package.SessionId;
        var directory = paths.SessionDirectory(package.WorkDirKey, sessionId);
        Directory.CreateDirectory(directory);

        foreach (var file in package.Files)
        {
            var content = file.Content;
            if (StringComparer.Ordinal.Equals(file.Path, StateFileName))
                content = SubstituteSessionDirectory(content, directory);
            ReplaceAtomically(Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar)), content);
        }

        var indexPath = paths.IndexFilePath;
        var current = File.Exists(indexPath) ? File.ReadAllText(indexPath, Utf8) : null;
        // Kimi writes sessionDir with forward slashes even on Windows; match that shape so an
        // import that changes nothing leaves the index bytes alone.
        var merged = KimiSessionIndex.Merge(current,
            KimiSessionIndex.CreateEntry(sessionId, directory.Replace('\\', '/'), package.WorkingDirectory));
        if (string.Equals(current, merged, StringComparison.Ordinal)) return;
        ReplaceAtomically(indexPath, Utf8.GetBytes(merged));
    }

    public static string StateFilePath(string sessionDirectory) => Path.Combine(sessionDirectory, StateFileName);

    /// <summary>
    /// Parses and normalizes state.json: validates the declared id, rewrites every
    /// agents.&lt;id&gt;.homedir to the placeholder (deterministic across machines), and returns the
    /// canonical bytes plus the working directory for the index entry.
    /// </summary>
    private static (byte[] Content, string? Cwd) NormalizeState(byte[] content, string sessionId)
    {
        string text;
        try { text = Utf8.GetString(content); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Kimi session state is not valid UTF-8.", exception);
        }
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException exception) { throw new InvalidDataException("Kimi session state is malformed.", exception); }
        if (node is not JsonObject state)
            throw new InvalidDataException("Kimi session state is not an object.");

        var declared = state.TryGetPropertyValue("id", out var idValue) && idValue is JsonValue id &&
                       id.TryGetValue<string>(out var idText)
            ? idText
            : null;
        if (declared is null)
            throw new InvalidDataException("Kimi session state carries no id.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(declared, KimiPaths.SessionIdPrefix + sessionId) &&
            !StringComparer.OrdinalIgnoreCase.Equals(declared, sessionId))
            throw new InvalidDataException("Kimi session state disagrees with the session directory name.");

        if (state.TryGetPropertyValue("agents", out var agentsValue) && agentsValue is JsonObject agents)
        {
            foreach (var pair in agents.ToList())
            {
                if (pair.Value is not JsonObject agent) continue;
                var agentId = pair.Key;
                agent["homedir"] = $"{HomeDirPlaceholder}/{AgentsDirectoryName}/{agentId}";
            }
        }

        var cwd = state.TryGetPropertyValue("cwd", out var cwdValue) && cwdValue is JsonValue cwdText &&
                  cwdText.TryGetValue<string>(out var cwdString)
            ? cwdString
            : null;
        return (Utf8.GetBytes(state.ToJsonString(JsonOptions)), cwd);
    }

    private static byte[] SubstituteSessionDirectory(byte[] stateContent, string sessionDirectory)
    {
        var text = Utf8.GetString(stateContent);
        if (!text.Contains(HomeDirPlaceholder, StringComparison.Ordinal)) return stateContent;
        // Kimi writes forward slashes even on Windows (state.json on this machine shows C:/...),
        // so the substituted path matches that shape.
        return Utf8.GetBytes(text.Replace(HomeDirPlaceholder,
            sessionDirectory.Replace('\\', '/'), StringComparison.Ordinal));
    }

    private static bool IsAllowedRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == "..")) return false;
        if (segments.Any(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return false;

        if (segments.Length == 1)
            return StringComparer.Ordinal.Equals(segments[0], StateFileName);
        if (segments.Length == 3 &&
            StringComparer.Ordinal.Equals(segments[0], AgentsDirectoryName) &&
            StringComparer.Ordinal.Equals(segments[2], WireFileName))
            return IsPlainComponent(segments[1]);
        if (segments.Length == 4 &&
            StringComparer.Ordinal.Equals(segments[0], AgentsDirectoryName) &&
            StringComparer.Ordinal.Equals(segments[2], PlansDirectoryName))
            return IsPlainComponent(segments[1]) &&
                   StringComparer.Ordinal.Equals(Path.GetExtension(segments[3]), ".md") &&
                   IsPlainComponent(Path.GetFileNameWithoutExtension(segments[3]));
        return false;
    }

    private static bool IsPlainComponent(string segment) =>
        segment.Length != 0 &&
        !segment.Contains('.') &&
        !segment.Contains(' ') &&
        segment[0] != '$';

    private static IEnumerable<string> EnumerateDirectChildren(string directory) =>
        Directory.EnumerateFileSystemEntries(directory)
            .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    private static void ReplaceAtomically(string destination, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record PackageDto(int V, string Id, string WorkDirKey, List<PackagedFileDto> Files);
    private sealed record PackagedFileDto(string Path, string ContentBase64);

    public sealed record PackagedFile(string Path, byte[] Content);

    public sealed record PackageInfo(
        string SessionId,
        string WorkDirKey,
        string WorkingDirectory,
        IReadOnlyList<PackagedFile> Files);
}
