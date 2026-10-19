namespace SptLinuxInstaller.Core;

public enum AuthenticationMode { Password, PrivateKey }

public sealed record ConnectionOptions(string Host, int Port, string Username,
    AuthenticationMode AuthenticationMode, string Password, string PrivateKeyPath, string KeyPassphrase)
{
    public override string ToString() => $"{Username}@{Host}:{Port} ({AuthenticationMode})";
}

public sealed record DeploymentOptions(string InstallDirectory, string ServiceName, int Port,
    string AdvertisedHost, string BindAddress);
public sealed record ReleaseInfo(string Version, string GameBuild, string DownloadUrl, string Sha256,
    long Size, string ReleaseUrl, DateTimeOffset PublishedAt);
public sealed record HostKeyPrompt(string Host, int Port, string Algorithm, string Fingerprint);
public sealed record OperationProgress(string Message, string Level = "info");
public sealed record ExistingServer(string ServiceName, string InstallDirectory, bool Active,
    string Version, string Address);
public sealed record ServerStatus(bool Installed, string Version, bool Active, string Address,
    string ServiceName, string Detail)
{
    public IReadOnlyList<ExistingServer> ExistingServers { get; init; } = Array.Empty<ExistingServer>();
    public string InstallDirectory { get; init; } = "";
    public string ExistingDiscoveryWarning { get; init; } = "";
}
