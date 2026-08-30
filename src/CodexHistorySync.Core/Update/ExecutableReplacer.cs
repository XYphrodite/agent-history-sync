namespace CodexHistorySync.Core.Update;

public interface IExecutableReplacer
{
    /// <summary>
    /// Puts <paramref name="stagedPath"/> in place of <paramref name="currentPath"/> and returns
    /// the path the previous binary was retired to.
    /// </summary>
    string Replace(string currentPath, string stagedPath);

    /// <summary>Puts a retired binary back, used when the freshly installed one fails its probe.</summary>
    void Restore(string retiredPath, string currentPath);

    /// <summary>Deletes retired copies left behind by earlier updates. Best effort by design.</summary>
    int RemoveRetiredCopies(string currentPath);
}

/// <summary>
/// Swaps the executable this process is running from. Windows refuses to overwrite a running
/// image but allows renaming it, so the update is a rename followed by a move: the old binary
/// steps aside under a <c>.old-*</c> name — still mapped by this process, and therefore not
/// deletable until the next run — and the new one takes the original path. Every path the
/// destination could fail on is reversed, because a half-applied update leaves the machine
/// with no agent-sync at all.
/// </summary>
public sealed class ExecutableReplacer : IExecutableReplacer
{
    private const string RetiredSuffix = ".old-";

    public string Replace(string currentPath, string stagedPath)
    {
        var current = RequireFullPath(currentPath, nameof(currentPath));
        var staged = RequireFullPath(stagedPath, nameof(stagedPath));
        if (!File.Exists(staged)) throw new FileNotFoundException("The staged binary is missing.", staged);
        if (!File.Exists(current)) throw new FileNotFoundException("The installed binary is missing.", current);

        var retired = current + RetiredSuffix + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",
            System.Globalization.CultureInfo.InvariantCulture);
        File.Move(current, retired);
        try
        {
            File.Move(staged, current);
        }
        catch
        {
            // The destination is empty at this point; putting the old binary back is the only
            // outcome that leaves a working installation behind.
            File.Move(retired, current);
            throw;
        }

        return retired;
    }

    public void Restore(string retiredPath, string currentPath)
    {
        var retired = RequireFullPath(retiredPath, nameof(retiredPath));
        var current = RequireFullPath(currentPath, nameof(currentPath));
        if (!File.Exists(retired)) throw new FileNotFoundException("The retired binary is missing.", retired);

        if (File.Exists(current)) File.Delete(current);
        File.Move(retired, current);
    }

    public int RemoveRetiredCopies(string currentPath)
    {
        var current = RequireFullPath(currentPath, nameof(currentPath));
        var directory = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;

        var removed = 0;
        var prefix = Path.GetFileName(current) + RetiredSuffix;
        foreach (var candidate in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            try
            {
                File.Delete(candidate);
                removed++;
            }
            catch (IOException)
            {
                // Still mapped by a running process — the next run collects it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static string RequireFullPath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("The path must be fully qualified.", parameterName);
        return Path.GetFullPath(value);
    }
}
