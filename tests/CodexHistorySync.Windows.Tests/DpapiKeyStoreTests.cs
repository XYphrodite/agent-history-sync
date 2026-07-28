using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using CodexHistorySync.Windows;

namespace CodexHistorySync.Windows.Tests;

public sealed class DpapiKeyStoreTests
{
    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsKeyWithoutStoringRawBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-{Guid.NewGuid():N}");
        var key = RandomNumberGenerator.GetBytes(32);

        try
        {
            var store = new DpapiKeyStore(testRoot);
            await store.SaveAsync("owner/repository", key, CancellationToken.None);

            var loaded = await store.LoadAsync("owner/repository", CancellationToken.None);
            var rawFile = await File.ReadAllBytesAsync(
                Assert.Single(Directory.GetFiles(testRoot)),
                CancellationToken.None);

            Assert.Equal(key, loaded);
            Assert.False(rawFile.AsSpan().IndexOf(key) >= 0);
            CryptographicOperations.ZeroMemory(rawFile);

            await store.DeleteAsync("owner/repository", CancellationToken.None);
            Assert.Null(await store.LoadAsync("owner/repository", CancellationToken.None));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_ContainsRepositoryIdWithinConfiguredKeyDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-{Guid.NewGuid():N}");
        var key = RandomNumberGenerator.GetBytes(32);

        try
        {
            var store = new DpapiKeyStore(testRoot);
            await store.SaveAsync("..\\..\\escaped", key, CancellationToken.None);

            Assert.Empty(Directory.GetDirectories(testRoot));
            Assert.Single(Directory.GetFiles(testRoot));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SaveAsync_RejectsDistinctMalformedUtf16RepositoryIdsBeforeCreatingFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-{Guid.NewGuid():N}");
        var key = RandomNumberGenerator.GetBytes(32);
        var malformedRepositoryIds = new[] { "\uD800", "\uD801" };

        try
        {
            var store = new DpapiKeyStore(testRoot);

            foreach (var repositoryId in malformedRepositoryIds)
            {
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    store.SaveAsync(repositoryId, key, CancellationToken.None));
            }

            Assert.False(Directory.Exists(testRoot));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task LoadAndDeleteAsync_RejectMalformedUtf16RepositoryIdBeforeFilesystemAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-{Guid.NewGuid():N}");
        var store = new DpapiKeyStore(testRoot);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.LoadAsync("\uD800", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DeleteAsync("\uD801", CancellationToken.None));
        Assert.False(Directory.Exists(testRoot));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Constructor_RejectsEmptyLocalAppDataBeforeBuildingRelativePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var originalWorkingDirectory = Environment.CurrentDirectory;
        var testWorkingDirectory = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-cwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testWorkingDirectory);

        try
        {
            Environment.CurrentDirectory = testWorkingDirectory;

            Assert.Throws<InvalidOperationException>(() =>
                new DpapiKeyStore(
                    keyDirectory: null,
                    localAppDataProvider: static () => string.Empty));
            Assert.False(Directory.Exists(Path.Combine(testWorkingDirectory, "CodexHistorySync")));
        }
        finally
        {
            Environment.CurrentDirectory = originalWorkingDirectory;
            Directory.Delete(testWorkingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ProtectsDirectoryAndFileAclsForCurrentOwnerOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-{Guid.NewGuid():N}");
        var key = RandomNumberGenerator.GetBytes(32);

        try
        {
            var store = new DpapiKeyStore(testRoot);
            await store.SaveAsync("owner/repository", key, CancellationToken.None);
            using var identity = WindowsIdentity.GetCurrent();
            var owner = Assert.IsType<SecurityIdentifier>(identity.User);

            AssertOwnerOnly(new DirectoryInfo(testRoot).GetAccessControl(), owner);
            AssertOwnerOnly(new FileInfo(Assert.Single(Directory.GetFiles(testRoot))).GetAccessControl(), owner);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnly(FileSystemSecurity security, SecurityIdentifier owner)
    {
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(owner, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var rule = Assert.Single(rules);
        Assert.Equal(owner, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
    }
}
