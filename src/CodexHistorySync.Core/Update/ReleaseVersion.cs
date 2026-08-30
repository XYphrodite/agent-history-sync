using System.Globalization;

namespace CodexHistorySync.Core.Update;

/// <summary>
/// The three-part version an installed binary and a release tag have in common.
/// Prerelease and build suffixes are deliberately not modelled: an update decision is "is the
/// release newer than what runs here", and a suffix we cannot order would turn that into a
/// guess. A tag we cannot parse is refused by name instead, which keeps the installer — not
/// this command — responsible for anything unusual.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var parts = text.Split('.');
        // A four-part form is accepted because that is how the runtime reports an assembly
        // version; the trailing revision carries no release meaning and must stay zero.
        if (parts.Length is not (3 or 4)) return false;

        var numbers = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length == 0 || !parts[index].All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
                return false;
        }

        if (parts.Length == 4 && numbers[3] != 0) return false;

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
