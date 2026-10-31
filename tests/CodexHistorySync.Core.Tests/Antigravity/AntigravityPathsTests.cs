using CodexHistorySync.Core.Antigravity;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravityPathsTests : IDisposable
{
    private readonly string _tempDir;

    public AntigravityPathsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agy-paths-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void TryResolveExplicitPathReturnsAntigravityPathsWhenDirectoryExists()
    {
        var resolved = AntigravityPaths.TryResolve(_tempDir);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(_tempDir), resolved!.Home);
        Assert.Equal(Path.Combine(Path.GetFullPath(_tempDir), ".agent-sync-antigravity"), resolved.AnchorRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(_tempDir), "conversation_summaries.db"), resolved.ConversationSummariesDbPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(_tempDir), "conversation_summaries.db"), resolved.SummariesDatabasePath);
        Assert.Equal(Path.Combine(Path.GetFullPath(_tempDir), "conversations"), resolved.ConversationsDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(_tempDir), "annotations"), resolved.AnnotationsDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(_tempDir), "brain"), resolved.BrainDirectory);
    }

    [Fact]
    public void TryResolveExplicitRelativePathReturnsNull()
    {
        var relativePath = Path.Combine(".", "nonexistent-relative-dir");
        var resolved = AntigravityPaths.TryResolve(relativePath);
        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveNonExistentDirectoryReturnsNull()
    {
        var nonExistent = Path.Combine(_tempDir, "does-not-exist");
        var resolved = AntigravityPaths.TryResolve(nonExistent);
        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveEnvironmentVariableAntigravityHomeResolvesWhenDirectoryExists()
    {
        var customDir = Path.Combine(_tempDir, "custom-home");
        Directory.CreateDirectory(customDir);

        var originalHome = Environment.GetEnvironmentVariable("ANTIGRAVITY_HOME");
        try
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_HOME", customDir);
            var resolved = AntigravityPaths.TryResolve(null);
            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(customDir), resolved!.Home);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_HOME", originalHome);
        }
    }

    [Fact]
    public void TryResolveEnvironmentVariableAntigravityCliHomeResolvesWhenDirectoryExists()
    {
        var customDir = Path.Combine(_tempDir, "custom-cli-home");
        Directory.CreateDirectory(customDir);

        var originalHome = Environment.GetEnvironmentVariable("ANTIGRAVITY_HOME");
        var originalCliHome = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLI_HOME");
        try
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_HOME", null);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_CLI_HOME", customDir);
            var resolved = AntigravityPaths.TryResolve(null);
            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(customDir), resolved!.Home);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_HOME", originalHome);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_CLI_HOME", originalCliHome);
        }
    }

    [Fact]
    public void TryResolveWithUserProfileOverrideFindsCliHomeWithSummariesDb()
    {
        var fakeProfile = Path.Combine(_tempDir, "fake-profile");
        var candidateCli = Path.Combine(fakeProfile, ".gemini", "antigravity-cli");
        Directory.CreateDirectory(candidateCli);
        File.WriteAllText(Path.Combine(candidateCli, AntigravityPaths.SummariesDatabaseName), "fake db");

        var originalHome = Environment.GetEnvironmentVariable("ANTIGRAVITY_HOME");
        var originalCliHome = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLI_HOME");
        var originalAppData = Environment.GetEnvironmentVariable("ANTIGRAVITY_APP_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_HOME", null);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_CLI_HOME", null);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_APP_DATA_DIR", null);

            var resolved = AntigravityPaths.TryResolve(null, fakeProfile);
            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(candidateCli), resolved!.Home);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_HOME", originalHome);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_CLI_HOME", originalCliHome);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_APP_DATA_DIR", originalAppData);
        }
    }

    [Fact]
    public void TryResolveWithUserProfileOverrideEmptyCandidatesReturnsNull()
    {
        var fakeProfile = Path.Combine(_tempDir, "empty-profile");
        Directory.CreateDirectory(fakeProfile);

        var originalHome = Environment.GetEnvironmentVariable("ANTIGRAVITY_HOME");
        var originalCliHome = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLI_HOME");
        var originalAppData = Environment.GetEnvironmentVariable("ANTIGRAVITY_APP_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_HOME", null);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_CLI_HOME", null);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_APP_DATA_DIR", null);

            var resolved = AntigravityPaths.TryResolve(null, fakeProfile);
            Assert.Null(resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_HOME", originalHome);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_CLI_HOME", originalCliHome);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_APP_DATA_DIR", originalAppData);
        }
    }

    [Theory]
    [InlineData("a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1", true)]
    [InlineData("A5C7F89B-8E2B-4FA8-B2A0-47B1C31278E1", true)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-a-guid", false)]
    [InlineData("{a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1}", false)]
    [InlineData("(a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1)", false)]
    [InlineData("a5c7f89b~8e2b-4fa8-b2a0-47b1c31278e1", false)]
    [InlineData("a5c7f89b/8e2b-4fa8-b2a0-47b1c31278e1", false)]
    [InlineData("a5c7f89b\\8e2b-4fa8-b2a0-47b1c31278e1", false)]
    public void IsSessionIdValidatesCorrectly(string candidate, bool expected)
    {
        var result = AntigravityPaths.IsSessionId(candidate);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void PathConstructionMethodsReturnExpectedPathsWhenSessionIdIsValid()
    {
        var paths = new AntigravityPaths(_tempDir);
        var validId = "11111111-2222-3333-4444-555555555555";

        var convPath = paths.ConversationDatabasePath(validId);
        Assert.Equal(Path.Combine(_tempDir, "conversations", $"{validId}.db"), convPath);

        var annotPath = paths.AnnotationPath(validId);
        Assert.Equal(Path.Combine(_tempDir, "annotations", $"{validId}.pbtxt"), annotPath);

        var brainDir = paths.BrainSessionDirectory(validId);
        Assert.Equal(Path.Combine(_tempDir, "brain", validId), brainDir);

        var transcriptPath = paths.TranscriptPath(validId);
        Assert.Equal(Path.Combine(_tempDir, "brain", validId, "transcript.jsonl"), transcriptPath);

        var anchorPath = paths.AnchorPath(validId);
        Assert.Equal(Path.Combine(_tempDir, ".agent-sync-antigravity", $"{validId}.json"), anchorPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("{11111111-2222-3333-4444-555555555555}")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("sub/path")]
    [InlineData("sub\\path")]
    public void PathConstructionMethodsThrowArgumentExceptionOnInvalidSessionId(string invalidId)
    {
        var paths = new AntigravityPaths(_tempDir);

        Assert.Throws<ArgumentException>(() => paths.ConversationDatabasePath(invalidId));
        Assert.Throws<ArgumentException>(() => paths.AnnotationPath(invalidId));
        Assert.Throws<ArgumentException>(() => paths.BrainSessionDirectory(invalidId));
        Assert.Throws<ArgumentException>(() => paths.TranscriptPath(invalidId));
        Assert.Throws<ArgumentException>(() => paths.AnchorPath(invalidId));
    }

    [Fact]
    public void TryParseAnchorValidAnchorReturnsTrueAndPopulatesHomeAndSessionId()
    {
        var sessionId = "22222222-3333-4444-5555-666666666666";
        var anchorPath = Path.Combine(_tempDir, AntigravityPaths.AnchorDirectoryName, $"{sessionId}.json");

        var success = AntigravityPaths.TryParseAnchor(anchorPath, out var home, out var parsedSessionId);

        Assert.True(success);
        Assert.Equal(Path.GetFullPath(_tempDir), home);
        Assert.Equal(sessionId, parsedSessionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-file")]
    public void TryParseAnchorEmptyOrWhitespaceReturnsFalse(string candidate)
    {
        var success = AntigravityPaths.TryParseAnchor(candidate, out var home, out var sessionId);
        Assert.False(success);
        Assert.Equal(string.Empty, home);
        Assert.Equal(string.Empty, sessionId);
    }

    [Fact]
    public void TryParseAnchorWrongExtensionReturnsFalse()
    {
        var sessionId = "22222222-3333-4444-5555-666666666666";
        var candidate = Path.Combine(_tempDir, AntigravityPaths.AnchorDirectoryName, $"{sessionId}.txt");

        var success = AntigravityPaths.TryParseAnchor(candidate, out _, out _);
        Assert.False(success);
    }

    [Fact]
    public void TryParseAnchorWrongDirectoryNameReturnsFalse()
    {
        var sessionId = "22222222-3333-4444-5555-666666666666";
        var candidate = Path.Combine(_tempDir, "wrong-anchor-directory", $"{sessionId}.json");

        var success = AntigravityPaths.TryParseAnchor(candidate, out _, out _);
        Assert.False(success);
    }

    [Fact]
    public void TryParseAnchorInvalidSessionIdReturnsFalse()
    {
        var candidate = Path.Combine(_tempDir, AntigravityPaths.AnchorDirectoryName, "invalid-guid.json");

        var success = AntigravityPaths.TryParseAnchor(candidate, out _, out _);
        Assert.False(success);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
