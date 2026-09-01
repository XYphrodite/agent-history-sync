namespace CodexHistorySync.Core.IO;

/// <summary>
/// One rule for "may this string stand as a single file or directory name", shared by every
/// identifier this codebase turns into a path component. It exists so the repository id and the
/// session id cannot drift apart on what they accept.
/// </summary>
internal static class SafeNameComponent
{
    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        value is not ("." or "..");
}
