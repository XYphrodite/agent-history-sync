using System.Net;
using System.Net.Sockets;

namespace CodexHistorySync.Remote;

public sealed class StoreEndpoint
{
    public Uri RepositoryUri { get; }
    public string Origin => RepositoryUri.GetLeftPart(UriPartial.Authority);
    public string Name { get; }

    public StoreEndpoint(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || value.Length > 512 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("Use a Tailscale repository URL without credentials, query or fragment.");
        var host = uri.IdnHost.Trim('[', ']');
        var allowed = host == "localhost";
        if (IPAddress.TryParse(host, out var address))
        {
            var bytes = address.GetAddressBytes();
            allowed = IPAddress.IsLoopback(address) ||
                (address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                (address.AddressFamily == AddressFamily.InterNetworkV6 && bytes.AsSpan(0, 6).SequenceEqual(new byte[] { 0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0 }));
        }
        else if (uri.Scheme == "https" && host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) allowed = true;
        if (!allowed) throw new InvalidDataException("The storage endpoint must use a Tailscale IP, HTTPS tailnet name, or loopback.");
        var parts = uri.AbsolutePath.TrimEnd('/').Split('/');
        if (parts.Length != 4 || parts[1] != "v1" || parts[2] != "repositories" || !StoreProtocol.IsName(parts[3]) ||
            value.Contains('%') || value.Contains('\\') || value.Contains("/../", StringComparison.Ordinal) || value.Contains("/./", StringComparison.Ordinal))
            throw new InvalidDataException("Expected /v1/repositories/<name> with a lowercase name.");
        if (uri.AbsolutePath != $"/v1/repositories/{parts[3]}" && uri.AbsolutePath != $"/v1/repositories/{parts[3]}/")
            throw new InvalidDataException("Invalid repository path.");
        Name = parts[3];
        RepositoryUri = new Uri($"{uri.GetLeftPart(UriPartial.Authority)}/v1/repositories/{Name}");
    }

    public static bool IsServerUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
}
