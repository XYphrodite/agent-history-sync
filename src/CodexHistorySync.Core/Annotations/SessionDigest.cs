using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Annotations;

/// <summary>
/// The text a suggester is given, and the hash that says which conversation it was made from.
/// </summary>
public sealed record SessionDigestResult(string Text, string Hash, string? OpeningRequest = null)
{
    /// <summary>True when the session held nothing worth naming: only wrappers, or no turns.</summary>
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>
/// Reduces a conversation to the text a model is asked to name. It works on
/// <see cref="PortableConversation"/> alone, so one implementation covers all four agents and the
/// existing readers stay the only things that know a native format.
/// </summary>
public static class SessionDigest
{
    public const int DefaultMaximumCharacters = 18000;

    /// <summary>One pasted log must not crowd out the forty turns around it.</summary>
    public const int MaximumTurnCharacters = 2000;

    /// <summary>
    /// How much of the opening request travels beside the digest. It is what the session was for,
    /// and without it a model names the loudest problem in the transcript instead of the work:
    /// measured on three real sessions, "Установщик xmrig" against "xmrig fleet management" for
    /// the same conversation.
    /// </summary>
    public const int MaximumOpeningCharacters = 600;

    private const string Elision = "\n\n[... middle omitted ...]\n\n";
    private const string TurnSeparator = "\n\n";

    public static SessionDigestResult Build(
        PortableConversation conversation,
        int maximumCharacters = DefaultMaximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);

        var builder = new StringBuilder();
        string? opening = null;
        foreach (var turn in conversation.Turns)
        {
            if (ConversationTechnicalText.IsWrapper(turn.Text)) continue;
            var text = turn.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            if (text.Length > MaximumTurnCharacters) text = text[..MaximumTurnCharacters];

            if (opening is null && turn.Role == ConversationRole.User)
            {
                opening = text.Length <= MaximumOpeningCharacters ? text : text[..MaximumOpeningCharacters];
            }

            if (builder.Length != 0) builder.Append(TurnSeparator);
            builder.Append(turn.Role == ConversationRole.User ? "USER: " : "ASSISTANT: ").Append(text);
        }

        var digest = builder.ToString();
        if (digest.Length > maximumCharacters) digest = Elide(digest, maximumCharacters);

        // Newlines are written out rather than taken from the environment, and nothing outside the
        // turns is hashed: two machines have to agree on the hash of one synchronized session.
        // The opening request is not hashed: it is drawn from the same turns the digest already
        // covers, and a hash that two machines must agree on has one source, not two.
        return new SessionDigestResult(
            digest,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(digest))),
            opening);
    }

    /// <summary>
    /// Keeps the opening, which says what the session was about, and the end, which says how it
    /// turned out. The middle of a long session is the part a title needs least.
    /// </summary>
    private static string Elide(string text, int maximumCharacters)
    {
        if (maximumCharacters <= Elision.Length) return text[..maximumCharacters];

        var remaining = maximumCharacters - Elision.Length;
        var head = remaining * 3 / 5;
        var tail = remaining - head;
        return string.Concat(text.AsSpan(0, head), Elision, text.AsSpan(text.Length - tail));
    }
}
