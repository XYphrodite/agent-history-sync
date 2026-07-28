using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace CodexHistorySync.Windows;

public interface IKeyStore
{
    Task SaveAsync(string repositoryId, ReadOnlyMemory<byte> key, CancellationToken ct);

    Task<byte[]?> LoadAsync(string repositoryId, CancellationToken ct);

    Task DeleteAsync(string repositoryId, CancellationToken ct);
}

[SupportedOSPlatform("windows")]
public sealed class DpapiKeyStore : IKeyStore
{
    private const int KeySize = 32;
    private const int MaximumRepositoryIdCharacters = 1_024;
    private const long MaximumProtectedKeyBytes = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly string keyDirectory;

    public DpapiKeyStore(string? keyDirectory = null)
        : this(
            keyDirectory,
            static () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    internal DpapiKeyStore(string? keyDirectory, Func<string> localAppDataProvider)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The DPAPI key store requires Windows.");
        }

        ArgumentNullException.ThrowIfNull(localAppDataProvider);
        if (keyDirectory is not null)
        {
            if (string.IsNullOrWhiteSpace(keyDirectory))
            {
                throw new ArgumentException("The key directory must not be empty.", nameof(keyDirectory));
            }

            this.keyDirectory = keyDirectory;
            return;
        }

        var localAppData = localAppDataProvider();
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        this.keyDirectory = Path.Combine(localAppData, "CodexHistorySync", "keys");
    }

    public async Task SaveAsync(string repositoryId, ReadOnlyMemory<byte> key, CancellationToken ct)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"The cached master key must be exactly {KeySize} bytes.", nameof(key));
        }

        ct.ThrowIfCancellationRequested();
        var (path, entropy) = GetPathAndEntropy(repositoryId);
        var keyCopy = key.ToArray();
        byte[]? protectedKey = null;
        string? temporaryPath = null;

        try
        {
            protectedKey = ProtectedData.Protect(keyCopy, entropy, DataProtectionScope.CurrentUser);
            EnsureOwnerOnlyDirectory();
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, protectedKey, ct).ConfigureAwait(false);
            SetOwnerOnlyFileAcl(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
            CryptographicOperations.ZeroMemory(entropy);
            if (protectedKey is not null)
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }

            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public async Task<byte[]?> LoadAsync(string repositoryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (path, entropy) = GetPathAndEntropy(repositoryId);
        byte[]? protectedKey = null;

        try
        {
            FileStream source;
            try
            {
                source = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 4_096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }

            await using (source)
            {
                var length = source.Length;
                if (length is <= 0 or > MaximumProtectedKeyBytes)
                {
                    throw new CryptographicException("The protected key file has an invalid length.");
                }

                protectedKey = new byte[(int)length];
                var totalRead = 0;
                while (totalRead < protectedKey.Length)
                {
                    var read = await source.ReadAsync(protectedKey.AsMemory(totalRead), ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new CryptographicException("The protected key file is truncated.");
                    }

                    totalRead += read;
                }
            }

            byte[]? key = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                key = ProtectedData.Unprotect(protectedKey, entropy, DataProtectionScope.CurrentUser);
                ct.ThrowIfCancellationRequested();
                if (key.Length != KeySize)
                {
                    throw new CryptographicException("The protected key has an invalid length.");
                }

                var result = key;
                key = null;
                return result;
            }
            finally
            {
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
            if (protectedKey is not null)
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }
        }
    }

    public Task DeleteAsync(string repositoryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (path, entropy) = GetPathAndEntropy(repositoryId);
        try
        {
            File.Delete(path);
            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private (string Path, byte[] Entropy) GetPathAndEntropy(string repositoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        if (repositoryId.Length > MaximumRepositoryIdCharacters)
        {
            throw new ArgumentException("Repository ID is too long.", nameof(repositoryId));
        }

        byte[] entropy;
        try
        {
            entropy = StrictUtf8.GetBytes(repositoryId);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Repository ID must contain valid UTF-16 text.",
                nameof(repositoryId),
                exception);
        }

        var fileId = SHA256.HashData(entropy);
        try
        {
            var fileName = Convert.ToHexStringLower(fileId) + ".key";
            return (Path.Combine(keyDirectory, fileName), entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileId);
        }
    }

    private void EnsureOwnerOnlyDirectory()
    {
        var directory = Directory.CreateDirectory(keyDirectory);
        var owner = GetCurrentOwner();
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static void SetOwnerOnlyFileAcl(string path)
    {
        var owner = GetCurrentOwner();
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static SecurityIdentifier GetCurrentOwner()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new InvalidOperationException("The current Windows user has no security identifier.");
    }
}
