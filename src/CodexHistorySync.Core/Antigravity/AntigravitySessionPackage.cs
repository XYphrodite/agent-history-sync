using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Antigravity;

/// <summary>
/// Google Antigravity session package for deterministic, encrypted synchronization.
/// Normalizes conversation summaries, step trajectories, optional protobuf annotations,
/// and brain transcripts/artifacts into canonical JSON representation without local SQLite rowids.
/// </summary>
public static class AntigravitySessionPackage
{
    public const int SchemaVersion = 1;
    public const string LogicalIdPrefix = "ag-";

    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool IsAntigravityLogicalId(string value) => TrySplitLogicalId(value, out _, out _);

    public static string ToLogicalId(string sessionId)
    {
        if (!AntigravityPaths.IsSessionId(sessionId))
            throw new ArgumentException("Antigravity session id is invalid.", nameof(sessionId));
        return LogicalIdPrefix + sessionId.ToLowerInvariant();
    }

    public static bool TrySplitLogicalId(string value, out string sessionId)
    {
        sessionId = string.Empty;
        if (string.IsNullOrEmpty(value) || !value.StartsWith(LogicalIdPrefix, StringComparison.Ordinal))
            return false;
        var body = value[LogicalIdPrefix.Length..];
        if (!AntigravityPaths.IsSessionId(body))
            return false;
        sessionId = body.ToLowerInvariant();
        return true;
    }

    public static bool TrySplitLogicalId(string value, out string profile, out string sessionId)
    {
        profile = "default";
        return TrySplitLogicalId(value, out sessionId);
    }

    public static string LogicalIdOf(byte[] package)
    {
        var dto = Parse(package);
        return ToLogicalId(dto.Id);
    }

    public static byte[] BuildFromLocal(AntigravityPaths paths, string sessionId)
    {
        using var memory = new MemoryStream();
        WriteFromLocal(memory, paths, sessionId);
        return memory.ToArray();
    }

    internal static (ContentHash Hash, long Length)? TryHashAndMeasure(
        AntigravityPaths paths,
        string sessionId,
        bool usePreloadedSummary = false,
        AntigravitySummaryRow? preloadedSummary = null)
    {
        try
        {
            using var sink = new HashingCountingStream();
            WriteFromLocal(sink, paths, sessionId, usePreloadedSummary, preloadedSummary);
            return sink.Complete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or SqliteException or ArgumentException or DecoderFallbackException)
        {
            return null;
        }
    }

