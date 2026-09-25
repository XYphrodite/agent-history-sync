using System.Globalization;

namespace CodexHistorySync.Core.Muse;

internal sealed record MuseDiskEntry(string Path, long Size, DateTimeOffset Modified)
{
    public static IReadOnlyList<MuseDiskEntry> ParseListing(string listing)
    {
        var entries = new List<MuseDiskEntry>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(listing);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) { Flush(); continue; }
            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator > 0) fields[line[..separator]] = line[(separator + 3)..];
        }
        Flush();
        return entries;

        void Flush()
        {
            if (fields.TryGetValue("Path", out var raw) && fields.GetValueOrDefault("Folder") == "-" &&
                fields.GetValueOrDefault("Mode")?.StartsWith('-') == true &&
                string.IsNullOrEmpty(fields.GetValueOrDefault("Symbolic Link")) &&
                long.TryParse(fields.GetValueOrDefault("Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size >= 0)
            {
                var path = raw.Replace('\\', '/');
                var segments = path.Split('/');
                if (segments.All(segment => segment.Length > 0 && segment is not ("." or "..") &&
                    segment.IndexOfAny([':', '*', '?', '\0']) < 0))
                {
                    var modified = fields.GetValueOrDefault("Modified") ?? "";
                    // ext4 timestamps have nanoseconds; DateTimeOffset accepts seven fractional digits.
                    if (modified.Length > 27) modified = modified[..27];
                    DateTimeOffset.TryParse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time);
                    entries.Add(new MuseDiskEntry(path, size, time));
                }
            }
            fields.Clear();
        }
    }

    public static bool TrySession(string path, string? configuredHome, out string home, out string[] ids)
    {
        home = ""; ids = [];
        var marker = configuredHome is null ? "/.local/share/muse/sessions/" : "/sessions/";
        var index = configuredHome is null ? path.IndexOf(marker, StringComparison.Ordinal)
            : path.StartsWith(configuredHome + marker, StringComparison.Ordinal) ? configuredHome.Length : -1;
        if (index < 0) return false;
        home = configuredHome ?? path[..(index + "/.local/share/muse".Length)];
        var tail = path[(index + marker.Length)..].Split('/');
        var first = tail.Length >= 5 && tail[0].Length == 4 && tail[1].Length == 2 && tail[2].Length == 2 &&
            tail.Take(3).All(part => part.All(char.IsAsciiDigit)) ? 3 : 0;
        if (tail.Length <= first + 1 || tail[^1] != MusePaths.SessionFileName || !Guid.TryParse(tail[first], out _)) return false;
        var chain = new List<string> { tail[first] };
        for (var i = first + 1; i < tail.Length - 1; i += 2)
        {
            if (i + 1 >= tail.Length - 1 || tail[i] != "subagent" || !Guid.TryParse(tail[i + 1], out _)) return false;
            chain.Add(tail[i + 1]);
            if (chain.Count > 32) return false;
        }
        ids = chain.ToArray();
        return true;
    }
}
