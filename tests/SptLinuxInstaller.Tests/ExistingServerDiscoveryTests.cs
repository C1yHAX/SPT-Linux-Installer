using System.Text.Json.Nodes;
using SptLinuxInstaller.Core;
using Xunit;

namespace SptLinuxInstaller.Tests;

public class ExistingServerDiscoveryTests
{
    [Fact]
    public void ExistingConstructorAndLegacyProtocolKeepEmptyDiscoveryDefaults()
    {
        var constructed = new ServerStatus(false, "", false, "", "spt-server", "");
        Assert.Empty(constructed.ExistingServers);
        Assert.Equal("", constructed.InstallDirectory);
        Assert.Equal("", constructed.ExistingDiscoveryWarning);
        var parsed = Parse(Data());
        Assert.Empty(parsed.ExistingServers);
        Assert.Equal("", parsed.InstallDirectory);
        Assert.Equal("", parsed.ExistingDiscoveryWarning);
    }

    [Fact]
    public void RunningManualServerIsSeparateFromAbsentSelectedManagedTarget()
    {
        var data = Data();
        data["installDirectory"] = "/opt/spt-server";
        data["existingServers"] = new JsonArray(ManualServer());
        var status = Parse(data);
        Assert.False(status.Installed);
        Assert.False(status.Active);
        Assert.Equal("spt-server", status.ServiceName);
        Assert.Equal("/opt/spt-server", status.InstallDirectory);
        var existing = Assert.Single(status.ExistingServers);
        Assert.Equal("spt-fika-server.service", existing.ServiceName);
        Assert.Equal("/srv/spt-fika/server", existing.InstallDirectory);
        Assert.True(existing.Active);
        Assert.Equal("4.1.6", existing.Version);
        Assert.Equal("https://example.org:6969", existing.Address);
        Assert.Equal("", status.ExistingDiscoveryWarning);
    }

    [Fact]
    public void UnknownFieldsAndUnavailableOptionalVersionOrAddressRemainCompatible()
    {
        var data = Data(); var entry = ManualServer();
        entry.Remove("version"); entry.Remove("address"); entry["futureField"] = "ignored";
        data["existingServers"] = new JsonArray(entry);
        data["futureStatusField"] = new JsonObject();
        var status = Parse(data);
        var existing = Assert.Single(status.ExistingServers);
        Assert.Equal("", existing.Version); Assert.Equal("", existing.Address);
        Assert.Equal("", status.ExistingDiscoveryWarning);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("{}")]
    [InlineData("{\"serviceName\":\"spt\",\"installDirectory\":\"relative/path\",\"active\":true}")]
    [InlineData("{\"serviceName\":\" \",\"installDirectory\":\"/srv/spt\",\"active\":true}")]
    [InlineData("{\"serviceName\":\"spt\",\"installDirectory\":\"/srv/spt\",\"active\":\"true\"}")]
    [InlineData("{\"serviceName\":\"spt\",\"installDirectory\":\"/srv/spt\",\"active\":true,\"version\":42}")]
    [InlineData("{\"serviceName\":\"spt\\nother\",\"installDirectory\":\"/srv/spt\",\"active\":true}")]
    public void MalformedEntryIsSkippedWithWarningWithoutLosingValidMainStatus(string badEntry)
    {
        var data = Data();
        data["existingServers"] = new JsonArray(JsonNode.Parse(badEntry), ManualServer());
        var status = Parse(data);
        Assert.Single(status.ExistingServers);
        Assert.False(status.Installed);
        Assert.Contains("unvollständig", status.ExistingDiscoveryWarning);
    }

    [Fact]
    public void OversizedDiscoveryListIsBoundedAndReportsTruncation()
    {
        var data = Data(); var entries = new JsonArray();
        for (var i = 0; i < RemoteProtocol.MaximumExistingServers + 1; i++) entries.Add(ManualServer());
        data["existingServers"] = entries;
        var status = Parse(data);
        Assert.Equal(64, status.ExistingServers.Count);
        Assert.Contains("überzählige", status.ExistingDiscoveryWarning);
    }