    internal static void WriteFromLocal(
        Stream destination,
        AntigravityPaths paths,
        string sessionId,
        bool usePreloadedSummary = false,
        AntigravitySummaryRow? preloadedSummary = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(paths);
        if (!AntigravityPaths.IsSessionId(sessionId))
            throw new ArgumentException("Antigravity session id is invalid.", nameof(sessionId));

        var normalizedId = sessionId.ToLowerInvariant();
        AntigravitySummaryRow? summary = null;
        if (usePreloadedSummary)
        {
            summary = preloadedSummary;
        }
        else if (File.Exists(paths.ConversationSummariesDbPath))
        {
            var summaries = AntigravityDatabase.ReadSummaries(paths.ConversationSummariesDbPath);
            summary = summaries.FirstOrDefault(s => string.Equals(s.ConversationId, normalizedId, StringComparison.OrdinalIgnoreCase));
        }

        var convDb = paths.ConversationDatabasePath(normalizedId);
        var convDbExists = File.Exists(convDb);

        if (summary is null && !convDbExists)
            throw new InvalidDataException($"Antigravity session '{normalizedId}' was not found.");

        var trajectoryMeta = convDbExists
            ? AntigravityDatabase.ReadTrajectoryMeta(convDb)
            : null;

        using var writer = new Utf8JsonWriter(destination, WriterOptions);
        writer.WriteStartObject();
        writer.WriteNumber("v", SchemaVersion);
        writer.WriteString("id", normalizedId);

        if (summary is not null)
        {
            var uris = (summary.WorkspaceUris ?? Array.Empty<string>())
                .OrderBy(u => u, StringComparer.Ordinal)
                .ToArray();

            writer.WriteStartObject("summary");
            writer.WriteString("ConversationId", normalizedId);
            if (summary.ParentConversationId is not null)
                writer.WriteString("ParentConversationId", summary.ParentConversationId);
            writer.WriteNumber("NestingDepth", summary.NestingDepth);
            if (summary.AgentName is not null)
                writer.WriteString("AgentName", summary.AgentName);
            if (summary.Title is not null)
                writer.WriteString("Title", summary.Title);
            if (summary.Status is not null)
                writer.WriteString("Status", summary.Status);
            writer.WriteStartArray("WorkspaceUris");
            foreach (var uri in uris)
            {
                if (uri is null) writer.WriteNullValue();
                else writer.WriteStringValue(uri);
            }
            writer.WriteEndArray();
            writer.WriteNumber("CreatedAtUnix", summary.CreatedAt.ToUnixTimeSeconds());
            writer.WriteNumber("UpdatedAtUnix", summary.UpdatedAt.ToUnixTimeSeconds());
            writer.WriteEndObject();
        }

        if (trajectoryMeta is not null)
        {
            writer.WriteStartObject("trajectory");
            writer.WriteString("ConversationId", normalizedId);
            if (trajectoryMeta.TrajectoryId is not null)
                writer.WriteString("TrajectoryId", trajectoryMeta.TrajectoryId);
            writer.WriteEndObject();
        }

        writer.WriteStartArray("steps");
        if (convDbExists)
        {
            AntigravityDatabase.StreamSteps(convDb, (idx, stepType, status, payload) =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("Index", idx);
                writer.WriteNumber("StepType", stepType);
                writer.WriteNumber("Status", status);
                if (payload is { Length: > 0 })
                {
                    writer.WriteBase64String("PayloadBase64", payload);
                }
                writer.WriteEndObject();
                if (writer.BytesPending >= 65_536)
                {
                    writer.Flush();
                }
            });
        }
        writer.WriteEndArray();

        var annotationPath = paths.AnnotationPath(normalizedId);
        if (File.Exists(annotationPath))
        {
            var annotation = File.ReadAllText(annotationPath).Replace("\r\n", "\n");
            writer.WriteString("annotation", annotation);
            if (writer.BytesPending >= 65_536)
            {
                writer.Flush();
            }
        }

        var brainDir = paths.BrainSessionDirectory(normalizedId);
        var brainDirExists = Directory.Exists(brainDir);
        string? canonicalTranscriptPath = null;
        if (brainDirExists)
        {
            var transcriptPath = paths.TranscriptPath(normalizedId);
            canonicalTranscriptPath = Path.GetFullPath(transcriptPath);
            if (File.Exists(transcriptPath))
            {
                var transcript = File.ReadAllText(transcriptPath).Replace("\r\n", "\n");
                writer.WriteString("transcript", transcript);
                if (writer.BytesPending >= 65_536)
                {
                    writer.Flush();
                }
            }
        }

        writer.WriteStartArray("artifacts");
        if (brainDirExists)
        {
            var artifactFiles = new List<(string RelativePath, string FullPath)>();
            foreach (var file in Directory.EnumerateFiles(brainDir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), canonicalTranscriptPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var relPath = Path.GetRelativePath(brainDir, file).Replace('\\', '/');
                artifactFiles.Add((relPath, file));
            }

            artifactFiles.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

            foreach (var (relPath, fullPath) in artifactFiles)
            {
                var bytes = File.ReadAllBytes(fullPath);
                writer.WriteStartObject();
                writer.WriteString("RelativePath", relPath);
                if (IsTextFile(bytes))
                {
                    var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
                    writer.WriteString("ContentText", text);
                }
                else
                {
                    writer.WriteBase64String("ContentBase64", bytes);
                }
                writer.WriteEndObject();
                if (writer.BytesPending >= 65_536)
                {
                    writer.Flush();
                }
            }
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();
    }

    internal static byte[] BuildFromLocalViaDtoForTest(AntigravityPaths paths, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!AntigravityPaths.IsSessionId(sessionId))
            throw new ArgumentException("Antigravity session id is invalid.", nameof(sessionId));

