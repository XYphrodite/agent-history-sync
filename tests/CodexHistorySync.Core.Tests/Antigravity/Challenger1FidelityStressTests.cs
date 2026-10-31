using System.Text;
using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Hermes;
using CodexHistorySync.Core.Kimi;
using CodexHistorySync.Core.Mimo;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class Challenger1FidelityStressTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workingDir;
    private readonly AntigravityPaths _antigravityPaths;

    public Challenger1FidelityStressTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "agy-stress-" + Guid.NewGuid().ToString("N"));
        _workingDir = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(_workingDir);
        var agyHome = Path.Combine(_tempRoot, "antigravity");
        Directory.CreateDirectory(agyHome);
        _antigravityPaths = new AntigravityPaths(agyHome);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch { }
    }

    [Theory]
    [InlineData("Привет, мир! Тестирование кодировки UTF-8 с кириллицей.")]
    [InlineData("你好，世界！这是一段中文测试消息，包含简体与繁體中文：測試。")]
    [InlineData("こんにちは世界！これは日本語のテストメッセージです。カタカナ、ひらがな、漢字：情報技術。")]
    [InlineData("안녕하세요 세계! 한국어 유니코드 테스트 메시지입니다.")]
    [InlineData("مرحبا بالعالم! هذا اختبار للنص العربي مع اتجاه الكتابة من اليمين إلى اليسار 12345.")]
    [InlineData("שלום עולם! זוהי בדיקה של טקסט עברי מימין לשמאל.")]
    [InlineData("Ελληνικά: Καλημέρα κόσμε! Μαθηματικά: ∑(n=1 to ∞) 1/n² = π²/6.")]
    [InlineData("Tiếng Việt: Thử nghiệm tiếng Việt có dấu: Chào thế giới, tất cả chúng ta!")]
    [InlineData("Hindi: नमस्ते दुनिया! यह एक परीक्षण संदेश है।")]
    [InlineData("Thai: สวัสดีชาวโลก! นี่คือข้อความทดสอบ")]
    [InlineData("Accents: Élégant château français, señor español con piñata, schön übermütig.")]
    [InlineData("Math symbols: ∀x ∈ ℝ, ∃y : x + y = 0 ∧ ∫₀^∞ e^(-x) dx = 1 ∧ √2 ∉ ℚ.")]
    [InlineData("Currency symbols: $100, €90, £80, ¥7000, ₽5000, ₿1.5, ₩50000.")]
    public async Task Roundtrip_MultilingualAndUnicodeCharacterSetStress_PreservedExactly(string sampleText)
    {
        var conversation = new PortableConversation(
            ConversationAgent.Antigravity,
            "unicode-test-session",
            "Unicode Stress Test",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, sampleText),
                new PortableTurn(ConversationRole.Assistant, "Response containing: " + sampleText)
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var writeResult = await writer.WriteAsync(conversation, CancellationToken.None);

        var reader = new AntigravityConversationReader();
        var roundtripped = await reader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(2, roundtripped.Turns.Count);
        Assert.Equal(sampleText, roundtripped.Turns[0].Text);
        Assert.Equal("Response containing: " + sampleText, roundtripped.Turns[1].Text);
    }

    [Theory]
    [InlineData("Emoji test: 😀 😃 😄 😁 😆 😅 😂 🤣 😊 😇")]
    [InlineData("Skin tone modifiers: 👋 👋🏻 👋🏼 👋🏽 👋🏾 👋🏿 👍 👍🏻 👍🏼 👍🏽 👍🏾 👍🏿")]
    [InlineData("ZWJ sequence emojis: 👨‍👩‍👧‍👦 👩‍💻 👨‍🚒 🧑‍🚀 🤹🏽‍♀️ 👁️‍🗨️")]
    [InlineData("Flags: 🇺🇸 🇬🇧 🇩🇪 🇫🇷 🇯🇵 🇨🇳 🇷🇺 🇧🇷 🇮🇳 🇨🇦")]
    [InlineData("Symbols & Keycaps: 1️⃣ 2️⃣ 3️⃣ #️⃣ *️⃣ ⚡ 💡 🚀 🛸 🛡️ ⚔️ 🔮")]
    public async Task Roundtrip_EmojiCharacterSetStress_PreservedExactly(string emojiSample)
    {
        var conversation = new PortableConversation(
            ConversationAgent.Antigravity,
            "emoji-test-session",
            "Emoji Stress Test",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, emojiSample),
                new PortableTurn(ConversationRole.Assistant, "Echo emoji: " + emojiSample)
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var writeResult = await writer.WriteAsync(conversation, CancellationToken.None);

        var reader = new AntigravityConversationReader();
        var roundtripped = await reader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(2, roundtripped.Turns.Count);
        Assert.Equal(emojiSample, roundtripped.Turns[0].Text);
        Assert.Equal("Echo emoji: " + emojiSample, roundtripped.Turns[1].Text);
    }

    [Fact]
    public async Task Roundtrip_MultilineTabsCodeBlocksAndJsonSnippets_PreservedExactly()
    {
        var multilineCodeText =
            """
            Here is a C# snippet:
            ```csharp
            public sealed class DataProcessor<T> where T : class, new()
            {
            	private readonly ILogger _logger;
            	public DataProcessor(ILogger logger) => _logger = logger;

            	public async Task<int> ProcessAsync(T item, CancellationToken ct)
            	{
            		if (item is null) throw new ArgumentNullException(nameof(item));
            		\r\n\t\t// Some inner tabs and multiline comment
            		return 42;
            	}
            }
            ```

            And JSON payload:
            ```json
            {
              "key": "value",
              "nested": {
                "array": [1, 2, 3, null, true, false],
                "escapes": "\"quotes\" \\slashes\\ /forward/ \b\f\n\r\t"
              }
            }
            ```

            SQL injection string:
            SELECT * FROM users WHERE name = 'O''Connor; DROP TABLE users; --';

            HTML / XML:
            <note priority="high">
              <to>Tove &amp; Jani</to>
              <content>Don't forget &lt;escaped&gt; entities!</content>
            </note>
            """;

        var conversation = new PortableConversation(
            ConversationAgent.Antigravity,
            "multiline-session",
            "Multiline Stress",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, multilineCodeText),
                new PortableTurn(ConversationRole.Assistant, "Assistant received:\n" + multilineCodeText)
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var writeResult = await writer.WriteAsync(conversation, CancellationToken.None);

        var reader = new AntigravityConversationReader();
        var roundtripped = await reader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(2, roundtripped.Turns.Count);
        Assert.Equal(multilineCodeText, roundtripped.Turns[0].Text);
        Assert.Equal("Assistant received:\n" + multilineCodeText, roundtripped.Turns[1].Text);
    }

    [Fact]
    public async Task Roundtrip_SingleTurnStructure_UserOnly()
    {
        var conversation = new PortableConversation(
            ConversationAgent.Antigravity,
            "single-user-turn-session",
            "Single User Turn",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Only one user prompt here.")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var writeResult = await writer.WriteAsync(conversation, CancellationToken.None);

        var reader = new AntigravityConversationReader();
        var roundtripped = await reader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Single(roundtripped.Turns);
        Assert.Equal(ConversationRole.User, roundtripped.Turns[0].Role);
        Assert.Equal("Only one user prompt here.", roundtripped.Turns[0].Text);
    }

    [Fact]
    public async Task Roundtrip_SingleTurnStructure_AssistantOnly()
    {
        var conversation = new PortableConversation(
            ConversationAgent.Antigravity,
            "single-assistant-turn-session",
            "Single Assistant Turn",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.Assistant, "Only one assistant message here.")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var writeResult = await writer.WriteAsync(conversation, CancellationToken.None);

        var reader = new AntigravityConversationReader();
        var roundtripped = await reader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Single(roundtripped.Turns);
        Assert.Equal(ConversationRole.Assistant, roundtripped.Turns[0].Role);
        Assert.Equal("Only one assistant message here.", roundtripped.Turns[0].Text);
    }

    [Fact]
    public async Task Roundtrip_TenTurnsStructure_AlternatingRoles()
    {
        var turns = new List<PortableTurn>();
        for (var i = 0; i < 10; i++)
        {
            var role = (i % 2 == 0) ? ConversationRole.User : ConversationRole.Assistant;
            turns.Add(new PortableTurn(role, $"Turn index {i} payload text for role {role} with random data {i * 1337}"));
        }

        var conversation = new PortableConversation(
            ConversationAgent.Antigravity,
            "ten-turns-session",
            "Ten Turns Conversation",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            turns);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var writeResult = await writer.WriteAsync(conversation, CancellationToken.None);

        var reader = new AntigravityConversationReader();
        var roundtripped = await reader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(10, roundtripped.Turns.Count);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(turns[i].Role, roundtripped.Turns[i].Role);
            Assert.Equal(turns[i].Text, roundtripped.Turns[i].Text);
        }
    }

    [Fact]
    public async Task Roundtrip_ConsecutiveSameRoleTurns()
    {
        var turns = new List<PortableTurn>
        {
            new(ConversationRole.User, "User query 1"),
            new(ConversationRole.User, "User query 2 immediately following 1"),
            new(ConversationRole.User, "User query 3 immediately following 2"),
            new(ConversationRole.Assistant, "Assistant response 1"),
            new(ConversationRole.Assistant, "Assistant response 2 continuing response 1"),
            new(ConversationRole.User, "User closing query")
        };

        var conversation = new PortableConversation(
            ConversationAgent.Antigravity,
            "consecutive-roles-session",
            "Consecutive Roles",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow,
            turns);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var writeResult = await writer.WriteAsync(conversation, CancellationToken.None);

        var reader = new AntigravityConversationReader();
        var roundtripped = await reader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(6, roundtripped.Turns.Count);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(turns[i].Role, roundtripped.Turns[i].Role);
            Assert.Equal(turns[i].Text, roundtripped.Turns[i].Text);
        }
    }

    [Fact]
    public async Task Roundtrip_LargePayloadStress_100KBMessagePreservedWithZeroDataLoss()
    {
        var sb = new StringBuilder(100_000);
        for (var i = 0; i < 2000; i++)
        {
            sb.Append($"Line {i:D4}: The quick brown fox jumps over the lazy dog 1234567890! 🦊🚀\n");
        }
        var largeText = sb.ToString();

        var conversation = new PortableConversation(
            ConversationAgent.Antigravity,
            "large-payload-session",
            "Large Payload Stress",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, largeText),
                new PortableTurn(ConversationRole.Assistant, "Echoing 100KB: " + largeText)
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var writeResult = await writer.WriteAsync(conversation, CancellationToken.None);

        var reader = new AntigravityConversationReader();
        var roundtripped = await reader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(2, roundtripped.Turns.Count);
        Assert.Equal(largeText, roundtripped.Turns[0].Text);
        Assert.Equal("Echoing 100KB: " + largeText, roundtripped.Turns[1].Text);
    }

    [Fact]
    public async Task TitleDerivation_ExplicitTitle_SurrogatePairAt80CharBoundary_TruncatesCleanlyWithoutDanglingSurrogate()
    {
        // 79 ASCII chars + emoji '😀' (\uD83D\uDE00, length 2 in UTF-16).
        // Total length = 81.
        // Slicing cleanly without leaving an orphaned high surrogate \uD83D truncates to 79 chars.
        var titleWithSurrogateAtBoundary = new string('A', 79) + "😀";

        var conversation = new PortableConversation(
            ConversationAgent.Antigravity,
            "surrogate-title-session",
            titleWithSurrogateAtBoundary,
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Check surrogate boundary in title")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var writeResult = await writer.WriteAsync(conversation, CancellationToken.None);
        var reader = new AntigravityConversationReader();
        var reread = await reader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(new string('A', 79), reread.Title);
    }

    [Fact]
    public async Task TitleDerivation_FallbackFirstUserTurn_SurrogatePairAt80CharBoundary_TruncatesCleanlyWithoutDanglingSurrogate()
    {
        // When Title is empty, DeriveTitle falls back to first user turn text.
        // If the first user turn text has an emoji spanning index 79-80, the same surrogate-aware truncation applies.
        var userTextWithSurrogateAtBoundary = new string('B', 79) + "🚀 Question about performance";

        var conversation = new PortableConversation(
            ConversationAgent.Antigravity,
            "surrogate-fallback-session",
            "",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, userTextWithSurrogateAtBoundary),
                new PortableTurn(ConversationRole.Assistant, "Response")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var writeResult = await writer.WriteAsync(conversation, CancellationToken.None);
        var reader = new AntigravityConversationReader();
        var reread = await reader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(new string('B', 79), reread.Title);
    }

    [Fact]
    public async Task TitleDerivation_SafeLength78And82_PreservesSurrogatePairsWithoutSplitting()
    {
        // When emoji does NOT cross index 80 (e.g., at 78 so it ends at 80, or at 82 where it is entirely beyond 80)
        var titleEndingExactlyAt80 = new string('C', 78) + "😀"; // length 80 exactly

        var conversation = new PortableConversation(
            ConversationAgent.Antigravity,
            "safe-title-session",
            titleEndingExactlyAt80,
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Safe boundary query")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var writeResult = await writer.WriteAsync(conversation, CancellationToken.None);

        var reader = new AntigravityConversationReader();
        var roundtripped = await reader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(titleEndingExactlyAt80, roundtripped.Title);
    }

    [Fact]
    public async Task CrossAgent_AntigravityToAllAgents_PreservesUnicodeAndEmojis()
    {
        var complexSample = "Привет! 🌍 CJK: 你好, 세계! Emojis: 👨‍👩‍👧‍👦 🚀 \nCode:\n```json\n{\"x\": 1}\n```";
        var agyConv = new PortableConversation(
            ConversationAgent.Antigravity,
            "cross-agy-session",
            "Cross Agent Unicode Stress",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, complexSample),
                new PortableTurn(ConversationRole.Assistant, "Echoing: " + complexSample)
            ]);

        // 1. Claude
        var claudeHome = Path.Combine(_tempRoot, "claude");
        var claudeProjects = Path.Combine(claudeHome, "projects");
        Directory.CreateDirectory(claudeProjects);
        var claudeWriter = new ClaudeConversationWriter(new ClaudePaths(claudeHome, claudeProjects));
        var claudeRes = await claudeWriter.WriteAsync(agyConv, CancellationToken.None);
        var claudeRead = await new ClaudeConversationReader().ReadAsync(claudeRes.NativePath, CancellationToken.None);
        Assert.Equal(complexSample, claudeRead.Turns[0].Text);

        // 2. Grok
        var grokHome = Path.Combine(_tempRoot, "grok");
        var grokSessions = Path.Combine(grokHome, "sessions");
        Directory.CreateDirectory(grokSessions);
        var grokWriter = new GrokConversationWriter(new GrokPaths(grokHome, grokSessions));
        var grokRes = await grokWriter.WriteAsync(agyConv, CancellationToken.None);
        var grokRead = await new GrokConversationReader().ReadAsync(grokRes.NativePath, CancellationToken.None);
        Assert.Equal(complexSample, grokRead.Turns[0].Text);

        // 3. Hermes
        var hermesHome = Path.Combine(_tempRoot, "hermes");
        Directory.CreateDirectory(hermesHome);
        var hermesWriter = new HermesConversationWriter(new HermesPaths(hermesHome));
        var hermesRes = await hermesWriter.WriteAsync(agyConv, CancellationToken.None);
        var hermesRead = await new HermesConversationReader().ReadAsync(hermesRes.NativePath, CancellationToken.None);
        Assert.Equal(complexSample, hermesRead.Turns[0].Text);

        // 4. Kimi
        var kimiHome = Path.Combine(_tempRoot, "kimi");
        var kimiSessions = Path.Combine(kimiHome, "sessions");
        Directory.CreateDirectory(kimiSessions);
        var kimiWriter = new KimiConversationWriter(new KimiPaths(kimiHome, kimiSessions));
        var kimiRes = await kimiWriter.WriteAsync(agyConv, CancellationToken.None);
        var kimiRead = await new KimiConversationReader().ReadAsync(kimiRes.NativePath, CancellationToken.None);
        Assert.Equal(complexSample, kimiRead.Turns[0].Text);

        // 5. Mimo
        var mimoHome = Path.Combine(_tempRoot, "mimo");
        Directory.CreateDirectory(mimoHome);
        var mimoWriter = new MimoConversationWriter(new MimoPaths(mimoHome));
        var mimoRes = await mimoWriter.WriteAsync(agyConv, CancellationToken.None);
        var mimoRead = await new MimoConversationReader().ReadAsync(mimoRes.NativePath, CancellationToken.None);
        Assert.Equal(complexSample, mimoRead.Turns[0].Text);
    }
}
