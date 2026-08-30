namespace CodexHistorySync.Core.Update;

/// <summary>
/// Reads the <c>agent-sync.exe.sha256</c> asset, which the release workflow writes as
/// "&lt;hash&gt;  agent-sync.exe". Anything else is rejected rather than interpreted: a checksum
/// this code cannot read must stop the update, not be skipped.
/// </summary>
public static class ReleaseChecksum
{
    public static string Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("The release checksum is empty.");

        var token = content.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || token.Length != 64 || !token.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("The release checksum is malformed.");

        return token.ToLowerInvariant();
    }

    public static string Format(ReadOnlySpan<byte> hash) => Convert.ToHexString(hash).ToLowerInvariant();

    public static bool Matches(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
