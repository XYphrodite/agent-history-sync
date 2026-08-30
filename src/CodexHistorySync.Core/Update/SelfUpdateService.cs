using System.Security.Cryptography;

namespace CodexHistorySync.Core.Update;

public sealed record SelfUpdateRequest(bool CheckOnly = false, string? Tag = null);

public enum SelfUpdateStatus
{
    /// <summary>Nothing newer is published; nothing was downloaded.</summary>
    AlreadyCurrent,

    /// <summary>A newer release exists and the caller only asked to look.</summary>
    UpdateAvailable,

    /// <summary>The installed binary was replaced and answered its post-install probe.</summary>
    Updated
}

public sealed record SelfUpdateReport(SelfUpdateStatus Status, ReleaseVersion Installed, ReleaseVersion Release, string Tag)
{
    /// <summary>Retired binaries from earlier updates that this run managed to delete.</summary>
    public int RetiredCopiesRemoved { get; init; }
}

/// <summary>
/// Replaces the running agent-sync with a published release.
///
/// The rules that matter are all refusals. A release whose tag cannot be ordered is refused
/// instead of guessed at. A download without a matching SHA-256 is refused instead of
/// installed. A file that is not a Windows executable is refused before it can take the place
/// of one. And a new binary that cannot answer a trivial probe is rolled back, because the
/// failure mode this command must never have is leaving the machine unable to run agent-sync.
/// </summary>
public sealed class SelfUpdateService
{
    private const string StagingPrefix = ".agent-sync-update-";

    private readonly string executablePath;
    private readonly ReleaseVersion installedVersion;
    private readonly IReleaseSource source;
    private readonly IExecutableReplacer replacer;
    private readonly Func<string, CancellationToken, Task<bool>>? probe;

    public SelfUpdateService(
        string executablePath,
        ReleaseVersion installedVersion,
        IReleaseSource source,
        IExecutableReplacer? replacer = null,
        Func<string, CancellationToken, Task<bool>>? probe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("The executable path must be fully qualified.", nameof(executablePath));

        this.executablePath = Path.GetFullPath(executablePath);
        this.installedVersion = installedVersion;
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.replacer = replacer ?? new ExecutableReplacer();
        this.probe = probe;
    }

    public async Task<SelfUpdateReport> UpdateAsync(SelfUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Collected on every run, including a check: the copy an earlier update retired stays
        // mapped by that run's own process and can only be deleted by a later one.
        var removed = replacer.RemoveRetiredCopies(executablePath);

        var release = await source.ResolveAsync(request.Tag, cancellationToken).ConfigureAwait(false);
        if (release is null) throw new InvalidDataException("The release source returned no release.");

        // An explicit tag is an instruction, not a comparison: pinning is how a bad release is
        // undone, so it installs even when it is the same version or older.
        if (request.Tag is null && release.Version <= installedVersion)
            return Report(SelfUpdateStatus.AlreadyCurrent, release, removed);

        if (request.CheckOnly)
            return Report(SelfUpdateStatus.UpdateAvailable, release, removed);

        await ApplyAsync(release, cancellationToken).ConfigureAwait(false);
        return Report(SelfUpdateStatus.Updated, release, removed);
    }

    private async Task ApplyAsync(ReleaseDescriptor release, CancellationToken cancellationToken)
    {
        var installDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("The install directory could not be determined.");

        // Staged beside the target rather than in the temp directory, so the final move stays
        // inside one volume instead of becoming a copy that can fail halfway.
        var staging = Path.Combine(installDirectory, StagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var staged = Path.Combine(staging, Path.GetFileName(executablePath));
            await source.DownloadAsync(release.ExecutableUrl, staged, cancellationToken).ConfigureAwait(false);
            var checksum = await source.ReadTextAsync(release.ChecksumUrl, cancellationToken).ConfigureAwait(false);

            await VerifyAsync(staged, ReleaseChecksum.Parse(checksum), cancellationToken).ConfigureAwait(false);

            // Run the release where it was staged, before anything is replaced. Doing this only
            // afterwards is not equivalent: a single-file host reads its bundled assemblies back
            // out of its own file, so a process that has just renamed itself away can no longer
            // load anything it has not already touched — starting with the process API needed to
            // run the probe at all.
            if (probe is not null && !await probe(staged, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The downloaded release did not run.");

            var retired = replacer.Replace(executablePath, staged);
            if (probe is null) return;

            // The same check from the installed path, which is the one that has to work. It can
            // only be made here because the staging run already loaded what running it needs.
            bool healthy;
            try
            {
                healthy = await probe(executablePath, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                replacer.Restore(retired, executablePath);
                throw;
            }

            if (!healthy)
            {
                replacer.Restore(retired, executablePath);
                throw new InvalidDataException("The downloaded release did not run after installation.");
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task VerifyAsync(string staged, string expectedHash, CancellationToken cancellationToken)
    {
        var info = new FileInfo(staged);
        if (!info.Exists || info.Length == 0)
            throw new InvalidDataException("The downloaded release is empty.");

        string actual;
        await using (var stream = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var header = new byte[2];
            if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) != 2 ||
                header[0] != (byte)'M' || header[1] != (byte)'Z')
                throw new InvalidDataException("The downloaded release is not a Windows executable.");

            stream.Position = 0;
            actual = ReleaseChecksum.Format(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        if (!ReleaseChecksum.Matches(expectedHash, actual))
            throw new InvalidDataException("The downloaded release failed its checksum.");
    }

    private SelfUpdateReport Report(SelfUpdateStatus status, ReleaseDescriptor release, int removed) =>
        new(status, installedVersion, release.Version, release.Tag) { RetiredCopiesRemoved = removed };
}