        var normalizedId = sessionId.ToLowerInvariant();
        SummaryDto? summaryDto = null;
        if (File.Exists(paths.ConversationSummariesDbPath))
        {
            var summaries = AntigravityDatabase.ReadSummaries(paths.ConversationSummariesDbPath);
            var summary = summaries.FirstOrDefault(s => string.Equals(s.ConversationId, normalizedId, StringComparison.OrdinalIgnoreCase));
            if (summary is not null)
            {
                var uris = (summary.WorkspaceUris ?? Array.Empty<string>())
                    .OrderBy(u => u, StringComparer.Ordinal)
                    .ToArray();

                summaryDto = new SummaryDto(
                    normalizedId,
                    summary.ParentConversationId,
                    summary.NestingDepth,
                    summary.AgentName,
                    summary.Title,
                    summary.Status,
                    uris,
                    summary.CreatedAt.ToUnixTimeSeconds(),
                    summary.UpdatedAt.ToUnixTimeSeconds());
            }
        }

        TrajectoryMetaDto? trajectoryDto = null;
        var stepDtos = new List<StepDto>();
        var convDb = paths.ConversationDatabasePath(normalizedId);
        var convDbExists = File.Exists(convDb);

        if (convDbExists)
        {
            var meta = AntigravityDatabase.ReadTrajectoryMeta(convDb);
            if (meta is not null)
            {
                trajectoryDto = new TrajectoryMetaDto(normalizedId, meta.TrajectoryId);
            }

            var steps = AntigravityDatabase.ReadSteps(convDb);
            foreach (var step in steps)
            {
                var payloadB64 = step.Payload is { Length: > 0 } ? Convert.ToBase64String(step.Payload) : null;
                stepDtos.Add(new StepDto(step.Index, step.StepType, step.Status, payloadB64));
            }
            stepDtos.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        if (summaryDto is null && !convDbExists)
            throw new InvalidDataException($"Antigravity session '{normalizedId}' was not found.");

        string? annotation = null;
        var annotationPath = paths.AnnotationPath(normalizedId);
        if (File.Exists(annotationPath))
        {
            annotation = File.ReadAllText(annotationPath).Replace("\r\n", "\n");
        }

        string? transcript = null;
        var artifacts = new List<ArtifactDto>();
        var brainDir = paths.BrainSessionDirectory(normalizedId);
        if (Directory.Exists(brainDir))
        {
            var transcriptPath = paths.TranscriptPath(normalizedId);
            if (File.Exists(transcriptPath))
            {
                transcript = File.ReadAllText(transcriptPath).Replace("\r\n", "\n");
            }

            foreach (var file in Directory.EnumerateFiles(brainDir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(transcriptPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                var relPath = Path.GetRelativePath(brainDir, file).Replace('\\', '/');
                var bytes = File.ReadAllBytes(file);
                if (IsTextFile(bytes))
                {
                    var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
                    artifacts.Add(new ArtifactDto(relPath, text, null));
                }
                else
                {
                    artifacts.Add(new ArtifactDto(relPath, null, Convert.ToBase64String(bytes)));
                }
            }

            artifacts.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        }

        var document = new PackageDto(
            SchemaVersion,
            normalizedId,
            summaryDto,
            trajectoryDto,
            stepDtos.ToArray(),
            annotation,
            transcript,
            artifacts.ToArray());

        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    public static byte[]? TryBuild(AntigravityPaths paths, string sessionId)
    {
        try
        {
            return BuildFromLocal(paths, sessionId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or SqliteException or ArgumentException or DecoderFallbackException)
        {
            return null;
        }
    }

    public static void Materialize(byte[] packageBytes, AntigravityPaths paths) => Materialize(paths, packageBytes);

    public static void Materialize(AntigravityPaths paths, byte[] packageBytes)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(packageBytes);

        var dto = Parse(packageBytes);
        var sessionId = dto.Id.ToLowerInvariant();

        if (dto.Summary is not null)
        {
            var summaryRow = new AntigravitySummaryRow(
                sessionId,
                dto.Summary.ParentConversationId,
                dto.Summary.NestingDepth,
                dto.Summary.AgentName,
                dto.Summary.Title ?? string.Empty,
                dto.Summary.Status,
                dto.Summary.WorkspaceUris ?? Array.Empty<string>(),
                DateTimeOffset.FromUnixTimeSeconds(dto.Summary.CreatedAtUnix),
                DateTimeOffset.FromUnixTimeSeconds(dto.Summary.UpdatedAtUnix));

            AntigravityDatabase.WriteSummary(paths.ConversationSummariesDbPath, summaryRow);
        }

        var trajId = dto.Trajectory?.TrajectoryId ?? ("traj_" + sessionId);
        var stepRows = (dto.Steps ?? Array.Empty<StepDto>())
            .OrderBy(s => s.Index)
            .Select(s => new AntigravityStepRow(
                s.Index,
                s.StepType,
                s.Status,
                string.IsNullOrEmpty(s.PayloadBase64) ? Array.Empty<byte>() : Convert.FromBase64String(s.PayloadBase64)))
            .ToArray();

        AntigravityDatabase.WriteSteps(paths.ConversationDatabasePath(sessionId), trajId, sessionId, stepRows);

        var annotationPath = paths.AnnotationPath(sessionId);
        if (dto.Annotation is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(annotationPath)!);
            File.WriteAllText(annotationPath, dto.Annotation);
        }
        else if (File.Exists(annotationPath))
        {
            File.Delete(annotationPath);
        }

        var brainDir = paths.BrainSessionDirectory(sessionId);
        var transcriptPath = paths.TranscriptPath(sessionId);
        if (dto.Transcript is not null)
        {
            Directory.CreateDirectory(brainDir);
            File.WriteAllText(transcriptPath, dto.Transcript);
        }
        else if (File.Exists(transcriptPath))
        {
            File.Delete(transcriptPath);
        }

        if (Directory.Exists(brainDir))
        {
            var canonicalTranscript = Path.GetFullPath(transcriptPath);
            foreach (var file in Directory.EnumerateFiles(brainDir, "*", SearchOption.AllDirectories))
            {
                if (!string.Equals(Path.GetFullPath(file), canonicalTranscript, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(brainDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    try { Directory.Delete(dir, false); } catch { }
                }
            }
        }

        if (dto.Artifacts is not null && dto.Artifacts.Length > 0)
        {
            Directory.CreateDirectory(brainDir);
            var canonicalBrainDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(brainDir));
            var brainPrefix = canonicalBrainDir + Path.DirectorySeparatorChar;

            foreach (var artifact in dto.Artifacts)
            {
                if (string.IsNullOrWhiteSpace(artifact.RelativePath))
                    throw new InvalidDataException("Artifact relative path cannot be empty or whitespace.");

                if (Path.IsPathRooted(artifact.RelativePath))
                    throw new InvalidDataException($"Artifact path '{artifact.RelativePath}' must not be rooted.");

                var segments = artifact.RelativePath.Split(new[] { '/', '\\' }, StringSplitOptions.None);
                if (segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    throw new InvalidDataException($"Artifact relative path '{artifact.RelativePath}' contains invalid segments.");

                var relativePath = artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var targetPath = Path.GetFullPath(Path.Combine(brainDir, relativePath));

                if (!targetPath.StartsWith(brainPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Artifact target path '{targetPath}' escapes the brain directory.");

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                if (artifact.ContentText is not null)
                    File.WriteAllText(targetPath, artifact.ContentText);
                else if (artifact.ContentBase64 is not null)
                    File.WriteAllBytes(targetPath, Convert.FromBase64String(artifact.ContentBase64));
            }
        }
    }

    public static ContentHash HashPackage(byte[] packageBytes) =>
        new(Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant());

    public static byte[] Fingerprint(string anchorPath)
    {
        var package = BuildFromAnchor(anchorPath)
            ?? (File.Exists(anchorPath) ? File.ReadAllBytes(anchorPath) : null)
            ?? throw new InvalidDataException("The selected session could not be validated.");
        return SHA256.HashData(package);
    }

    public static ContentHash? HashAnchor(string anchorPath)
    {
        try
        {
            var package = BuildFromAnchor(anchorPath)
                ?? (File.Exists(anchorPath) ? File.ReadAllBytes(anchorPath) : null);
            return package is null ? null : HashPackage(package);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or JsonException or SqliteException or DecoderFallbackException)
        {
            return null;
        }
    }

    public static void MaterializeAnchor(string anchorPath)
    {
        if (!AntigravityPaths.TryParseAnchor(anchorPath, out var home, out var sessionId))
            throw new InvalidDataException("Antigravity anchor path is invalid.");
        var package = File.ReadAllBytes(anchorPath);
        var id = LogicalIdOf(package);
        if (!string.Equals(id, ToLogicalId(sessionId), StringComparison.Ordinal))
            throw new InvalidDataException("Antigravity anchor path does not match the package.");
        Materialize(new AntigravityPaths(home), package);
    }

    public static void WriteAnchorSnapshot(string anchorPath)
    {
        var package = BuildFromAnchor(anchorPath)
            ?? throw new InvalidDataException("Antigravity session is not in the state database.");
        var directory = Path.GetDirectoryName(anchorPath)
            ?? throw new InvalidDataException("Antigravity anchor path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = anchorPath + ".tmp";
        File.WriteAllBytes(temporary, package);
        File.Move(temporary, anchorPath, overwrite: true);
    }

    public static void DeleteAnchor(string anchorPath)
    {
        if (!AntigravityPaths.TryParseAnchor(anchorPath, out var home, out var sessionId))
            throw new InvalidDataException("Antigravity anchor path is invalid.");
        var paths = new AntigravityPaths(home);

        AntigravityDatabase.DeleteSummary(paths.ConversationSummariesDbPath, sessionId);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var convDb = paths.ConversationDatabasePath(sessionId);
        if (File.Exists(convDb)) File.Delete(convDb);
        if (File.Exists(convDb + "-wal")) File.Delete(convDb + "-wal");
        if (File.Exists(convDb + "-shm")) File.Delete(convDb + "-shm");

        var annotation = paths.AnnotationPath(sessionId);
        if (File.Exists(annotation)) File.Delete(annotation);

        var brainDir = paths.BrainSessionDirectory(sessionId);
        if (Directory.Exists(brainDir)) Directory.Delete(brainDir, recursive: true);
    }

    internal static byte[]? BuildFromAnchor(string anchorPath)
    {
        if (!AntigravityPaths.TryParseAnchor(anchorPath, out var home, out var sessionId))
            return null;
        return TryBuild(new AntigravityPaths(home), sessionId);
    }

    private static PackageDto Parse(byte[] package)
    {
        PackageDto? dto;
        try { dto = JsonSerializer.Deserialize<PackageDto>(package, JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("Antigravity session package is malformed.", exception); }
        if (dto is null || dto.V != SchemaVersion) throw new InvalidDataException("Antigravity session package schema is unsupported.");
        if (!AntigravityPaths.IsSessionId(dto.Id))
            throw new InvalidDataException("Antigravity session package id is invalid.");
        return dto;
    }

    private static bool IsTextFile(byte[] bytes)
    {
        if (bytes.Length == 0) return true;
        if (bytes.AsSpan().Contains((byte)0)) return false;
        try
        {
            _ = Utf8Strict.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private sealed record PackageDto(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("summary")] SummaryDto? Summary,
        [property: JsonPropertyName("trajectory")] TrajectoryMetaDto? Trajectory,
        [property: JsonPropertyName("steps")] StepDto[] Steps,
        [property: JsonPropertyName("annotation")] string? Annotation,
        [property: JsonPropertyName("transcript")] string? Transcript,
        [property: JsonPropertyName("artifacts")] ArtifactDto[] Artifacts);

    private sealed record SummaryDto(
        string ConversationId,
        string? ParentConversationId,
        int NestingDepth,
        string? AgentName,
        string? Title,
        string? Status,
        string[] WorkspaceUris,
        long CreatedAtUnix,
        long UpdatedAtUnix);

    private sealed record TrajectoryMetaDto(
        string ConversationId,
        string TrajectoryId);

    private sealed record StepDto(
        int Index,
        int StepType,
        int Status,
        string? PayloadBase64);

    private sealed record ArtifactDto(
        string RelativePath,
        string? ContentText,
        string? ContentBase64);
}
