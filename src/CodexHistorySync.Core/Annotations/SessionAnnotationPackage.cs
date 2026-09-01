using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Annotations;

/// <summary>
/// The published form of one annotation. The bytes are exactly what
/// <see cref="SessionAnnotationStore"/> writes to disk - there is no second encoding to keep in
/// step - and this type adds what the repository needs around them: a logical id, and a hash.
///
/// The logical id carries the agent as well as the session, because two agents may hold the same
/// session id and each of them may be named separately.
/// </summary>
public static class SessionAnnotationPackage
{
    public const string LogicalIdPrefix = "annotation-";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ToLogicalId(SessionAnnotationKey key)
    {
        if (!Enum.IsDefined(key.Agent)) throw new ArgumentException("Annotation agent is invalid.", nameof(key));
        if (!IO.SafeNameComponent.IsValid(key.SessionId))
            throw new ArgumentException("Annotation session id is invalid.", nameof(key));
        return LogicalIdPrefix + key.Agent.ToString().ToLowerInvariant() + "-" + key.SessionId;
    }

    public static bool TryParseLogicalId(string? value, out SessionAnnotationKey key)
    {
        key = default;
        if (value is null || !value.StartsWith(LogicalIdPrefix, StringComparison.Ordinal)) return false;

        var rest = value[LogicalIdPrefix.Length..];
        var separator = rest.IndexOf('-');
        if (separator <= 0 || separator == rest.Length - 1) return false;

        // Agent names carry no hyphen, so the first one ends the agent and starts the session id.
        if (!Enum.TryParse<ManagedAgent>(rest[..separator], ignoreCase: true, out var agent) ||
            !string.Equals(agent.ToString().ToLowerInvariant(), rest[..separator], StringComparison.Ordinal))
            return false;

        var sessionId = rest[(separator + 1)..];
        if (!IO.SafeNameComponent.IsValid(sessionId)) return false;

        key = new SessionAnnotationKey(agent, sessionId);
        return true;
    }

    public static byte[] Build(SessionAnnotationKey key, SessionAnnotation annotation) =>
        SessionAnnotationStore.Serialize(key, annotation);

    public static ContentHash HashPackage(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new ContentHash(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>Reads published bytes. False for anything this build cannot use.</summary>
    public static bool TryReadPackage(byte[] bytes, out SessionAnnotationKey key, out SessionAnnotation annotation)
    {
        key = default;
        annotation = null!;
        if (bytes is null || bytes.Length == 0) return false;

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return SessionAnnotationStore.TryRead(text, out key, out annotation);
    }

    /// <summary>The file an imported annotation belongs in, under the annotations directory.</summary>
    public static string DestinationPath(string annotationsDirectory, SessionAnnotationKey key) =>
        Path.Combine(annotationsDirectory, SessionAnnotationStore.FileName(key));
}
