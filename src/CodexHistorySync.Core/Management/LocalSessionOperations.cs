using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Management;

internal interface IManagedSessionFingerprintProvider
{
    Task<byte[]> CaptureAsync(string nativePath, ManagedAgent agent, CancellationToken cancellationToken);
    byte[] CaptureImmediate(string nativePath, ManagedAgent agent, CancellationToken cancellationToken);
}

public sealed class LocalSessionOperations : ILocalSessionOperations
{
    private readonly CodexPaths? codexPaths;
    private readonly GrokPaths? grokPaths;
    private readonly ClaudePaths? claudePaths;
    private readonly ContinuePaths? continuePaths;
    private readonly IConversationWriter? continueWriter;
    private readonly IConversationReader continueReader;
    private readonly IManagedSessionActiveState activeState;
    private readonly IManagedSessionDirectoryDeleter directoryDeleter;
    private readonly IConversationWriter? codexWriter;
    private readonly IConversationWriter? grokWriter;
    private readonly IConversationWriter? claudeWriter;
    private readonly IConversationReader codexReader;
    private readonly IConversationReader grokReader;
    private readonly IConversationReader claudeReader;
    private readonly IManagedSessionFingerprintProvider fingerprintProvider;

    private static readonly HashSet<string> ActiveCopyFailures = new(StringComparer.Ordinal)
    {
        "The session is active."
    };

    private static readonly HashSet<string> ChangedCopyFailures = new(StringComparer.Ordinal)
    {
        "The selected session path changed.",
        "The selected session changed during validation."
    };

    private static readonly HashSet<string> DestinationCopyFailures = new(StringComparer.Ordinal)
    {
        "The destination agent is unavailable.",
        "The configured Codex executable is unavailable.",
        "The discovered Codex executable is unavailable."
    };

    private static readonly HashSet<string> IncompatibleCopyFailures = new(StringComparer.Ordinal)
    {
        "The staged Codex conversation failed the compatibility probe."
    };

    private static readonly HashSet<string> UnreadableCopyFailures = new(StringComparer.Ordinal)
    {
        "The session is not readable.",
        "The selected session identity is invalid.",
        "The selected Codex target is invalid.",
        "The selected Codex target is outside its native root.",
        "The selected Grok target is invalid.",
        "The selected Claude target is invalid.",
        "The selected Claude identity is invalid.",
        "Claude conversation is invalid.",
        "The staged Claude conversation failed validation.",
        "The selected Continue target is invalid.",
        "The selected Continue identity is invalid.",
        "The Continue session could not be read as a conversation.",
        "The staged Continue conversation failed validation.",
        "The Continue session index is not a JSON array.",
        "The selected Grok identity is invalid.",
        "The selected agent is invalid.",
        "The selected session identity changed.",
        "The selected Grok session could not be validated.",
        "The selected Grok session contains a reparse point.",
        "The selected session could not be validated.",
        "Codex conversation is invalid.",
        "Grok conversation is invalid.",
        "The staged Codex conversation failed validation.",
        "The staged Grok conversation failed validation.",
        "The portable conversation has an unsupported role."
    };

    public LocalSessionOperations(
        CodexPaths? codexPaths,
        GrokPaths? grokPaths,
        IManagedSessionActiveState activeState,
        IManagedSessionDirectoryDeleter directoryDeleter,
        IConversationWriter? codexWriter,
        IConversationWriter? grokWriter,
        ClaudePaths? claudePaths = null,
        IConversationWriter? claudeWriter = null,
        ContinuePaths? continuePaths = null,
        IConversationWriter? continueWriter = null)
        : this(
            codexPaths,
            grokPaths,
            activeState,
            directoryDeleter,
            codexWriter,
            grokWriter,
            new CodexConversationReader(),
            new GrokConversationReader(),
            null,
            claudePaths,
            claudeWriter,
            new ClaudeConversationReader(),
            continuePaths,
            continueWriter,
            new ContinueConversationReader())
    {
    }

