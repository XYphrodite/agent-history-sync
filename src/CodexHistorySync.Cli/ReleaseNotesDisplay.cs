using System.Globalization;
using System.Text;
using CodexHistorySync.Core.Update;
using Spectre.Console;

namespace CodexHistorySync.Cli;

internal static class ReleaseNotesDisplay
{
    internal const int MaximumCharacters = 600;
    private const int MaximumLines = 12;

    public static void Write(IAnsiConsole console, ReleaseDescriptor release)
    {
        var notes = Summarize(release.Notes);
        var heading = $"Release notes ({release.Version})";
        if (console.Profile.Capabilities.Interactive)
        {
            // Plain text: neither Markdown nor Spectre markup from the network is executed.
            console.Write(new Panel(new Text(notes))
                .Header(heading).Border(BoxBorder.Rounded).BorderColor(Color.Grey35).Expand());
        }
        else
        {
            console.WriteLine(heading + ":");
            console.WriteLine(notes);
        }

        if (release.ReleasePageUrl is { } url)
            console.WriteLine($"Full release notes: {url.AbsoluteUri}");
    }

    internal static string Summarize(string? notes)
    {
        var clean = new StringBuilder();
        foreach (var rune in (notes ?? string.Empty).ReplaceLineEndings("\n").EnumerateRunes())
        {
            if (rune.Value == '\t') clean.Append("    ");
            else if (rune.Value == '\n' || Rune.GetUnicodeCategory(rune) is not
                     (UnicodeCategory.Control or UnicodeCategory.Format))
                clean.Append(rune.ToString());
        }

        var text = clean.ToString().Trim();
        if (text.Length == 0) return "No release notes provided.";

        var length = Math.Min(text.Length, MaximumCharacters);
        var lines = 1;
        for (var index = 0; index < length; index++)
        {
            if (text[index] == '\n' && ++lines > MaximumLines)
            {
                length = index;
                break;
            }
        }
        if (length == text.Length) return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length].TrimEnd() + "...";
    }
}
