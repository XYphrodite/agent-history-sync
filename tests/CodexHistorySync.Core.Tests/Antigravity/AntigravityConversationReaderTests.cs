using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravityConversationReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AntigravityPaths _paths;

    public AntigravityConversationReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "antigravity-reader-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "conversations"));
        _paths = new AntigravityPaths(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReadAsync_FromAnchorPath_ExtractsUserAndAssistantTurns()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);

        var steps = new List<AntigravityStepRow>
        {
            new(0, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodeUserInput("What is the speed of light?")),
            new(1, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodePlannerResponse("Approximately 299,792,458 m/s.", "Thinking about physics..."))
        };

        AntigravityDatabase.WriteSteps(convDb, "traj-1", sessionId, steps);
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            ConversationId: sessionId,
            ParentConversationId: null,
            NestingDepth: 0,
            AgentName: "cli",
            Title: "Physics Query",
            Status: "COMPLETED",
            WorkspaceUris: new[] { "file:///C:/Workspace/Physics" },
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt: DateTimeOffset.UtcNow));

        var reader = new AntigravityConversationReader();
        var anchorPath = _paths.AnchorPath(sessionId);

        var conversation = await reader.ReadAsync(anchorPath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Antigravity, conversation.SourceAgent);
        Assert.Equal(sessionId, conversation.SourceSessionId);
        Assert.Equal("Physics Query", conversation.Title);
        Assert.Equal(@"C:\Workspace\Physics", conversation.WorkingDirectory);
        Assert.Equal(2, conversation.Turns.Count);

        Assert.Equal(ConversationRole.User, conversation.Turns[0].Role);
        Assert.Equal("What is the speed of light?", conversation.Turns[0].Text);

        Assert.Equal(ConversationRole.Assistant, conversation.Turns[1].Role);
        Assert.Equal("Approximately 299,792,458 m/s.", conversation.Turns[1].Text);
    }

    [Fact]
    public async Task ReadAsync_DirectDb_FallsBackToThinkingWhenResponseEmpty()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);

        var steps = new List<AntigravityStepRow>
        {
            new(0, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodeUserInput("Hello")),
            new(1, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodePlannerResponse(string.Empty, "Only thinking text present"))
        };

        AntigravityDatabase.WriteSteps(convDb, "traj-1", sessionId, steps);

        var reader = new AntigravityConversationReader();
        var conversation = await reader.ReadAsync(convDb, CancellationToken.None);

        Assert.Equal(ConversationAgent.Antigravity, conversation.SourceAgent);
        Assert.Equal(sessionId, conversation.SourceSessionId);
        Assert.Equal(2, conversation.Turns.Count);
        Assert.Equal("Only thinking text present", conversation.Turns[1].Text);
    }

    [Fact]
    public async Task ReadAsync_MissingDatabaseThrowsInvalidDataException()
    {
        var sessionId = Guid.NewGuid().ToString();
        var reader = new AntigravityConversationReader();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadAsync(_paths.AnchorPath(sessionId), CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_EmptyStepsThrowsInvalidDataException()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);
        AntigravityDatabase.WriteSteps(convDb, "traj-1", sessionId, Array.Empty<AntigravityStepRow>());

        var reader = new AntigravityConversationReader();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadAsync(convDb, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_DropsIntermediateThinkingStepsWhenVisibleAssistantResponseExists()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);

        using var toolStepBody = new MemoryStream();
        // field 3 = thinking, field 7 = tool_call
        var thinkingBytes = System.Text.Encoding.UTF8.GetBytes("Internal reasoning before calling read_file...");
        toolStepBody.WriteByte((3 << 3) | 2);
        toolStepBody.WriteByte((byte)thinkingBytes.Length);
        toolStepBody.Write(thinkingBytes);
        var toolBytes = new byte[] { 0x0A, 0x09, (byte)'r', (byte)'e', (byte)'a', (byte)'d', (byte)'_', (byte)'f', (byte)'i', (byte)'l', (byte)'e' };
        toolStepBody.WriteByte((7 << 3) | 2);
        toolStepBody.WriteByte((byte)toolBytes.Length);
        toolStepBody.Write(toolBytes);

        using var toolStepPayload = new MemoryStream();
        var bodyArray = toolStepBody.ToArray();
        toolStepPayload.WriteByte((20 << 3) | 2);
        toolStepPayload.WriteByte((byte)bodyArray.Length);
        toolStepPayload.Write(bodyArray);

        var steps = new List<AntigravityStepRow>
        {
            new(0, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodeUserInput("Check the configuration file.")),
            new(1, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone,
                toolStepPayload.ToArray()),
            new(2, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodePlannerResponse(string.Empty, "Intermediate retry thought without visible reply")),
            new(3, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodePlannerResponse("The configuration file is valid.", "Final check complete."))
        };

        AntigravityDatabase.WriteSteps(convDb, "traj-1", sessionId, steps);

        var reader = new AntigravityConversationReader();
        var conversation = await reader.ReadAsync(convDb, CancellationToken.None);

        Assert.Equal(2, conversation.Turns.Count);
        Assert.Equal(ConversationRole.User, conversation.Turns[0].Role);
        Assert.Equal("Check the configuration file.", conversation.Turns[0].Text);
        Assert.Equal(ConversationRole.Assistant, conversation.Turns[1].Role);
        Assert.Equal("The configuration file is valid.", conversation.Turns[1].Text);
    }
}
