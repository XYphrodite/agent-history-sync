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

    public async Task<SessionAnnotationDraft?> SuggestAsync(
        SessionDigestResult digest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (_chatEndpoint is null || digest.IsEmpty || cancellationToken.IsCancellationRequested) return null;

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
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
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ReadDraft(body);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                             or JsonException or InvalidOperationException or IOException)
        {
            // Unreachable, timed out, cancelled, or answering something that is not an answer.
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
            new { role = "user", content = "Session transcript:\n\n" + digest.Text }
        }
    };

    private string SystemPrompt()
    {
        var language = _options.Language switch
        {
            "ru" => "Answer in Russian.",
            "en" => "Answer in English.",
            _ => "Answer in the language the USER speaks in the transcript."
        };

        return "You name coding sessions. From the transcript return JSON. \"title\": two to five words " +
               "naming the concrete subject of the work - a component, a file, a machine, a feature or a bug. " +
               "Never a generic phrase, never the words session or chat, no trailing period. " +
               "\"description\": one sentence of at most 140 characters saying what was actually done and how it " +
               "ended. " + language + " Use only what the transcript says and invent nothing.";
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
