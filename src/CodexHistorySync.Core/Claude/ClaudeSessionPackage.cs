using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Claude;

/// <summary>
/// Compact portable representation of a Claude Code session for encrypted sync.
/// Syncs one transcript under ~/.claude/projects only (not backups / ide / shell-snapshots).
/// </summary>
public static class ClaudeSessionPackage
{
    public const int SchemaVersion = 1;
    public const string LogicalIdPrefix = "cl-";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex SessionIdPattern = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static bool IsClaudeLogicalId(string value) =>
        value.StartsWith(LogicalIdPrefix, StringComparison.Ordinal) &&
        SessionIdPattern.IsMatch(value[LogicalIdPrefix.Length..]);

    public static string ToLogicalId(string sessionId)
    {
        if (!SessionIdPattern.IsMatch(sessionId))
            throw new ArgumentException("Claude session id must be a UUID.", nameof(sessionId));
        return LogicalIdPrefix + sessionId.ToLowerInvariant();
    }

    public static string SessionIdFromLogicalId(string logicalId)
    {
        if (!IsClaudeLogicalId(logicalId))
            throw new ArgumentException("Not a Claude logical object id.", nameof(logicalId));
        return logicalId[LogicalIdPrefix.Length..];
    }

    public static byte[] BuildFromFile(string sessionFilePath)
    {
        if (!File.Exists(sessionFilePath)) throw new FileNotFoundException("Claude session file is missing.", sessionFilePath);

        var sessionId = Path.GetFileNameWithoutExtension(sessionFilePath);
        if (!SessionIdPattern.IsMatch(sessionId))
            throw new InvalidDataException("Claude session file name is not a UUID.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(sessionFilePath), ".jsonl"))
            throw new InvalidDataException("Claude session file must use the .jsonl extension.");

        var project = Directory.GetParent(sessionFilePath)?.Name
            ?? throw new InvalidDataException("Claude session file has no project directory.");
        PathSafety.ValidateFileComponent(project, nameof(project));

        var transcript = NormalizeNewlines(File.ReadAllText(sessionFilePath, Utf8));
        var cwd = ReadCwd(transcript, sessionId);

        var package = new PackageDto(SchemaVersion, sessionId.ToLowerInvariant(), cwd, project, transcript);
        return JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
    }

    public static ContentHash HashPackage(byte[] package) =>
        new(Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant());

    public static PackageInfo Parse(byte[] package)
    {
        PackageDto? dto;
        try { dto = JsonSerializer.Deserialize<PackageDto>(package, JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("Claude session package is malformed.", exception); }
        if (dto is null || dto.V != SchemaVersion) throw new InvalidDataException("Claude session package schema is unsupported.");
        if (!SessionIdPattern.IsMatch(dto.Id)) throw new InvalidDataException("Claude session package id is invalid.");
        if (string.IsNullOrWhiteSpace(dto.Cwd) || string.IsNullOrWhiteSpace(dto.Transcript))
            throw new InvalidDataException("Claude session package is incomplete.");
        try { PathSafety.ValidateFileComponent(dto.Project, nameof(dto.Project)); }
        catch (ArgumentException exception) { throw new InvalidDataException("Claude session package project segment is unsafe.", exception); }
        return new PackageInfo(dto.Id.ToLowerInvariant(), dto.Cwd, dto.Project, Utf8.GetBytes(NormalizeNewlines(dto.Transcript)));
    }

    public static void Materialize(PackageInfo package, ClaudePaths paths)
    {
        var destination = paths.SessionFilePath(package.Project, package.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, package.Transcript);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Reads the authoritative cwd out of the records. Only user, assistant, and attachment
    /// records carry one, so the first record that has it wins; a session without any is not
    /// syncable, because the project directory name cannot be reversed into a cwd (design D1).
    /// Drive-letter casing varies between records and is preserved as written.
    /// </summary>
    private static string ReadCwd(string transcript, string sessionId)
    {
        string? cwd = null;
        foreach (var line in transcript.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (document.RootElement.TryGetProperty("sessionId", out var recordId) &&
                    recordId.ValueKind == JsonValueKind.String &&
                    !StringComparer.OrdinalIgnoreCase.Equals(recordId.GetString(), sessionId))
                    throw new InvalidDataException("Claude session records disagree with the session file name.");
                if (cwd is null &&
                    document.RootElement.TryGetProperty("cwd", out var recordCwd) &&
                    recordCwd.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(recordCwd.GetString()))
                    cwd = recordCwd.GetString();
            }
        }

        return cwd ?? throw new InvalidDataException("Claude session records carry no cwd.");
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed record PackageDto(int V, string Id, string Cwd, string Project, string Transcript);

    public sealed record PackageInfo(string SessionId, string Cwd, string Project, byte[] Transcript);
}
