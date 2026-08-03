using CodexHistorySync.Cli;

namespace CodexHistorySync.IntegrationTests;

public sealed class OwnedTemporaryDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-owned-cleanup-{Guid.NewGuid():N}");

    [Fact]
    public void Missing_or_tampered_ownership_marker_never_deletes_tree()
    {
        Directory.CreateDirectory(root);
        foreach (var mutate in new Action<string>[] { File.Delete, path => File.WriteAllText(path, "foreign-owner") })
        {
            var owned = OwnedTemporaryDirectory.Create(root, "codex-history-sync-init-");
            var sentinel = Path.Combine(owned.RootPath, "sentinel.txt");
            File.WriteAllText(sentinel, "keep");
            mutate(owned.MarkerPath);

            Assert.False(owned.TryDelete());
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
    }

    [Fact]
    public void Owned_concrete_tree_is_deleted_leaf_first()
    {
        Directory.CreateDirectory(root);
        var owned = OwnedTemporaryDirectory.Create(root, "codex-history-sync-init-");
        var nested = Directory.CreateDirectory(Path.Combine(owned.RootPath, "repository", ".git", "objects", "aa"));
        var file = Path.Combine(nested.FullName, "read-only");
        File.WriteAllText(file, "data");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Assert.True(owned.TryDelete());
        Assert.False(Directory.Exists(owned.RootPath));
    }

    [Fact]
    public void Reparse_descendant_is_rejected_without_touching_target()
    {
        Directory.CreateDirectory(root);
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside"));
        var outsideFile = Path.Combine(outside.FullName, "keep.txt");
        File.WriteAllText(outsideFile, "keep");
        var owned = OwnedTemporaryDirectory.Create(root, "codex-history-sync-init-");
        try { Directory.CreateSymbolicLink(Path.Combine(owned.RootPath, "linked"), outside.FullName); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw Xunit.Sdk.SkipException.ForSkip($"Symbolic-link creation is unavailable: {exception.GetType().Name}"); }

        Assert.False(owned.TryDelete());
        Assert.Equal("keep", File.ReadAllText(outsideFile));
    }

    [Fact]
    public void Ancestor_swap_after_validation_is_rejected_before_deletion()
    {
        Directory.CreateDirectory(root);
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside"));
        var outsideFile = Path.Combine(outside.FullName, "keep.txt");
        File.WriteAllText(outsideFile, "keep");
        var owned = OwnedTemporaryDirectory.Create(root, "codex-history-sync-init-");
        var preserved = owned.RootPath + ".preserved";

        bool Swap()
        {
            Directory.Move(owned.RootPath, preserved);
            try { Directory.CreateSymbolicLink(owned.RootPath, outside.FullName); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Directory.Move(preserved, owned.RootPath);
                throw Xunit.Sdk.SkipException.ForSkip($"Symbolic-link creation is unavailable: {exception.GetType().Name}");
            }
            return true;
        }

        Assert.False(owned.TryDelete(Swap));
        Assert.Equal("keep", File.ReadAllText(outsideFile));
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(entry);
                else File.Delete(entry);
            }
            else if (!attributes.HasFlag(FileAttributes.Directory)) File.SetAttributes(entry, FileAttributes.Normal);
        }
        Directory.Delete(root, true);
    }
}
