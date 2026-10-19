using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SptLinuxInstaller.Core;
using Renci.SshNet.Sftp;
using System.Runtime.CompilerServices;
using Xunit;

namespace SptLinuxInstaller.Tests;

public class ReleaseTests
{
    internal static JsonObject Fixture() => new()
    {
        ["tag_name"] = "4.1.6", ["prerelease"] = false, ["draft"] = false,
        ["html_url"] = "https://github.com/SP-Tushonka/build/releases/tag/4.1.6",
        ["published_at"] = "2026-09-18T15:45:30Z",
        ["assets"] = new JsonArray(new JsonObject
        {
            ["name"] = "SPT-4.1.6-40743-731d7a2.7z", ["size"] = 154768026,
            ["digest"] = "sha256:" + new string('a', 64),
            ["browser_download_url"] = "https://github.com/SP-Tushonka/build/releases/download/4.1.6/SPT-4.1.6-40743-731d7a2.7z"
        })
    };

    [Fact]
    public void StableOfficialReleaseReturnsVersionBuildAndRequiredDigest()
    {
        var release = ReleaseService.Parse(Fixture().ToJsonString());
        Assert.Equal("4.1.6", release.Version);
        Assert.Equal("40743", release.GameBuild);
        Assert.Equal(new string('a', 64), release.Sha256);
        Assert.Equal(154768026, release.Size);
        Assert.Equal(2026, release.PublishedAt.Year);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    public void UnpublishedOrPreviewReleaseIsRejected(string flag)
    {
        var json = Fixture(); json[flag] = true;
        Assert.Throws<InvalidDataException>(() => ReleaseService.Parse(json.ToJsonString()));
    }

    [Theory]
    [InlineData("4.1.6-beta")]
    [InlineData("4.1")]
    [InlineData("04.1.6")]
    [InlineData("latest")]
    public void NonStableVersionIsRejected(string version)
    {
        var json = Fixture(); json["tag_name"] = version;
        Assert.Throws<InvalidDataException>(() => ReleaseService.Parse(json.ToJsonString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:abcd")]
    [InlineData("sha1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void MissingOrWrongDigestDoesNotFallBackToUncheckedDownload(string? digest)
    {
        var json = Fixture(); json["assets"]![0]!["digest"] = digest;
        Assert.Throws<InvalidDataException>(() => ReleaseService.Parse(json.ToJsonString()));
    }

    [Theory]
    [InlineData("https://github.com/another/build/releases/download/4.1.6/SPT-4.1.6-40743-731d7a2.7z")]
    [InlineData("http://github.com/SP-Tushonka/build/releases/download/4.1.6/SPT-4.1.6-40743-731d7a2.7z")]
    [InlineData("https://github.com.evil.example/SP-Tushonka/build/releases/download/4.1.6/SPT-4.1.6-40743-731d7a2.7z")]
    [InlineData("https://github.com/SP-Tushonka/build/releases/download/4.1.6/SPT-4.1.6-40743-731d7a2.7z?redirect=evil")]
    public void AssetUrlMustExactlyMatchOfficialOwnerTagAndFilename(string url)
    {
        var json = Fixture(); json["assets"]![0]!["browser_download_url"] = url;
        Assert.Throws<InvalidDataException>(() => ReleaseService.Parse(json.ToJsonString()));
    }

    [Fact]
    public void ReleasePageMustBeOfficialEvenWhenAssetLooksValid()
    {
        var json = Fixture(); json["html_url"] = "https://github.com/old-owner/build/releases/tag/4.1.6";
        Assert.Throws<InvalidDataException>(() => ReleaseService.Parse(json.ToJsonString()));
    }

    [Fact]
    public void AmbiguousArchivesAreRejected()
    {
        var json = Fixture(); var assets = json["assets"]!.AsArray(); assets.Add(assets[0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => ReleaseService.Parse(json.ToJsonString()));
    }

    [Fact]
    public void ArchiveForDifferentVersionIsNotSelected()
    {
        var json = Fixture(); json["assets"]![0]!["name"] = "SPT-4.1.5-40743-731d7a2.7z";
        Assert.Throws<InvalidDataException>(() => ReleaseService.Parse(json.ToJsonString()));
    }

    [Fact]
    public async Task LatestUsesNewOfficialRepositoryAndNoCachedFallback()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(ReleaseService.LatestApiUrl, request.RequestUri!.AbsoluteUri);
            Assert.NotEmpty(request.Headers.UserAgent);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture().ToJsonString()) };
        });
        using var client = new HttpClient(handler);
        Assert.Equal("4.1.6", (await new ReleaseService(client).GetLatestAsync()).Version);
        handler.Response = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        await Assert.ThrowsAsync<HttpRequestException>(() => new ReleaseService(client).GetLatestAsync());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Response { get; set; } = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Response(request)); }
    }
}

