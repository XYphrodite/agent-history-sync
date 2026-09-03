using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Grok;

/// <summary>
/// Compact portable representation of a Grok CLI session for encrypted sync.
/// Syncs chat_history + summary only (not terminal logs / locks / sqlite).
/// </summary>
public static class GrokSessionPackage
{
    public const int SchemaVersion = 1;
    public const string LogicalIdPrefix = "g-";
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

    public static bool IsGrokLogicalId(string value) =>
        value.StartsWith(LogicalIdPrefix, StringComparison.Ordinal) &&
        SessionIdPattern.IsMatch(value[LogicalIdPrefix.Length..]);

    public static string ToLogicalId(string sessionId)
    {
        if (!SessionIdPattern.IsMatch(sessionId))
            throw new ArgumentException("Grok session id must be a UUID.", nameof(sessionId));
        return LogicalIdPrefix + sessionId.ToLowerInvariant();
    }

    public static string SessionIdFromLogicalId(string logicalId)
    {
        if (!IsGrokLogicalId(logicalId))
            throw new ArgumentException("Not a Grok logical object id.", nameof(logicalId));
        return logicalId[LogicalIdPrefix.Length..];
    }

    public static byte[] BuildFromDirectory(string sessionDirectory)
    {
        var chatPath = Path.Combine(sessionDirectory, "chat_history.jsonl");
        if (!File.Exists(chatPath)) throw new FileNotFoundException("Grok chat_history.jsonl is missing.", chatPath);

        var summaryPath = Path.Combine(sessionDirectory, "summary.json");
        string? summaryText = File.Exists(summaryPath) ? File.ReadAllText(summaryPath, Utf8) : null;
        var sessionId = Path.GetFileName(Path.TrimEndingDirectorySeparator(sessionDirectory));
        var cwd = TryReadCwd(summaryText) ?? GuessCwdFromParent(sessionDirectory);

        if (!SessionIdPattern.IsMatch(sessionId))
            throw new InvalidDataException("Grok session directory name is not a UUID.");

        var chat = GrokChatNormalizer.Normalize(File.ReadAllBytes(chatPath));
        // Refuse to build what this same type will not parse. A chat that normalizes to nothing -
        // an empty file, or a session closed before the first turn, whose only records are the
        // system and tool lines the normalizer drops - produced a well-formed package with an
        // empty chatHistory. Parse rejects that, but only on the machine pulling it, and the
        // rejection came out of staging as an unhandled InvalidDataException that took the whole
        // synchronization down. One such session on one machine stopped every other machine from
        // synchronizing anything at all, until somebody deleted the directory by hand.
        if (chat.Length == 0)
            throw new InvalidDataException("Grok session holds no synchronizable chat history.");
        var package = new PackageDto(SchemaVersion, sessionId.ToLowerInvariant(), cwd, Utf8.GetString(chat), summaryText);
        return JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
    }

    public static ContentHash HashPackage(byte[] package) =>
        new(Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant());

    public static PackageInfo Parse(byte[] package)
    {
        PackageDto? dto;
        try { dto = JsonSerializer.Deserialize<PackageDto>(package, JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("Grok session package is malformed.", exception); }
        if (dto is null || dto.V != SchemaVersion) throw new InvalidDataException("Grok session package schema is unsupported.");
        if (!SessionIdPattern.IsMatch(dto.Id)) throw new InvalidDataException("Grok session package id is invalid.");
        if (string.IsNullOrWhiteSpace(dto.Cwd) || string.IsNullOrWhiteSpace(dto.ChatHistory))
            throw new InvalidDataException("Grok session package is incomplete.");
        return new PackageInfo(dto.Id.ToLowerInvariant(), Path.GetFullPath(dto.Cwd), Utf8.GetBytes(NormalizeNewlines(dto.ChatHistory)),
            dto.Summary is null ? null : Utf8.GetBytes(dto.Summary));
    }

    public static void Materialize(PackageInfo package, GrokPaths paths)
    {
        var directory = paths.SessionDirectory(package.Cwd, package.SessionId);
        Directory.CreateDirectory(directory);
        var chatPath = Path.Combine(directory, "chat_history.jsonl");
        var summaryPath = Path.Combine(directory, "summary.json");
        var chatTemp = chatPath + ".tmp";
        var summaryTemp = summaryPath + ".tmp";
        try
        {
            File.WriteAllBytes(chatTemp, package.ChatHistory);
            if (package.Summary is not null) File.WriteAllBytes(summaryTemp, package.Summary);
            File.Move(chatTemp, chatPath, overwrite: true);
            if (package.Summary is not null) File.Move(summaryTemp, summaryPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(chatTemp)) File.Delete(chatTemp);
            if (File.Exists(summaryTemp)) File.Delete(summaryTemp);
        }
    }

    public static string ChatHistoryPath(string sessionDirectory) => Path.Combine(sessionDirectory, "chat_history.jsonl");

    private static string? TryReadCwd(string? summaryText)
    {
        if (string.IsNullOrWhiteSpace(summaryText)) return null;
        try
        {
            using var document = JsonDocument.Parse(summaryText);
            if (document.RootElement.TryGetProperty("info", out var info) &&
                info.TryGetProperty("cwd", out var cwd) &&
                cwd.ValueKind == JsonValueKind.String)
                return cwd.GetString();
            if (document.RootElement.TryGetProperty("cwd", out var rootCwd) && rootCwd.ValueKind == JsonValueKind.String)
                return rootCwd.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static string GuessCwdFromParent(string sessionDirectory)
    {
        var parent = Directory.GetParent(sessionDirectory)?.Name
            ?? throw new InvalidDataException("Grok session directory has no parent cwd segment.");
        return Uri.UnescapeDataString(parent);
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed record PackageDto(int V, string Id, string Cwd, string ChatHistory, string? Summary);

    public sealed record PackageInfo(string SessionId, string Cwd, byte[] ChatHistory, byte[]? Summary);
}
