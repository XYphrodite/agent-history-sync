using System.Security.Cryptography;
using System.Text;
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
    private readonly IManagedSessionActiveState activeState;
    private readonly IManagedSessionDirectoryDeleter directoryDeleter;
    private readonly IConversationWriter? codexWriter;
    private readonly IConversationWriter? grokWriter;
    private readonly IConversationReader codexReader;
    private readonly IConversationReader grokReader;
    private readonly IManagedSessionFingerprintProvider fingerprintProvider;

    public LocalSessionOperations(
        CodexPaths? codexPaths,
        GrokPaths? grokPaths,
        IManagedSessionActiveState activeState,
        IManagedSessionDirectoryDeleter directoryDeleter,
        IConversationWriter? codexWriter,
        IConversationWriter? grokWriter)
        : this(
            codexPaths,
            grokPaths,
            activeState,
            directoryDeleter,
            codexWriter,
            grokWriter,
            new CodexConversationReader(),
            new GrokConversationReader())
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
        IManagedSessionFingerprintProvider? fingerprintProvider = null)
    {
        this.codexPaths = codexPaths;
        this.grokPaths = grokPaths;
        this.activeState = activeState ?? throw new ArgumentNullException(nameof(activeState));
        this.directoryDeleter = directoryDeleter ?? throw new ArgumentNullException(nameof(directoryDeleter));
        this.codexWriter = codexWriter;
        this.grokWriter = grokWriter;
        this.codexReader = codexReader ?? throw new ArgumentNullException(nameof(codexReader));
        this.grokReader = grokReader ?? throw new ArgumentNullException(nameof(grokReader));
        this.fingerprintProvider = fingerprintProvider ?? SystemFingerprintProvider.Instance;
    }

    public async Task<string> CopyAsync(ManagedSession source, CancellationToken cancellationToken)
    {
        try
        {
            var validated = await ReadAndValidateAsync(source, cancellationToken).ConfigureAwait(false);
            await RevalidateForActionAsync(source, validated, cancellationToken).ConfigureAwait(false);
            var writer = source.Agent switch
            {
                ManagedAgent.Codex => grokWriter,
                ManagedAgent.Grok => codexWriter,
                _ => null
            } ?? throw new InvalidOperationException("The destination agent is unavailable.");

            var result = await writer.WriteAsync(validated.Conversation, cancellationToken).ConfigureAwait(false);
            return result.SessionId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new ManagedSessionOperationException(ManagedSessionOperationFailure.Copy);
        }
    }

    public async Task DeleteAsync(ManagedSession source, CancellationToken cancellationToken)
    {
        try
        {
            var validated = await ReadAndValidateAsync(source, cancellationToken).ConfigureAwait(false);
            await RevalidateForActionAsync(source, validated, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (source.Agent == ManagedAgent.Codex)
            {
                File.Delete(validated.NativePath);
                return;
            }

            await directoryDeleter.DeleteAsync(validated.Root, validated.NativePath, cancellationToken)
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
        var reader = source.Agent == ManagedAgent.Codex ? codexReader : grokReader;
        var conversation = await reader.ReadAsync(target.NativePath, cancellationToken).ConfigureAwait(false);
        ValidateConversationIdentity(source, conversation);

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

    private async Task RevalidateForActionAsync(
        ManagedSession source,
        ValidatedSource validated,
        CancellationToken cancellationToken)
    {
        await RequireUnchangedAsync(source, validated, cancellationToken).ConfigureAwait(false);
        await RequireUnchangedAsync(source, validated, cancellationToken).ConfigureAwait(false);
        await RequireInactiveAsync(source, validated.NativePath, cancellationToken).ConfigureAwait(false);
        RequireUnchangedImmediately(source, validated, cancellationToken);
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

        throw new InvalidDataException("The selected agent is invalid.");
    }

    private static void ValidateConversationIdentity(ManagedSession source, PortableConversation conversation)
    {
        var expectedAgent = source.Agent == ManagedAgent.Codex ? ConversationAgent.Codex : ConversationAgent.Grok;
        if (conversation.SourceAgent != expectedAgent ||
            !string.Equals(conversation.SourceSessionId, source.SessionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected session identity changed.");
    }

    private static async Task<byte[]> CaptureFingerprintAsync(
        string nativePath,
        ManagedAgent agent,
        CancellationToken cancellationToken)
    {
        if (agent == ManagedAgent.Codex)
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
        if (agent == ManagedAgent.Codex)
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
