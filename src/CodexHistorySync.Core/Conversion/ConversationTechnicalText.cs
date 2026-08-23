namespace CodexHistorySync.Core.Conversion;

internal static class ConversationTechnicalText
{
    private static readonly string[] Wrappers =
    [
        "<environment_context>", "<recommended_plugins>", "<user_info>", "<system-reminder>",
        "<permissions instructions>", "<skills_instructions>", "<apps_instructions>", "<plugins_instructions>",
        "# Files mentioned by the user:", "# Context from my IDE setup:"
    ];

    public static bool IsWrapper(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.TrimStart();
        return Wrappers.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
