using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Claude;

/// <summary>
/// One Claude Code memory file for encrypted sync. The traveling bytes are the markdown itself
/// after newline normalization, not a second encoding: the hash the scanner publishes has to be
/// the hash a backup of the file will recompute, or an interrupted mutation cannot roll back.
/// Project and name travel in the logical id, because both are needed to place the file and
/// neither can be recovered from the body.
/// There is no truncation. These files are small, and a limiter that is not a fixed point is
/// how defect 4 left imported sessions in permanent conflict.
/// </summary>
public static class ClaudeMemoryPackage
{
    public const string LogicalIdPrefix = "cm-";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex HexPattern = new("^[0-9a-f]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsClaudeMemoryLogicalId(string value) => TryParseLogicalId(value, out _, out _);

    public static string ToLogicalId(string project, string name)
    {
        PathSafety.ValidateFileComponent(project, nameof(project));
        PathSafety.ValidateFileComponent(name, nameof(name));
        return LogicalIdPrefix + Convert.ToHexString(Utf8.GetBytes(project)).ToLowerInvariant() + "." + name;
    }

    public static bool TryParseLogicalId(string? value, out string project, out string name)
    {
        project = string.Empty;
        name = string.Empty;
        if (value is null || !value.StartsWith(LogicalIdPrefix, StringComparison.Ordinal)) return false;
        var rest = value[LogicalIdPrefix.Length..];
        var separator = rest.IndexOf('.');
        if (separator <= 0 || separator == rest.Length - 1) return false;
        var hex = rest[..separator];
        if (hex.Length % 2 != 0 || !HexPattern.IsMatch(hex)) return false;
        name = rest[(separator + 1)..];
        try
        {
            project = Utf8.GetString(Convert.FromHexString(hex));
            PathSafety.ValidateFileComponent(project, nameof(project));
            PathSafety.ValidateFileComponent(name, nameof(name));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or DecoderFallbackException)
        {
            project = string.Empty;
            name = string.Empty;
            return false;
        }
        return true;
    }

    public static byte[] BuildFromFile(string memoryFilePath)
    {
        if (!File.Exists(memoryFilePath)) throw new FileNotFoundException("Claude memory file is missing.", memoryFilePath);
        var (project, name) = ReadLocation(memoryFilePath);
        return Build(project, name, File.ReadAllText(memoryFilePath, Utf8));
    }

    public static byte[] Build(string project, string name, string body)
    {
        PathSafety.ValidateFileComponent(project, nameof(project));
        PathSafety.ValidateFileComponent(name, nameof(name));
        var normalized = NormalizeNewlines(body);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidDataException("Claude memory file is empty.");
        return Utf8.GetBytes(normalized);
    }

    public static ContentHash HashPackage(byte[] package) =>
        new(Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant());

    public static PackageInfo Parse(byte[] package, string logicalId)
    {
        if (!TryParseLogicalId(logicalId, out var project, out var name))
            throw new InvalidDataException("Claude memory logical object id is invalid.");
        string body;
        try { body = NormalizeNewlines(Utf8.GetString(package)); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Claude memory package is not strict UTF-8.", exception);
        }
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidDataException("Claude memory package is incomplete.");
        return new PackageInfo(project, name, body);
    }

    public static void Materialize(PackageInfo package, ClaudePaths paths)
    {
        var destination = paths.MemoryFilePath(package.Project, package.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, Utf8.GetBytes(package.Body));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static (string Project, string Name) ReadLocation(string memoryFilePath)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(memoryFilePath), ".md"))
            throw new InvalidDataException("Claude memory file must use the .md extension.");
        var name = Path.GetFileNameWithoutExtension(memoryFilePath);
        var memoryDirectory = Path.GetDirectoryName(memoryFilePath);
        if (string.IsNullOrWhiteSpace(memoryDirectory) ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(memoryDirectory), "memory"))
            throw new InvalidDataException("Claude memory file must sit in a memory directory.");
        var projectDirectory = Path.GetDirectoryName(memoryDirectory);
        var project = string.IsNullOrWhiteSpace(projectDirectory) ? null : Path.GetFileName(projectDirectory);
        if (string.IsNullOrWhiteSpace(project))
            throw new InvalidDataException("Claude memory file has no project directory.");
        try
        {
            PathSafety.ValidateFileComponent(project, nameof(project));
            PathSafety.ValidateFileComponent(name, nameof(name));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Claude memory file path is unsafe.", exception);
        }
        return (project, name);
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public sealed record PackageInfo(string Project, string Name, string Body);
}
