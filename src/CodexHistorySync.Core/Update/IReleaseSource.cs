namespace CodexHistorySync.Core.Update;

/// <summary>
/// One published release: the tag it carries, plus the two assets an update needs. The
/// checksum asset is not optional, because the only thing standing between a downloaded blob
/// and the executable this machine runs next is that hash.
/// </summary>
public sealed record ReleaseDescriptor(string Tag, ReleaseVersion Version, Uri ExecutableUrl, Uri ChecksumUrl);

/// <summary>
/// Where releases come from. Kept behind an interface so the update logic — version
/// comparison, checksum enforcement, binary swap — is testable without network access.
/// </summary>
public interface IReleaseSource
{
    /// <summary>Resolves the newest release, or the one carrying <paramref name="tag"/>.</summary>
    Task<ReleaseDescriptor> ResolveAsync(string? tag, CancellationToken cancellationToken);

    /// <summary>Downloads an asset to <paramref name="destinationPath"/>, which must not exist.</summary>
    Task DownloadAsync(Uri address, string destinationPath, CancellationToken cancellationToken);

    /// <summary>Reads a small text asset, such as the checksum file.</summary>
    Task<string> ReadTextAsync(Uri address, CancellationToken cancellationToken);
}
