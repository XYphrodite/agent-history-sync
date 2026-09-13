using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Cli.Management;
using CodexHistorySync.Cli.Search;
using CodexHistorySync.Cli.Mcp;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Search;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;
using CodexHistorySync.Windows;
using Spectre.Console;

namespace CodexHistorySync.Cli;

internal sealed class WindowsManagedSessionDirectoryDeleter : IManagedSessionDirectoryDeleter
{
    private readonly Func<bool>? afterContainmentValidation;
    private readonly Func<bool>? afterRootPathValidation;
    private readonly Func<bool>? afterPathValidation;
    private readonly Func<bool>? afterTreeCapture;

    public WindowsManagedSessionDirectoryDeleter()
    {
    }

    internal WindowsManagedSessionDirectoryDeleter(
        Func<bool>? afterPathValidation,
        Func<bool>? afterTreeCapture)
        : this(null, null, afterPathValidation, afterTreeCapture)
    {
    }

    internal WindowsManagedSessionDirectoryDeleter(
        Func<bool>? afterRootPathValidation,
        Func<bool>? afterPathValidation,
        Func<bool>? afterTreeCapture)
        : this(null, afterRootPathValidation, afterPathValidation, afterTreeCapture)
    {
    }

    internal WindowsManagedSessionDirectoryDeleter(
        Func<bool>? afterContainmentValidation,
        Func<bool>? afterRootPathValidation,
        Func<bool>? afterPathValidation,
        Func<bool>? afterTreeCapture)
    {
        this.afterContainmentValidation = afterContainmentValidation;
        this.afterRootPathValidation = afterRootPathValidation;
        this.afterPathValidation = afterPathValidation;
        this.afterTreeCapture = afterTreeCapture;
    }

    public Task DeleteAsync(string sessionsRoot, string sessionDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Managed session deletion requires Windows.");
        if (!WindowsOwnedTreeDeleter.TryGetIdentity(sessionsRoot, out var expectedRootIdentity))
            throw new IOException("The sessions root identity is unavailable.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionsRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory));
        if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase) ||
            !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected session directory is outside the sessions root.");
        if (afterContainmentValidation is not null && !afterContainmentValidation())
            throw new IOException("The sessions root changed after containment validation.");

        RequireConcreteAncestors(root, target, afterRootPathValidation);
        if (afterPathValidation is not null && !afterPathValidation())
            throw new IOException("The selected session directory changed before deletion.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!WindowsOwnedTreeDeleter.TryDeleteDescendantTree(
                root,
                target,
                expectedRootIdentity,
                afterTreeCapture,
                () => { cancellationToken.ThrowIfCancellationRequested(); return true; }))
            throw new IOException("The selected session directory could not be deleted safely.");
        return Task.CompletedTask;
    }

    private static void RequireConcreteAncestors(
        string root,
        string target,
        Func<bool>? afterRootPathValidation)
    {
        for (var current = target;; current = Path.GetDirectoryName(current)
                 ?? throw new InvalidDataException("The selected session directory has no sessions-root ancestor."))
        {
            var attributes = File.GetAttributes(current);
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The selected session directory is not a concrete directory.");
            if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) continue;
            if (afterRootPathValidation is not null && !afterRootPathValidation())
                throw new IOException("The sessions root changed during validation.");
            return;
        }
    }

}