    internal LocalSessionOperations(
        CodexPaths? codexPaths,
        GrokPaths? grokPaths,
        IManagedSessionActiveState activeState,
        IManagedSessionDirectoryDeleter directoryDeleter,
        IConversationWriter? codexWriter,
        IConversationWriter? grokWriter,
        IConversationReader codexReader,
        IConversationReader grokReader,
        IManagedSessionFingerprintProvider? fingerprintProvider = null,
        ClaudePaths? claudePaths = null,
        IConversationWriter? claudeWriter = null,
        IConversationReader? claudeReader = null,
        ContinuePaths? continuePaths = null,
        IConversationWriter? continueWriter = null,
        IConversationReader? continueReader = null)
    {
        this.codexPaths = codexPaths;
        this.grokPaths = grokPaths;
        this.claudePaths = claudePaths;
        this.claudeWriter = claudeWriter;
        this.claudeReader = claudeReader ?? new ClaudeConversationReader();
        this.continuePaths = continuePaths;
        this.continueWriter = continueWriter;
        this.continueReader = continueReader ?? new ContinueConversationReader();
        this.activeState = activeState ?? throw new ArgumentNullException(nameof(activeState));
        this.directoryDeleter = directoryDeleter ?? throw new ArgumentNullException(nameof(directoryDeleter));
        this.codexWriter = codexWriter;
        this.grokWriter = grokWriter;
        this.codexReader = codexReader ?? throw new ArgumentNullException(nameof(codexReader));
        this.grokReader = grokReader ?? throw new ArgumentNullException(nameof(grokReader));
        this.fingerprintProvider = fingerprintProvider ?? SystemFingerprintProvider.Instance;
    }

    public IReadOnlyList<ManagedAgent> AvailableCopyTargets(ManagedSession source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ManagedAgents.Destinations(source.Agent).Where(agent => WriterFor(agent) is not null).ToArray();
    }

    public async Task<string> CopyAsync(ManagedSession source, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(source);
            // Only unambiguous with exactly one other agent configured; the caller picks otherwise.
            var targets = AvailableCopyTargets(source);
            if (targets.Count != 1) throw new InvalidOperationException("The destination agent is unavailable.");
            return await CopyAsync(source, targets[0], cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ManagedSessionOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ManagedSessionOperationException(
                ManagedSessionOperationFailure.Copy,
                ClassifyCopyFailure(exception));
        }
    }

    public async Task<string> CopyAsync(ManagedSession source, ManagedAgent target, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(source);
            if (target == source.Agent) throw new InvalidOperationException("The destination agent is unavailable.");
            var validated = await ReadAndValidateAsync(source, cancellationToken).ConfigureAwait(false);
            var writer = WriterFor(target)
                ?? throw new InvalidOperationException("The destination agent is unavailable.");
            return await CopyAfterFinalValidationAsync(
                    source, validated, writer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ManagedSessionOperationException(
                ManagedSessionOperationFailure.Copy,
                ClassifyCopyFailure(exception));
        }
    }

