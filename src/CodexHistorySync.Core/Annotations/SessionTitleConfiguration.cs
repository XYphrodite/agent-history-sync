using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CodexHistorySync.Core.Annotations;

/// <summary>
/// What titling was configured with, and why it is off when it is. Nothing here throws: a bad
/// configuration turns the feature off and says so, it never takes the session list down with it.
/// </summary>
public sealed record SessionTitleConfiguration(SessionTitleOptions Options, string? Rejection)
{
    public const int CurrentSchemaVersion = 1;

    public const string EndpointVariable = "AGENT_SYNC_TITLE_ENDPOINT";
    public const string ModelVariable = "AGENT_SYNC_TITLE_MODEL";
    public const string LanguageVariable = "AGENT_SYNC_TITLE_LANGUAGE";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => Options.Endpoint is not null;

    /// <summary>
    /// Reads <c>%LOCALAPPDATA%\CodexHistorySync\titles.json</c>, then lets the environment override
    /// it. There is no default endpoint: with nothing configured, no session text leaves the machine.
    /// </summary>
    public static SessionTitleConfiguration Load(
        string? localAppDataDirectory = null,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var root = Root(localAppDataDirectory);

        string? endpoint = null;
        string? model = null;
        string? language = null;

        if (!string.IsNullOrWhiteSpace(root))
        {
            var path = PathFor(localAppDataDirectory);
            if (File.Exists(path))
            {
                TitleFile? file;
                try
                {
                    file = JsonSerializer.Deserialize<TitleFile>(File.ReadAllText(path), JsonOptions);
                }
                catch (Exception exception) when (exception is JsonException or IOException
                                                     or UnauthorizedAccessException)
                {
                    return Off($"{path} could not be read: {exception.Message}");
                }

                if (file is null) return Off($"{path} is empty.");
                if (file.SchemaVersion != CurrentSchemaVersion)
                {
                    return Off($"{path} has schema version {file.SchemaVersion}, which this build does not know.");
                }

                endpoint = file.Endpoint;
                model = file.Model;
                language = file.Language;
            }
        }

        endpoint = FirstSet(environment(EndpointVariable), endpoint);
        model = FirstSet(environment(ModelVariable), model);
        language = FirstSet(environment(LanguageVariable), language);

        if (string.IsNullOrWhiteSpace(endpoint)) return new SessionTitleConfiguration(new SessionTitleOptions(null), null);

        var rejection = RejectEndpoint(endpoint);
        return rejection is not null
            ? Off(rejection)
            : new SessionTitleConfiguration(
                new SessionTitleOptions(
                    endpoint.Trim(),
                    string.IsNullOrWhiteSpace(model) ? SessionTitleOptions.DefaultModel : model.Trim(),
                    string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim()),
                null);
    }

    /// <summary>The file the command writes and <see cref="Load"/> reads.</summary>
    public static string PathFor(string? localAppDataDirectory = null) =>
        Path.Combine(Root(localAppDataDirectory), "CodexHistorySync", "titles.json");

    /// <summary>
    /// Writes the configuration, or refuses it and writes nothing. The endpoint is checked here
    /// as well as on load, so a bad address is refused when it is typed rather than silently
    /// stored and ignored later.
    /// </summary>
    public static SessionTitleConfiguration Save(
        SessionTitleOptions options,
        string? localAppDataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new ArgumentException("An endpoint is required.", nameof(options));

        if (RejectEndpoint(options.Endpoint) is { } rejection) return Off(rejection);

        var saved = new SessionTitleOptions(
            options.Endpoint.Trim(),
            string.IsNullOrWhiteSpace(options.Model) ? SessionTitleOptions.DefaultModel : options.Model.Trim(),
            string.IsNullOrWhiteSpace(options.Language) ? "auto" : options.Language.Trim().ToLowerInvariant());

        var path = PathFor(localAppDataDirectory);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".titles.json.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new TitleFile(CurrentSchemaVersion, saved.Endpoint, saved.Model, saved.Language),
                new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return new SessionTitleConfiguration(saved, null);
    }

    /// <summary>Turns titling off by removing the file. True when there was one to remove.</summary>
    public static bool Disable(string? localAppDataDirectory = null)
    {
        var path = PathFor(localAppDataDirectory);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Why an endpoint is not acceptable, or null when it is.</summary>
    public static string? Reject(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ? "An endpoint is required." : RejectEndpoint(endpoint);

    private static string Root(string? localAppDataDirectory)
    {
        var root = localAppDataDirectory
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(root) ? string.Empty : root;
    }

    private static SessionTitleConfiguration Off(string rejection) =>
        new(new SessionTitleOptions(null), rejection);

    private static string? FirstSet(string? overriding, string? configured) =>
        string.IsNullOrWhiteSpace(overriding) ? configured : overriding;

    /// <summary>
    /// A session digest is the whole conversation, so it may go only to this machine or to a host
    /// on a private network the operator named by address. A DNS name is refused because it can be
    /// pointed anywhere after it is configured.
    /// </summary>
    private static string? RejectEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"'{endpoint}' is not an http or https address.";
        }

        var host = uri.Host.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return null;

        if (!IPAddress.TryParse(host, out var address))
        {
            return $"'{host}' is a name, not an address; titling accepts this machine or a private address.";
        }

        return IsLocalOrPrivate(address)
            ? null
            : $"'{host}' is a public address; titling accepts this machine or a private address.";
    }

    private static bool IsLocalOrPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Unique-local (fc00::/7) and link-local (fe80::/10).
            var v6 = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (v6[0] & 0xFE) == 0xFC;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            10 => true,
            127 => true,
            169 when octets[1] == 254 => true,
            172 when octets[1] >= 16 && octets[1] <= 31 => true,
            192 when octets[1] == 168 => true,
            // 100.64.0.0/10 - carrier-grade NAT, and where every tailnet node lives.
            100 when octets[1] >= 64 && octets[1] <= 127 => true,
            _ => false
        };
    }

    private sealed record TitleFile(int SchemaVersion, string? Endpoint, string? Model, string? Language);
}
