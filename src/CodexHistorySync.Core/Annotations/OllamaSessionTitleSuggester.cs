using System.Text;
using System.Text.Json;

namespace CodexHistorySync.Core.Annotations;

/// <summary>
/// Asks an OpenAI-shaped local endpoint (Ollama) to name one session. Every failure is answered
/// with null rather than an exception: titling is a convenience on top of a session list that has
/// to keep working when the box holding the model does not.
/// </summary>
public sealed class OllamaSessionTitleSuggester : ISessionTitleSuggester, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>One box, one GPU: overlapping requests only make every one of them slower.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly HttpClient _client;
    private readonly SessionTitleOptions _options;
    private readonly Uri? _chatEndpoint;

    public OllamaSessionTitleSuggester(SessionTitleOptions options)
        : this(
            options,
            new SocketsHttpHandler { ConnectTimeout = SessionTitleOptions.ConnectTimeout },
            disposeHandler: true)
    {
    }

    public OllamaSessionTitleSuggester(SessionTitleOptions options, HttpMessageHandler handler, bool disposeHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        _options = options;
        _chatEndpoint = ResolveChatEndpoint(options.Endpoint);
        _client = new HttpClient(handler, disposeHandler) { Timeout = SessionTitleOptions.RequestTimeout };
    }

    public bool IsConfigured => _chatEndpoint is not null;

    public string? LastFailure { get; private set; }

    public async Task<SessionAnnotationDraft?> SuggestAsync(
        SessionDigestResult digest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        LastFailure = null;
        if (_chatEndpoint is null) { LastFailure = "No endpoint is configured."; return null; }
        if (digest.IsEmpty) { LastFailure = "There was nothing in the session to send."; return null; }
        if (cancellationToken.IsCancellationRequested) { LastFailure = "Cancelled before the request was made."; return null; }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LastFailure = "Cancelled while waiting for the endpoint.";
            return null;
        }

        try
        {
            // The body is serialized up front so the request carries a Content-Length. Ollama
            // closes the connection on a chunked body, which is what streaming the JSON produces.
            using var content = new StringContent(
                JsonSerializer.Serialize(BuildRequest(digest), JsonOptions), Encoding.UTF8, "application/json");
            using var response = await _client
                .PostAsync(_chatEndpoint, content, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var refusal = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                LastFailure = $"The endpoint answered {(int)response.StatusCode}: {Head(refusal)}";
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var draft = ReadDraft(body);
            if (draft is null) LastFailure = $"The answer carried no usable title: {Head(body)}";
            return draft;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                             or JsonException or InvalidOperationException or IOException)
        {
            // Unreachable, timed out, cancelled, or answering something that is not an answer.
            LastFailure = $"{exception.GetType().Name}: {Head(exception.Message)}";
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }

    private static string Head(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(nothing)"
            : value.Length <= 300 ? value.ReplaceLineEndings(" ")
            : value[..300].ReplaceLineEndings(" ");

    private static Uri? ResolveChatEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        var address = endpoint.Trim().TrimEnd('/') + "/api/chat";
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }

    private object BuildRequest(SessionDigestResult digest) => new
    {
        model = _options.Model,
        stream = false,
        // No reasoning: it is what a thinking model spends most of its time and its token
        // budget on, and a budget spent thinking comes back with an empty answer. Measured on
        // one real session: 9.8 s with this off against 38.5 s with it on, same model, and a
        // sharper title. Models without a thinking mode ignore it.
        think = false,
        // The schema is enforced by the server, so a chatty or thinking model still answers JSON.
        format = new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string" },
                description = new { type = "string" }
            },
            required = new[] { "title", "description" }
        },
        options = new { temperature = 0.3, num_ctx = 16384, num_predict = 8000 },
        messages = new object[]
        {
            new { role = "system", content = SystemPrompt() },
            new { role = "user", content = UserPrompt(digest) }
        }
    };

    private static string UserPrompt(SessionDigestResult digest) =>
        string.IsNullOrWhiteSpace(digest.OpeningRequest)
            ? "Session transcript:\n\n" + digest.Text
            : "The user opened with:\n" + digest.OpeningRequest + "\n\nSession transcript:\n\n" + digest.Text;

    private string SystemPrompt()
    {
        var language = _options.Language switch
        {
            "ru" => "Write both the title and the description in Russian.",
            "en" => "Write both the title and the description in English.",
            _ => "Write BOTH the title and the description in the language the USER writes in."
        };

        // Leading with the thing rather than with what was done to it is what separates a name
        // from a status line, and naming the work rather than its loudest problem is what keeps
        // a list of forty sessions readable. Both were measured against real sessions.
        return "You name coding sessions so they can be told apart in a list months later. You are " +
               "given the request the session opened with and then the conversation. Return JSON. " +
               "\"title\": two to five words naming what the session as a whole was about. Lead with the " +
               "thing itself - the component, machine, feature or bug the work was aimed at - not with " +
               "what was done to it: prefer \"QR unlock on the club machines\" over \"Fixing the QR unlock " +
               "problem\". Take it from what the user asked for and what the work became, never from a " +
               "passing detail of one turn. No trailing period, and never the words session or chat. " +
               "\"description\": one sentence of at most 140 characters saying what was actually done and " +
               "how it ended. " + language + " Use only what the transcript says and invent nothing.";
    }

    private SessionAnnotationDraft? ReadDraft(string body)
    {
        using var response = JsonDocument.Parse(body);
        if (!response.RootElement.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String ||
            content.GetString() is not { } answer ||
            string.IsNullOrWhiteSpace(answer))
            return null;

        using var document = JsonDocument.Parse(answer);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

        var title = Normalize(GetString(document.RootElement, "title"), SessionAnnotation.MaximumTitleLength);
        if (title is null) return null;

        var description =
            Normalize(GetString(document.RootElement, "description"), SessionAnnotation.MaximumDescriptionLength);
        return new SessionAnnotationDraft(title, description ?? string.Empty, _options.Model);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Normalize(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var single = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= maximumLength ? single : single[..maximumLength];
    }
}