public class HostKeyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SptLinuxInstallerTests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "known-hosts.json");
    private const string Algorithm = "ssh-ed25519";
    private static readonly string First = "SHA256:" + new string('A', 43);
    private static readonly string Second = "SHA256:" + new string('B', 43);

    [Fact]
    public void DeclinedFirstUseNeverCreatesTrust()
    {
        Assert.False(new KnownHostStore(FilePath).Verify("example.org", 22, Algorithm, First, _ => false));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void AcceptedPinSurvivesNewStoreAndDoesNotPromptAgain()
    {
        Assert.True(new KnownHostStore(FilePath).Verify("Example.ORG", 22, Algorithm, First, prompt =>
        { Assert.Equal("example.org", prompt.Host); return true; }));
        Assert.True(new KnownHostStore(FilePath).Verify("example.org.", 22, Algorithm, First, _ => throw new Exception("Unexpected TOFU prompt")));
        Assert.DoesNotContain("Password", File.ReadAllText(FilePath));
    }

    [Fact]
    public void ChangedHostKeyHardFailsWithoutOverwritingOrAsking()
    {
        var store = new KnownHostStore(FilePath); store.Verify("example.org", 22, Algorithm, First, _ => true);
        var before = File.ReadAllText(FilePath);
        Assert.Throws<HostKeyMismatchException>(() => store.Verify("example.org", 22, Algorithm, Second, _ => throw new Exception("Must not ask")));
        Assert.Equal(before, File.ReadAllText(FilePath));
    }

    [Fact]
    public void ChangedAlgorithmHardFailsEvenWhenFingerprintTextMatches()
    {
        var store = new KnownHostStore(FilePath); store.Verify("example.org", 22, Algorithm, First, _ => true);
        Assert.Throws<HostKeyMismatchException>(() => store.Verify("example.org", 22, "ssh-rsa", First, _ => true));
    }

    [Fact]
    public void DifferentPortRequiresItsOwnTrustDecision()
    {
        var store = new KnownHostStore(FilePath); store.Verify("example.org", 22, Algorithm, First, _ => true);
        Assert.False(store.Verify("example.org", 2222, Algorithm, First, _ => false));
    }

    [Fact]
    public void CorruptPinFileFailsClosed()
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(FilePath, "{broken");
        Assert.Throws<InvalidDataException>(() => new KnownHostStore(FilePath).Verify("example.org", 22, Algorithm, First, _ => true));
        Assert.Equal("{broken", File.ReadAllText(FilePath));
    }

    [Fact]
    public void ConcurrentDecisionIsRecheckedBeforeAtomicWrite()
    {
        var first = new KnownHostStore(FilePath); var other = new KnownHostStore(FilePath);
        Assert.Throws<HostKeyMismatchException>(() => first.Verify("example.org", 22, Algorithm, First, _ =>
        { other.Verify("example.org", 22, Algorithm, Second, _ => true); return true; }));
        Assert.True(other.Verify("example.org", 22, Algorithm, Second, _ => false));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(_directory);
        var allowedPrefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SptLinuxInstallerTests-"));
        if (target.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}

public class ValidationAndProtocolTests
{
    private static ConnectionOptions Connection => new("example.org", 22, "root", AuthenticationMode.Password, "secret-123", "", "passphrase-456");
    private static DeploymentOptions Deployment => new("/opt/spt-server", "spt-server", 6969, "example.org", "0.0.0.0");

    [Fact]
    public void ValidConnectionAndDeploymentAreAccepted()
    { InputValidation.ValidateConnection(Connection); InputValidation.ValidateDeployment(Deployment); }

    [Fact]
    public void SftpModeUsesLibraryOctalDigitsAndGrantsOnlyOwnerReadWrite()
    {
        var attributes = (SftpFileAttributes)RuntimeHelpers.GetUninitializedObject(typeof(SftpFileAttributes));
        attributes.SetPermissions(InstallerService.RestrictedFileMode);
        Assert.True(attributes.OwnerCanRead);
        Assert.True(attributes.OwnerCanWrite);
        Assert.False(attributes.OwnerCanExecute);
        Assert.False(attributes.GroupCanRead);
        Assert.False(attributes.GroupCanWrite);
        Assert.False(attributes.GroupCanExecute);
        Assert.False(attributes.OthersCanRead);
        Assert.False(attributes.OthersCanWrite);
        Assert.False(attributes.OthersCanExecute);
    }

    [Theory]
    [InlineData("https://example.org")]
    [InlineData("example.org:22")]
    [InlineData("example.org;id")]
    [InlineData("127.1")]
    [InlineData("999.999.999.999")]
    [InlineData("example.org\n")]
    public void InvalidSshHostsAreRejected(string host) =>
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateConnection(Connection with { Host = host }));

    [Theory]
    [InlineData("/")]
    [InlineData("/opt")]
    [InlineData("/opt/spt/../other")]
    [InlineData("/opt/spt;id")]
    [InlineData("/srv/spt")]
    public void RemoteInstallPathIsConfinedToDedicatedOptDirectory(string path) =>
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateDeployment(Deployment with { InstallDirectory = path }));

    [Theory]
    [InlineData("ssh.service")]
    [InlineData("spt;reboot")]
    [InlineData("a")]
    [InlineData("SptServer")]
    public void ServiceNameCannotInjectArgumentsOrSpecifyUnitSuffix(string name) =>
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateDeployment(Deployment with { ServiceName = name }));

    [Fact]
    public void InvalidNetworkAndAuthenticationInputsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateConnection(Connection with { Port = 0 }));
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateConnection(Connection with { Password = "" }));
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateConnection(Connection with { Username = "root;id" }));
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateConnection(Connection with { AuthenticationMode = AuthenticationMode.PrivateKey }));
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateDeployment(Deployment with { Port = 443 }));
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateDeployment(Deployment with { AdvertisedHost = "0.0.0.0" }));
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateDeployment(Deployment with { AdvertisedHost = "example.org." }));
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateDeployment(Deployment with { BindAddress = "example.org" }));
    }

    [Fact]
    public void ShellQuotePreservesMetacharactersAsOneLiteralArgument()
    {
        Assert.Equal("'a'\"'\"'b $(touch x) `id`'", InputValidation.ShellQuote("a'b $(touch x) `id`"));
        Assert.Throws<ArgumentException>(() => InputValidation.ShellQuote("a\0b"));
    }

    [Fact]
    public void CredentialsAreAbsentFromRecordDisplayRequestAndSanitizedLogs()
    {
        Assert.DoesNotContain(Connection.Password, Connection.ToString());
        Assert.DoesNotContain(Connection.KeyPassphrase, Connection.ToString());
        var request = InstallerService.BuildRequest(Deployment, ReleaseService.Parse(ReleaseTests.Fixture().ToJsonString()));
        using var json = JsonDocument.Parse(request);
        Assert.Equal(Deployment.InstallDirectory, json.RootElement.GetProperty("installDirectory").GetString());
        Assert.Equal(8, json.RootElement.EnumerateObject().Count());
        Assert.False(json.RootElement.TryGetProperty("password", out _));
        var message = InstallerService.Sanitize("secret-123 passphrase-456 apiKey=unrelated-secret", Connection);
        Assert.DoesNotContain("secret-123", message); Assert.DoesNotContain("passphrase-456", message);
        Assert.DoesNotContain("unrelated-secret", message);
    }

    [Fact]
    public void ProtocolStreamsLogsAndRequiresOneFinalTypedResult()
    {
        var parser = new RemoteProtocol(); var logs = new List<string>();
        parser.Accept("{\"event\":\"log\",\"level\":\"info\",\"message\":\"ready\"}", (message, _) => logs.Add(message));
        parser.Accept("{\"event\":\"result\",\"data\":{\"installed\":true,\"version\":\"4.1.6\",\"active\":true,\"address\":\"https://example.org:6969\",\"serviceName\":\"spt-server\",\"detail\":\"ready\"}}", (_, _) => { });
        Assert.Equal(["ready"], logs); Assert.True(parser.Status!.Active); Assert.False(parser.Invalid);
        parser.Accept("{\"event\":\"result\",\"data\":{}}", (_, _) => { }); Assert.True(parser.Invalid);
    }

    [Fact]
    public void ProtocolErrorIsNeverReportedAsSuccessfulStatus()
    {
        var parser = new RemoteProtocol();
        parser.Accept("{\"event\":\"result\",\"data\":{\"error\":\"disk full\"}}", (_, _) => { });
        Assert.Equal("disk full", parser.Error); Assert.Null(parser.Status);
        var malformed = new RemoteProtocol(); malformed.Accept("not json", (_, _) => { }); Assert.True(malformed.Invalid);
    }

    [Fact]
    public async Task InvalidActionAndPrecancelledOperationDoNotOpenSshConnection()
    {
        var service = new InstallerService(Path.Combine(Path.GetTempPath(), "unused-known-hosts.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync(Connection, Deployment, "delete", null, _ => true, null));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(Connection, Deployment, "status", null, _ => true, null, new CancellationToken(true)));
    }
}