    public async Task DeleteAsync(ManagedSession source, CancellationToken cancellationToken)
    {
        try
        {
            var validated = await ReadAndValidateAsync(source, cancellationToken).ConfigureAwait(false);
            await DeleteAfterFinalValidationAsync(source, validated, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new ManagedSessionOperationException(ManagedSessionOperationFailure.Delete);
        }
    }

    private IConversationWriter? WriterFor(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => codexWriter,
        ManagedAgent.Grok => grokWriter,
        ManagedAgent.Claude => claudeWriter,
        ManagedAgent.Continue => continueWriter,
        _ => null
    };

    private IConversationReader ReaderFor(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => codexReader,
        ManagedAgent.Grok => grokReader,
        ManagedAgent.Claude => claudeReader,
        ManagedAgent.Continue => continueReader,
        _ => throw new InvalidDataException("The selected agent is invalid.")
    };

    /// <summary>Codex, Claude, and Continue keep one file per session; Grok keeps a directory.</summary>
    internal static bool IsFileBackedAgent(ManagedAgent agent) =>
        agent is ManagedAgent.Codex or ManagedAgent.Claude or ManagedAgent.Continue;

    private async Task<ValidatedSource> ReadAndValidateAsync(
        ManagedSession source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanRead) throw new InvalidOperationException("The session is not readable.");
        if (source.IsActive) throw new InvalidOperationException("The session is active.");

        var target = ValidateTarget(source);
        var before = await fingerprintProvider.CaptureAsync(target.NativePath, source.Agent, cancellationToken)
            .ConfigureAwait(false);
        var reader = ReaderFor(source.Agent);
        var conversation = await reader.ReadAsync(target.NativePath, cancellationToken).ConfigureAwait(false);
        ValidateConversationIdentity(source, conversation);
        conversation = WithCatalogTitle(source, conversation);

        var revalidated = ValidateTarget(source);
        if (!string.Equals(target.NativePath, revalidated.NativePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.Root, revalidated.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected session path changed.");
        var after = await fingerprintProvider.CaptureAsync(revalidated.NativePath, source.Agent, cancellationToken)
            .ConfigureAwait(false);
        if (!before.AsSpan().SequenceEqual(after))
            throw new InvalidDataException("The selected session changed during validation.");

        return new ValidatedSource(revalidated.Root, revalidated.NativePath, conversation, after);
    }

    private async Task<string> CopyAfterFinalValidationAsync(
        ManagedSession source,
        ValidatedSource validated,
        IConversationWriter writer,
        CancellationToken cancellationToken)
    {
        await RequireStableBeforeFinalActiveCheckAsync(source, validated, cancellationToken)
            .ConfigureAwait(false);
        await RequireInactiveAsync(source, validated.NativePath, cancellationToken).ConfigureAwait(false);
        RequireUnchangedImmediately(source, validated, cancellationToken);
        var writeTask = writer.WriteAsync(validated.Conversation, cancellationToken);
        var result = await writeTask.ConfigureAwait(false);
        return result.SessionId;
    }

    private async Task DeleteAfterFinalValidationAsync(
        ManagedSession source,
        ValidatedSource validated,
        CancellationToken cancellationToken)
    {
        await RequireStableBeforeFinalActiveCheckAsync(source, validated, cancellationToken)
            .ConfigureAwait(false);
        await RequireInactiveAsync(source, validated.NativePath, cancellationToken).ConfigureAwait(false);
        RequireUnchangedImmediately(source, validated, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsFileBackedAgent(source.Agent))
        {
            File.Delete(validated.NativePath);
            // Continue keeps a second record of the session in the shared index. Leaving it there
            // would put a row in Continue's list that throws when opened, which is what the
            // extension's own delete avoids by removing both.
            if (source.Agent == ManagedAgent.Continue && continuePaths is not null)
                RemoveFromContinueIndex(source.SessionId);
            return;
        }

        var deleteTask = directoryDeleter.DeleteAsync(
            validated.Root, validated.NativePath, cancellationToken);
        await deleteTask.ConfigureAwait(false);
    }

    private async Task RequireStableBeforeFinalActiveCheckAsync(
        ManagedSession source,
        ValidatedSource validated,
        CancellationToken cancellationToken)
    {
        await RequireUnchangedAsync(source, validated, cancellationToken).ConfigureAwait(false);
        await RequireUnchangedAsync(source, validated, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireUnchangedAsync(
        ManagedSession source,
        ValidatedSource validated,
        CancellationToken cancellationToken)
    {
        var target = ValidateTarget(source);
        if (!string.Equals(target.NativePath, validated.NativePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.Root, validated.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected session path changed.");
        var fingerprint = await fingerprintProvider.CaptureAsync(
                target.NativePath, source.Agent, cancellationToken)
            .ConfigureAwait(false);
        RequireSameTarget(ValidateTarget(source), validated);
        if (!validated.Fingerprint.AsSpan().SequenceEqual(fingerprint))
            throw new InvalidDataException("The selected session changed during validation.");
    }

    private void RequireUnchangedImmediately(
        ManagedSession source,
        ValidatedSource validated,
        CancellationToken cancellationToken)
    {
        var target = ValidateTarget(source);
        RequireSameTarget(target, validated);
        var fingerprint = fingerprintProvider.CaptureImmediate(
            target.NativePath, source.Agent, cancellationToken);
        RequireSameTarget(ValidateTarget(source), validated);
        if (!validated.Fingerprint.AsSpan().SequenceEqual(fingerprint))
            throw new InvalidDataException("The selected session changed during validation.");
    }

    private static void RequireSameTarget(Target target, ValidatedSource validated)
    {
        if (!string.Equals(target.NativePath, validated.NativePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.Root, validated.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected session path changed.");
    }

    private async Task RequireInactiveAsync(
        ManagedSession source,
        string nativePath,
        CancellationToken cancellationToken)
    {
        bool isActive;
        try
        {
            isActive = await activeState.IsActiveAsync(
                    source.Agent, source.SessionId, nativePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("The session active state could not be established.", exception);
        }
        if (isActive) throw new InvalidOperationException("The session is active.");
    }

    private Target ValidateTarget(ManagedSession source)
    {
        if (string.IsNullOrWhiteSpace(source.SessionId) || string.IsNullOrWhiteSpace(source.NativePath))
            throw new InvalidDataException("The selected session identity is invalid.");

        if (source.Agent == ManagedAgent.Codex)
        {
            if (codexPaths is null ||
                !string.Equals(Path.GetExtension(source.NativePath), ".jsonl", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected Codex target is invalid.");
            foreach (var root in new[] { codexPaths.Sessions, codexPaths.ArchivedSessions })
            {
                if (!ManagedSessionPathPolicy.TryResolveConcreteTarget(
                        source.NativePath, root, expectDirectory: false, out var nativePath))
                    continue;
                return new Target(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), nativePath);
            }
            throw new InvalidDataException("The selected Codex target is outside its native root.");
        }

        if (source.Agent == ManagedAgent.Grok)
        {
            if (grokPaths is null ||
                !string.Equals(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.NativePath))),
                    source.SessionId,
                    StringComparison.OrdinalIgnoreCase) ||
                !ManagedSessionPathPolicy.TryResolveConcreteTarget(
                    source.NativePath, grokPaths.Sessions, expectDirectory: true, out var nativePath))
                throw new InvalidDataException("The selected Grok target is invalid.");
            try
            {
                _ = GrokSessionPackage.ToLogicalId(source.SessionId);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The selected Grok identity is invalid.", exception);
            }
            return new Target(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(grokPaths.Sessions)),
                nativePath);
        }

        if (source.Agent == ManagedAgent.Claude)
        {
            if (claudePaths is null ||
                !string.Equals(
                    Path.GetFileNameWithoutExtension(Path.GetFullPath(source.NativePath)),
                    source.SessionId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(source.NativePath), ".jsonl", StringComparison.OrdinalIgnoreCase) ||
                !ManagedSessionPathPolicy.TryResolveConcreteTarget(
                    source.NativePath, claudePaths.Projects, expectDirectory: false, out var nativePath))
                throw new InvalidDataException("The selected Claude target is invalid.");
            try
            {
                _ = ClaudeSessionPackage.ToLogicalId(source.SessionId);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The selected Claude identity is invalid.", exception);
            }
            return new Target(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(claudePaths.Projects)),
                nativePath);
        }

        if (source.Agent == ManagedAgent.Continue)
        {
            if (continuePaths is null ||
                ContinuePaths.IsIndexFile(source.NativePath) ||
                !string.Equals(
                    Path.GetFileNameWithoutExtension(Path.GetFullPath(source.NativePath)),
                    source.SessionId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(source.NativePath), ".json", StringComparison.OrdinalIgnoreCase) ||
                !ManagedSessionPathPolicy.TryResolveConcreteTarget(
                    source.NativePath, continuePaths.Sessions, expectDirectory: false, out var nativePath))
                throw new InvalidDataException("The selected Continue target is invalid.");
            try
            {
                _ = ContinueSessionPackage.ToLogicalId(source.SessionId);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The selected Continue identity is invalid.", exception);
            }
            return new Target(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(continuePaths.Sessions)),
                nativePath);
        }

        throw new InvalidDataException("The selected agent is invalid.");
    }

    private static ManagedSessionFailureReason ClassifyCopyFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (ActiveCopyFailures.Contains(current.Message))
                return ManagedSessionFailureReason.Active;
            if (ChangedCopyFailures.Contains(current.Message))
                return ManagedSessionFailureReason.Changed;
            if (DestinationCopyFailures.Contains(current.Message))
                return ManagedSessionFailureReason.DestinationUnavailable;
            if (IncompatibleCopyFailures.Contains(current.Message))
                return ManagedSessionFailureReason.Incompatible;
            if (UnreadableCopyFailures.Contains(current.Message) ||
                current.Message.StartsWith("A working directory is required", StringComparison.Ordinal))
                return ManagedSessionFailureReason.Unreadable;
        }

        return ManagedSessionFailureReason.Unspecified;
    }

    /// <summary>
    /// Drops one session from the shared index. The session file is already gone at this point, so
    /// an index that cannot be parsed is left alone rather than replaced: Continue needs it to
    /// create sessions at all, and the stale row it keeps is the smaller harm.
    /// </summary>
    private void RemoveFromContinueIndex(string sessionId)
    {
        var indexPath = continuePaths!.IndexFilePath;
        if (!File.Exists(indexPath)) return;

        string merged;
        try
        {
            merged = ContinueSessionIndex.Remove(File.ReadAllText(indexPath), sessionId);
        }
        catch (InvalidDataException)
        {
            return;
        }

        var temporary = indexPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, merged);
            File.Move(temporary, indexPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateConversationIdentity(ManagedSession source, PortableConversation conversation)
    {
        var expectedAgent = source.Agent switch
        {
            ManagedAgent.Codex => ConversationAgent.Codex,
            ManagedAgent.Grok => ConversationAgent.Grok,
            ManagedAgent.Claude => ConversationAgent.Claude,
            ManagedAgent.Continue => ConversationAgent.Continue,
            _ => throw new InvalidDataException("The selected agent is invalid.")
        };
        if (conversation.SourceAgent != expectedAgent ||
            !string.Equals(conversation.SourceSessionId, source.SessionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected session identity changed.");
    }

    private static PortableConversation WithCatalogTitle(ManagedSession source, PortableConversation conversation)
    {
        var catalogTitle = source.Title?.Trim();
        if (string.IsNullOrWhiteSpace(catalogTitle) ||
            string.Equals(catalogTitle, source.SessionId, StringComparison.OrdinalIgnoreCase) ||
            ConversationTechnicalText.IsWrapper(catalogTitle))
            return conversation;
        return conversation with { Title = catalogTitle };
    }

    private static async Task<byte[]> CaptureFingerprintAsync(
        string nativePath,
        ManagedAgent agent,
        CancellationToken cancellationToken)
    {
        if (IsFileBackedAgent(agent))
            return await HashFileAsync(nativePath, cancellationToken).ConfigureAwait(false);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        IReadOnlyList<ConcreteEntry> entries;
        try
        {
            entries = EnumerateConcreteEntries(nativePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The selected Grok session could not be validated.", exception);
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(
                (entry.IsDirectory ? "D:" : "F:") + entry.RelativePath + "\n"));
            if (!entry.IsDirectory)
                hash.AppendData(await HashFileAsync(entry.FullPath, cancellationToken).ConfigureAwait(false));
        }
        return hash.GetHashAndReset();
    }

    private static byte[] CaptureFingerprintImmediate(
        string nativePath,
        ManagedAgent agent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsFileBackedAgent(agent))
            return HashFileImmediate(nativePath, cancellationToken);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        IReadOnlyList<ConcreteEntry> entries;
        try
        {
            entries = EnumerateConcreteEntries(nativePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The selected Grok session could not be validated.", exception);
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(
                (entry.IsDirectory ? "D:" : "F:") + entry.RelativePath + "\n"));
            if (!entry.IsDirectory)
                hash.AppendData(HashFileImmediate(entry.FullPath, cancellationToken));
        }
        return hash.GetHashAndReset();
    }

    private static IReadOnlyList<ConcreteEntry> EnumerateConcreteEntries(string root)
    {
        var entries = new List<ConcreteEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 })
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The selected Grok session contains a reparse point.");
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                entries.Add(new ConcreteEntry(path, Path.GetRelativePath(root, path), isDirectory));
                if (isDirectory) pending.Push(path);
            }
        }
        return entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.SequentialScan);
            return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The selected session could not be validated.", exception);
        }
    }

    private static byte[] HashFileImmediate(string path, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.SequentialScan);
            var fingerprint = SHA256.HashData(stream);
            cancellationToken.ThrowIfCancellationRequested();
            return fingerprint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The selected session could not be validated.", exception);
        }
    }

    private sealed record Target(string Root, string NativePath);
    private sealed record ValidatedSource(
        string Root,
        string NativePath,
        PortableConversation Conversation,
        byte[] Fingerprint);
    private sealed record ConcreteEntry(string FullPath, string RelativePath, bool IsDirectory);

    private sealed class SystemFingerprintProvider : IManagedSessionFingerprintProvider
    {
        public static SystemFingerprintProvider Instance { get; } = new();

        public Task<byte[]> CaptureAsync(
            string nativePath,
            ManagedAgent agent,
            CancellationToken cancellationToken) =>
            CaptureFingerprintAsync(nativePath, agent, cancellationToken);

        public byte[] CaptureImmediate(
            string nativePath,
            ManagedAgent agent,
            CancellationToken cancellationToken) =>
            CaptureFingerprintImmediate(nativePath, agent, cancellationToken);
    }
}
