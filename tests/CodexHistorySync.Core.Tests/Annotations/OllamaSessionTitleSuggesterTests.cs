using System.Net;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Annotations;

namespace CodexHistorySync.Core.Tests.Annotations;

public sealed class OllamaSessionTitleSuggesterTests
{
    private static readonly SessionDigestResult Digest = new("USER: what broke\n\nASSISTANT: the event log", "hash-1");

    [Fact]
    public async Task SuggestAsync_TurnsAWellFormedAnswerIntoADraft()
    {
        using var handler = new StubHandler(Answer("Event log stopped on the GPU box", "Ollama could not start a runner until the service came back."));
        var suggester = Suggester(handler);

        var draft = await suggester.SuggestAsync(Digest, CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Equal("Event log stopped on the GPU box", draft.Title);
        Assert.Equal("Ollama could not start a runner until the service came back.", draft.Description);
        Assert.Equal("qwen3:8b", draft.Model);
    }

    [Fact]
    public async Task SuggestAsync_SendsTheDigestTheModelAndTheAnswerSchema()
    {
        using var handler = new StubHandler(Answer("Title", "Description"));

        await Suggester(handler).SuggestAsync(Digest, CancellationToken.None);

        var request = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
        Assert.Equal("qwen3:8b", request.GetProperty("model").GetString());
        Assert.False(request.GetProperty("stream").GetBoolean());
        Assert.Equal("object", request.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(16384, request.GetProperty("options").GetProperty("num_ctx").GetInt32());
        // Reasoning off: with it on the answer costs four times the wall clock and can come
        // back empty when the token budget goes on thinking instead of on the title.
        Assert.False(request.GetProperty("think").GetBoolean());
        var messages = request.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains(Digest.Text, messages[1].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:11434/api/chat", Assert.Single(handler.Uris).ToString());
    }

    [Fact]
    public async Task SuggestAsync_SendsABodyOfKnownLengthRatherThanAStream()
    {
        // Ollama closes the connection on a chunked request body, so the body has to be measured
        // before it is sent. Streaming the JSON straight into the request is what produces one.
        using var handler = new StubHandler(Answer("Title", "Description"));

        await Suggester(handler).SuggestAsync(Digest, CancellationToken.None);

        Assert.NotNull(Assert.Single(handler.ContentLengths));
    }

    [Fact]
    public async Task SuggestAsync_LeadsWithTheRequestTheSessionOpenedWith()
    {
        using var handler = new StubHandler(Answer("Title", "Description"));
        var digest = new SessionDigestResult(
            "USER: what broke", "hash-1", "make this machine a second GPU box");

        await Suggester(handler).SuggestAsync(digest, CancellationToken.None);

        var messages = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement.GetProperty("messages");
        var prompt = messages[1].GetProperty("content").GetString()!;
        Assert.StartsWith("The user opened with:", prompt, StringComparison.Ordinal);
        Assert.Contains("make this machine a second GPU box", prompt, StringComparison.Ordinal);
        Assert.Contains("USER: what broke", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuggestAsync_SendsOnlyTheTranscriptWhenNothingOpenedTheSession()
    {
        using var handler = new StubHandler(Answer("Title", "Description"));

        await Suggester(handler).SuggestAsync(Digest, CancellationToken.None);

        var messages = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement.GetProperty("messages");
        Assert.StartsWith("Session transcript:", messages[1].GetProperty("content").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuggestAsync_CutsATitleOverTheBoundAndNormalizesItsWhitespace()
    {
        var overlong = new string('t', SessionAnnotation.MaximumTitleLength + 40);
        using var handler = new StubHandler(Answer("  spaced   out  ", "d"));
        using var overlongHandler = new StubHandler(Answer(overlong, "d"));

        var spaced = await Suggester(handler).SuggestAsync(Digest, CancellationToken.None);
        var cut = await Suggester(overlongHandler).SuggestAsync(Digest, CancellationToken.None);

        Assert.Equal("spaced out", spaced?.Title);
        Assert.Equal(SessionAnnotation.MaximumTitleLength, cut?.Title.Length);
    }

    [Fact]
    public async Task SuggestAsync_CutsADescriptionOverTheBound()
    {
        using var handler = new StubHandler(
            Answer("Title", new string('d', SessionAnnotation.MaximumDescriptionLength + 40)));

        var draft = await Suggester(handler).SuggestAsync(Digest, CancellationToken.None);

        Assert.Equal(SessionAnnotation.MaximumDescriptionLength, draft?.Description.Length);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "message": { "content": "{\"description\":\"no title here\"}" } }""")]
    [InlineData("""{ "message": { "content": "{\"title\":\"   \",\"description\":\"blank\"}" } }""")]
    [InlineData("""{ "message": { "content": "the model ignored the schema" } }""")]
    [InlineData("""{ "done": true }""")]
    public async Task SuggestAsync_ReturnsNothingForAnAnswerItCannotUse(string body)
    {
        using var handler = new StubHandler(body);

        Assert.Null(await Suggester(handler).SuggestAsync(Digest, CancellationToken.None));
    }

    [Fact]
    public async Task SuggestAsync_ReturnsNothingWhenTheEndpointFails()
    {
        using var handler = new StubHandler("upstream is unhappy", HttpStatusCode.InternalServerError);

        Assert.Null(await Suggester(handler).SuggestAsync(Digest, CancellationToken.None));
    }

    [Fact]
    public async Task SuggestAsync_ReturnsNothingWhenTheEndpointCannotBeReached()
    {
        // Exactly what a box with a stopped runner does: the connection never completes.
        using var handler = new StubHandler(_ => throw new HttpRequestException("No connection could be made."));

        Assert.Null(await Suggester(handler).SuggestAsync(Digest, CancellationToken.None));
    }

    [Fact]
    public async Task SuggestAsync_ReturnsNothingWhenTheRequestTimesOut()
    {
        using var handler = new StubHandler(_ => throw new TaskCanceledException("The request timed out."));

        Assert.Null(await Suggester(handler).SuggestAsync(Digest, CancellationToken.None));
    }

    [Fact]
    public async Task SuggestAsync_ReturnsNothingWhenTheCallerCancels()
    {
        using var handler = new StubHandler(Answer("Title", "Description"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.Null(await Suggester(handler).SuggestAsync(Digest, cancellation.Token));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task SuggestAsync_AsksNothingWhenNoEndpointIsConfigured()
    {
        using var handler = new StubHandler(Answer("Title", "Description"));
        var suggester = new OllamaSessionTitleSuggester(new SessionTitleOptions(null), handler, disposeHandler: false);

        Assert.False(suggester.IsConfigured);
        Assert.Null(await suggester.SuggestAsync(Digest, CancellationToken.None));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task SuggestAsync_AsksNothingAboutAnEmptyDigest()
    {
        using var handler = new StubHandler(Answer("Title", "Description"));

        Assert.Null(await Suggester(handler).SuggestAsync(new SessionDigestResult("", "hash"), CancellationToken.None));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task SuggestAsync_RunsOneRequestAtATime()
    {
        // One 8 GiB box answers one prompt at a time; overlapping requests only make both slower.
        using var handler = new StubHandler(Answer("Title", "Description")) { DelayMilliseconds = 40 };
        var suggester = Suggester(handler);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => suggester.SuggestAsync(Digest, CancellationToken.None)));

        Assert.Equal(1, handler.MaximumConcurrency);
        Assert.Equal(4, handler.Bodies.Count);
    }

    private static OllamaSessionTitleSuggester Suggester(StubHandler handler) =>
        new(new SessionTitleOptions("http://127.0.0.1:11434"), handler, disposeHandler: false);

    private static string Answer(string title, string description) =>
        JsonSerializer.Serialize(new
        {
            message = new
            {
                role = "assistant",
                content = JsonSerializer.Serialize(new { title, description })
            }
        });

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        private readonly Lock _gate = new();
        private int _concurrency;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
            : this(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            })
        {
        }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<string> Bodies { get; } = [];

        public List<Uri> Uris { get; } = [];

        public List<long?> ContentLengths { get; } = [];

        public int DelayMilliseconds { get; init; }

        public int MaximumConcurrency { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _concurrency++;
                MaximumConcurrency = Math.Max(MaximumConcurrency, _concurrency);
                Uris.Add(request.RequestUri!);
                ContentLengths.Add(request.Content?.Headers.ContentLength);
                Bodies.Add(request.Content is null
                    ? string.Empty
                    : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            }

            try
            {
                if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, cancellationToken);
                return _respond(request);
            }
            finally
            {
                lock (_gate) _concurrency--;
            }
        }
    }
}