    [Fact]
    public void OversizedStringsAreSkippedAndBackendWarningIsPreserved()
    {
        var data = Data(); var entry = ManualServer(); entry["version"] = new string('x', 129);
        data["existingServers"] = new JsonArray(entry);
        data["existingDiscoveryWarning"] = "Ein Dienst konnte nicht gelesen werden.";
        var status = Parse(data);
        Assert.Empty(status.ExistingServers);
        Assert.StartsWith("Ein Dienst konnte nicht gelesen werden.", status.ExistingDiscoveryWarning);
        Assert.Contains("unvollständig", status.ExistingDiscoveryWarning);
    }

    [Theory]
    [InlineData("existingServers", "{}")]
    [InlineData("existingServers", "null")]
    [InlineData("existingDiscoveryWarning", "false")]
    [InlineData("installDirectory", "42")]
    [InlineData("installDirectory", "\"relative\"")]
    public void MalformedOptionalTopLevelDataDoesNotInvalidateManagedStatus(string field, string value)
    {
        var data = Data(); data[field] = JsonNode.Parse(value);
        var status = Parse(data);
        Assert.False(status.Installed);
        Assert.Empty(status.ExistingServers);
        Assert.Equal("", status.InstallDirectory);
        Assert.Contains("unvollständig", status.ExistingDiscoveryWarning);
    }

    [Fact]
    public void ParsedListCannotBeMutatedByConsumers()
    {
        var data = Data(); data["existingServers"] = new JsonArray(ManualServer());
        var status = Parse(data);
        var list = Assert.IsAssignableFrom<IList<ExistingServer>>(status.ExistingServers);
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    [Fact]
    public void NewDiscoveryFieldsUseTheExistingCredentialRedactionBeforeReturningToUi()
    {
        var connection = new ConnectionOptions("example.org", 22, "root", AuthenticationMode.Password,
            "secret-value", "", "secret-phrase");
        var status = new ServerStatus(false, "", false, "", "spt-server", "")
        {
            InstallDirectory = "/opt/secret-value",
            ExistingDiscoveryWarning = "secret-phrase apiKey=another-secret",
            ExistingServers = [new("service-secret-value", "/srv/secret-phrase", true,
                "secret-value", "https://secret-phrase.example.org")]
        };
        var safe = InstallerService.SanitizeStatus(status, connection);
        Assert.DoesNotContain("secret-value", safe.InstallDirectory);
        Assert.DoesNotContain("secret-phrase", safe.ExistingDiscoveryWarning);
        Assert.DoesNotContain("another-secret", safe.ExistingDiscoveryWarning);
        Assert.All(safe.ExistingServers, entry =>
        {
            Assert.DoesNotContain("secret-value", entry.ToString());
            Assert.DoesNotContain("secret-phrase", entry.ToString());
        });
    }

    private static JsonObject Data() => new()
    {
        ["installed"] = false, ["version"] = "", ["active"] = false,
        ["address"] = "https://example.org:6969", ["serviceName"] = "spt-server", ["detail"] = ""
    };

    private static JsonObject ManualServer() => new()
    {
        ["serviceName"] = "spt-fika-server.service", ["installDirectory"] = "/srv/spt-fika/server",
        ["active"] = true, ["version"] = "4.1.6", ["address"] = "https://example.org:6969"
    };

    private static ServerStatus Parse(JsonObject data)
    {
        var parser = new RemoteProtocol();
        parser.Accept(new JsonObject { ["event"] = "result", ["data"] = data }.ToJsonString(), (_, _) => { });
        Assert.False(parser.Invalid);
        Assert.Null(parser.Error);
        return Assert.IsType<ServerStatus>(parser.Status);
    }
}
