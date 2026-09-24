namespace CodexHistorySync.Core.Muse;

/// <summary>Visits only the dated session layout; never descends into session-owned data.</summary>
internal static class MuseSessionDiscovery
{
    public static IReadOnlyList<string> MainDirectories(string root, CancellationToken ct)
    {
        var result = new List<string>();
        Visit(root, 0);
        return result;

        void Visit(string parent, int depth)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var directory in ChildDirectories(parent))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);
                if ((depth == 0 || depth == 3) && Guid.TryParse(name, out _))
                    result.Add(directory);
                else if (depth < 3 && name.Length == (depth == 0 ? 4 : 2) && name.All(char.IsAsciiDigit))
                    Visit(directory, depth + 1);
            }
        }
    }

    public static IReadOnlyList<string> ChildDirectories(string root)
    {
        try
        {
            if (!Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return [];
            return Directory.EnumerateDirectories(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            }).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
