using CodexHistorySync.Core.Update;

namespace CodexHistorySync.Core.Tests.Update;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.7.0", 0, 7, 0)]
    [InlineData("0.7.0", 0, 7, 0)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData(" 0.7.0 ", 0, 7, 0)]
    [InlineData("0.7.0.0", 0, 7, 0)]
    [InlineData("10.20.30", 10, 20, 30)]
    public void SupportedTagsParse(string value, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(value, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.7")]
    [InlineData("0.7.0.1")]
    [InlineData("0.7.0-rc1")]
    [InlineData("0.7.0+build")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("0.7.x")]
    [InlineData("-1.0.0")]
    [InlineData("0.7.0/../evil")]
    public void UnorderableTagsAreRefused(string? value)
    {
        // Accepting any of these would let a tag this code cannot order decide whether an
        // update is "newer", and the pinned form of it reaches a URL path.
        Assert.False(ReleaseVersion.TryParse(value, out _));
    }

    [Fact]
    public void OrderingComparesMajorThenMinorThenPatch()
    {
        Assert.True(new ReleaseVersion(0, 7, 0) < new ReleaseVersion(0, 8, 0));
        Assert.True(new ReleaseVersion(0, 8, 0) < new ReleaseVersion(1, 0, 0));
        Assert.True(new ReleaseVersion(0, 7, 1) > new ReleaseVersion(0, 7, 0));
        Assert.True(new ReleaseVersion(0, 7, 0) <= new ReleaseVersion(0, 7, 0));
        Assert.True(new ReleaseVersion(0, 7, 0) >= new ReleaseVersion(0, 7, 0));
        Assert.Equal(0, new ReleaseVersion(1, 2, 3).CompareTo(new ReleaseVersion(1, 2, 3)));
    }

    [Fact]
    public void RenderedFormHasNoLeadingV()
    {
        // The pinned tag is rebuilt as "v" + this text, so a v here would produce "vv0.7.0".
        Assert.Equal("0.7.0", new ReleaseVersion(0, 7, 0).ToString());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zz3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")]
    public void MalformedChecksumsAreRefused(string content) =>
        Assert.Throws<InvalidDataException>(() => ReleaseChecksum.Parse(content));

    [Fact]
    public void ChecksumIsTakenFromTheFirstTokenAndCasefolded()
    {
        const string Hash = "3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855A";

        Assert.Equal(Hash.ToLowerInvariant(), ReleaseChecksum.Parse($"{Hash}  agent-sync.exe\n"));
        Assert.True(ReleaseChecksum.Matches(Hash, Hash.ToLowerInvariant()));
    }
}
