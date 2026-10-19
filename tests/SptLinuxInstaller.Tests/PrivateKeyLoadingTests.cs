using System.Security.Cryptography;
using SptLinuxInstaller.Core;
using Xunit;

namespace SptLinuxInstaller.Tests;

public sealed class PrivateKeyLoadingTests : IDisposable
{
    private const string Passphrase = "fixture-only-correct-passphrase";
    private const string WrongPassphrase = "fixture-only-wrong-passphrase";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SptPrivateKeyTests-" + Guid.NewGuid().ToString("N"));
    private string KeyPath => Path.Combine(_directory, "private-location-not-for-error-output.key");

    public PrivateKeyLoadingTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("")]
    [InlineData("This is not a private key.")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIThisIsOnlyAPublicKey user@example.org")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nAA==\n-----END PRIVATE KEY-----\n")]
    public void EmptyPublicAndMalformedFilesHaveSafeGermanExplanation(string contents)
    {
        File.WriteAllText(KeyPath, contents);
        var ex = Assert.Throws<SshLoginException>(() => InstallerService.LoadPrivateKey(KeyPath, WrongPassphrase));
        Assert.Equal(SshLoginFailure.PrivateKeyInvalid, ex.Failure);
        Assert.Contains("öffentliche Datei (.pub)", ex.Message);
        Assert.Contains("Passphrase", ex.Message);
        AssertSafe(ex);
    }

    [Fact]
    public void UnencryptedPrivateKeyStillLoads()
    {
        using var rsa = RSA.Create(2048);
        File.WriteAllText(KeyPath, rsa.ExportRSAPrivateKeyPem());
        using var key = InstallerService.LoadPrivateKey(KeyPath, "");
        Assert.NotEmpty(key.HostKeyAlgorithms);
    }

    [Fact]
    public void EncryptedPrivateKeyLoadsWithItsCorrectPassphrase()
    {
        WriteEncryptedKey();
        using var key = InstallerService.LoadPrivateKey(KeyPath, Passphrase);
        Assert.NotEmpty(key.HostKeyAlgorithms);
    }

    [Fact]
    public void WrongPassphraseUsesSafeExplanationWithoutLeakingParserData()
    {
        WriteEncryptedKey();
        var ex = Assert.Throws<SshLoginException>(() => InstallerService.LoadPrivateKey(KeyPath, WrongPassphrase));
        Assert.Equal(SshLoginFailure.PrivateKeyInvalid, ex.Failure);
        Assert.Contains("verschlüsselten Schlüsseln die Passphrase", ex.Message);
        AssertSafe(ex);
    }

    [Fact]
    public void ExplicitMissingPassphraseErrorExplainsTheDifferenceFromAccountPassword()
    {
        File.WriteAllText(KeyPath, "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\n" +
            "DEK-Info: AES-256-CBC,00112233445566778899AABBCCDDEEFF\n\nAA==\n-----END RSA PRIVATE KEY-----\n");
        var ex = Assert.Throws<SshLoginException>(() => InstallerService.LoadPrivateKey(KeyPath, ""));
        Assert.Equal(SshLoginFailure.PrivateKeyPassphraseRequired, ex.Failure);
        Assert.Contains("nicht das SSH-Kontopasswort", ex.Message);
        AssertSafe(ex);
    }

    [Fact]
    public void FileRemovedBeforeParsingDoesNotExposeItsPath()
    {
        var ex = Assert.Throws<SshLoginException>(() => InstallerService.LoadPrivateKey(KeyPath, ""));
        Assert.Equal(SshLoginFailure.PrivateKeyUnreadable, ex.Failure);
        Assert.Contains("lesbare Datei", ex.Message);
        AssertSafe(ex);
    }

    [Fact]
    public async Task InstallerRejectsInvalidFileBeforeAnyHostTrustOrConnectionAttempt()
    {
        File.WriteAllText(KeyPath, "");
        var connection = new ConnectionOptions("127.0.0.1", 1, "operator", AuthenticationMode.PrivateKey,
            "unused-account-password", KeyPath, WrongPassphrase);
        var deployment = new DeploymentOptions("/opt/test-server", "test-server", 6969, "127.0.0.1", "127.0.0.1");
        var service = new InstallerService(Path.Combine(_directory, "unused-known-hosts.json"));
        var ex = await Assert.ThrowsAsync<SshLoginException>(() => service.ExecuteAsync(connection, deployment,
            "status", null, _ => throw new InvalidOperationException("Host trust must not be requested"), null));
        Assert.Equal(SshLoginFailure.PrivateKeyInvalid, ex.Failure);
        AssertSafe(ex);
        Assert.False(File.Exists(Path.Combine(_directory, "unused-known-hosts.json")));
    }

    private void WriteEncryptedKey()
    {
        using var rsa = RSA.Create(2048);
        File.WriteAllText(KeyPath, rsa.ExportEncryptedPkcs8PrivateKeyPem(Passphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)));
    }

    private void AssertSafe(SshLoginException ex)
    {
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(KeyPath, ex.ToString());
        Assert.DoesNotContain("private-location-not-for-error-output", ex.ToString());
        Assert.DoesNotContain(Passphrase, ex.ToString());
        Assert.DoesNotContain(WrongPassphrase, ex.ToString());
        Assert.DoesNotContain("Invalid private key file", ex.ToString());
        Assert.Contains("noch nicht gestartet", ex.Message);
    }

    public void Dispose()
    {
        File.Delete(KeyPath);
        Directory.Delete(_directory);
    }
}
