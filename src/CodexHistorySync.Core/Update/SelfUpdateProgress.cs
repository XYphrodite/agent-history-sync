namespace CodexHistorySync.Core.Update;

public enum SelfUpdatePhase
{
    Checking,
    Downloading,
    Verifying,
    Installing
}

/// <summary>Reported synchronously, in order, as an update proceeds. Byte counts describe only the download.</summary>
public sealed record SelfUpdateProgress(
    SelfUpdatePhase Phase,
    ReleaseDescriptor? Release = null,
    long ReceivedBytes = 0,
    long? TotalBytes = null);
