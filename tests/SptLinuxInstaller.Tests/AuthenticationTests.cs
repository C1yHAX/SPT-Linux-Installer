using Renci.SshNet;
using Renci.SshNet.Common;
using SptLinuxInstaller.Core;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace SptLinuxInstaller.Tests;

public class AuthenticationTests
{
    private const string Secret = "not-a-real-password-123";
    private static ConnectionOptions Options => new("example.org", 22, "operator",
        AuthenticationMode.Password, Secret, "nonexistent-private-key", "unused-key-passphrase");

    [Fact]
    public void PasswordModeUsesPasswordAndKeyboardInteractiveWithoutReadingOrOfferingAKey()
    {
        using var ssh = new SshAuthentication(Options, null);
        using var sftp = new SshAuthentication(Options, null);
        Assert.Equal(["password", "keyboard-interactive"], ssh.ConnectionInfo.AuthenticationMethods.Select(m => m.Name));
        Assert.NotSame(ssh.ConnectionInfo, sftp.ConnectionInfo);
        Assert.NotSame(ssh.ConnectionInfo.AuthenticationMethods[1], sftp.ConnectionInfo.AuthenticationMethods[1]);
    }

    [Fact]
    public void PrivateKeyModeOffersOnlyTheExplicitKeyAndHasItsOwnRejectionMessage()
    {
        using var rsa = RSA.Create(2048);
        using var bytes = new MemoryStream(Encoding.ASCII.GetBytes(rsa.ExportRSAPrivateKeyPem()));
        using var key = new PrivateKeyFile(bytes);
        using var auth = new SshAuthentication(Options with { AuthenticationMode = AuthenticationMode.PrivateKey }, key);
        Assert.Equal(["publickey"], auth.ConnectionInfo.AuthenticationMethods.Select(m => m.Name));
        var ex = auth.Explain(new SshAuthenticationException("Permission denied (publickey)."));
        Assert.Equal(SshLoginFailure.PrivateKeyRejected, ex.Failure);
        Assert.Contains("öffentliche Schlüssel", ex.Message);
        Assert.DoesNotContain(Secret, ex.ToString());
    }

    [Theory]
    [InlineData("Password:")]
    [InlineData("  password: ")]
    [InlineData("Passwort:")]
    [InlineData("operator's password:")]
    [InlineData("operator@example.org's password:")]
    [InlineData("Password for operator:")]
    [InlineData("Passwort für operator:")]
    public void RecognizedHiddenPasswordPromptReceivesPassword(string text)
    {
        var prompt = new AuthenticationPrompt(0, false, text);
        Responder().Respond(null, Challenge("", prompt));
        Assert.Equal(Secret, prompt.Response);
    }

    [Theory]
    [InlineData("OTP:")]
    [InlineData("Verification code:")]
    [InlineData("Password + OTP:")]
    [InlineData("One-time password:")]
    [InlineData("Password (OTP):")]
    [InlineData("Passcode:")]
    [InlineData("Username:")]
    [InlineData("Enter secret:")]
    [InlineData("Password:\nVerification code:")]
    [InlineData("other-user's password:")]
    [InlineData("")]
    public void UnknownOrMfaPromptNeverReceivesPassword(string text)
    {
        var prompt = new AuthenticationPrompt(0, false, text);
        var ex = Assert.Throws<SshLoginException>(() => Responder().Respond(null, Challenge("", prompt)));
        Assert.Equal(SshLoginFailure.InteractiveAuthenticationUnsupported, ex.Failure);
        Assert.Null(prompt.Response);
        Assert.DoesNotContain(Secret, ex.ToString());
    }

    [Fact]
    public void EchoedPasswordPromptIsRejectedWithoutExposingPassword()
    {
        var prompt = new AuthenticationPrompt(0, true, "Password:");
        Assert.Throws<SshLoginException>(() => Responder().Respond(null, Challenge("", prompt)));
        Assert.Null(prompt.Response);
    }

    [Fact]
    public void MixedChallengeIsRejectedBeforeAnyPasswordResponseIsAssigned()
    {
        var password = new AuthenticationPrompt(0, false, "Password:");
        var otp = new AuthenticationPrompt(1, false, "Verification code:");
        Assert.Throws<SshLoginException>(() => Responder().Respond(null, Challenge("", password, otp)));
        Assert.Null(password.Response);
        Assert.Null(otp.Response);
    }

    [Theory]
    [InlineData("Use your one-time password")]
    [InlineData("MFA is required")]
    [InlineData("Enter verification code")]
    [InlineData("Bitte den Einmalcode eingeben")]
    public void AdditionalFactorInstructionOverridesAnOtherwiseRecognizedPrompt(string instruction)
    {
        var prompt = new AuthenticationPrompt(0, false, "Password:");
        var ex = Assert.Throws<SshLoginException>(() => Responder().Respond(null, Challenge(instruction, prompt)));
        Assert.Equal(SshLoginFailure.InteractiveAuthenticationUnsupported, ex.Failure);
        Assert.Null(prompt.Response);
    }

    [Fact]
    public void ASecondPasswordChallengeDoesNotReuseThePasswordAsAnotherFactor()
    {
        var responder = Responder();
        responder.Respond(null, Challenge("", new AuthenticationPrompt(0, false, "Password:")));
        var second = new AuthenticationPrompt(0, false, "Password:");
        Assert.Throws<SshLoginException>(() => responder.Respond(null, Challenge("", second)));
        Assert.Null(second.Response);
    }

    [Fact]
    public void InformationalChallengeDoesNotConsumeTheSinglePasswordResponse()
    {
        var responder = Responder();
        responder.Respond(null, Challenge("Authorized users only"));
        var prompt = new AuthenticationPrompt(0, false, "Password:");
        responder.Respond(null, Challenge("", prompt));
        Assert.Equal(Secret, prompt.Response);
    }

    [Theory]
    [InlineData("Warning: your password will expire in 7 days.")]
    [InlineData("Warning: your password expires in 7 days.")]
    [InlineData("Warnung: Ihr Passwort läuft in 7 Tagen ab.")]
    public void UpcomingPasswordExpiryWarningAllowsTheFollowingNormalPasswordChallenge(string warning)
    {
        var responder = Responder();
        responder.Respond(null, Challenge(warning));
        var prompt = new AuthenticationPrompt(0, false, "Password:");
        responder.Respond(null, Challenge("", prompt));
        Assert.Equal(Secret, prompt.Response);
    }

    [Fact]
    public void UpcomingExpiryWarningMayAccompanyThePasswordPrompt()
    {
        var prompt = new AuthenticationPrompt(0, false, "Password:");
        Responder().Respond(null, Challenge("Warning: your password will expire in 7 days.", prompt));
        Assert.Equal(Secret, prompt.Response);
    }

    [Fact]
    public void AccountExpiryInformationIsNotMisreportedAsARequiredPasswordChange()
    {
        Responder().Respond(null, Challenge("Your account has expired."));
    }

    [Theory]
    [InlineData("Your password has expired", "Password:")]
    [InlineData("Your password is expired", "Password:")]
    [InlineData("Ihr Passwort ist abgelaufen", "Passwort:")]
    [InlineData("", "New password:")]
    [InlineData("Bitte das Passwort ändern", "Passwort:")]
    public void PasswordChangeNeverReceivesOrReusesTheExistingPassword(string instruction, string text)
    {
        var prompt = new AuthenticationPrompt(0, false, text);
        var ex = Assert.Throws<SshLoginException>(() => Responder().Respond(null, Challenge(instruction, prompt)));
        Assert.Equal(SshLoginFailure.PasswordChangeRequired, ex.Failure);
        Assert.Null(prompt.Response);
    }

    [Fact]
    public void PublicKeyOnlyServerHasActionableGermanErrorInsteadOfWrongPasswordClaim()
    {
        using var auth = new SshAuthentication(Options, null);
        var ex = auth.Explain(new SshAuthenticationException(
            "No suitable authentication method found to complete authentication (publickey)."));
        Assert.Equal(SshLoginFailure.PublicKeyRequired, ex.Failure);
        Assert.Contains("Privater SSH-Schlüssel", ex.Message);
        Assert.Contains("SSH-Konfiguration nicht", ex.Message);
        Assert.DoesNotContain("abgelehnt", ex.Message);
        Assert.DoesNotContain(Secret, ex.ToString());
    }

    [Theory]
    [InlineData("Permission denied (password).")]
    [InlineData("Permission denied (keyboard-interactive).")]
    public void RejectedPasswordExplainsOtherAccountPolicyCausesWithoutClaimingCertainty(string message)
    {
        using var auth = new SshAuthentication(Options, null);
        var ex = auth.Explain(new SshAuthenticationException(message));
        Assert.Equal(SshLoginFailure.PasswordRejected, ex.Failure);
        Assert.Contains("Benutzername und Passwort", ex.Message);
        Assert.Contains("Kontosperre", ex.Message);
    }

    [Theory]
    [InlineData("No suitable authentication method found to complete authentication (publickey,gssapi-with-mic).")]
    [InlineData("No authentication methods defined on SSH server.")]
    public void OtherUnavailableMethodsAreNotMisreportedAsPublicKeyOnly(string message)
    {
        using var auth = new SshAuthentication(Options, null);
        Assert.Equal(SshLoginFailure.AuthenticationMethodUnavailable,
            auth.Explain(new SshAuthenticationException(message)).Failure);
    }

    [Fact]
    public void PartialSuccessImmediatelyStopsBeforeAnotherMethodCanReuseThePassword()
    {
        using var auth = new SshAuthentication(Options, null);
        using var method = auth.Observe(new FakeAuthenticationMethod(AuthenticationResult.PartialSuccess));
        var interrupted = Assert.Throws<SshLoginException>(() => method.Authenticate(null!));
        Assert.Equal(SshLoginFailure.InteractiveAuthenticationUnsupported, interrupted.Failure);
        Assert.Equal(["publickey"], method.AllowedAuthentications);
        var ex = auth.Explain(new SshAuthenticationException(
            "No suitable authentication method found to complete authentication (publickey)."));
        Assert.Equal(SshLoginFailure.InteractiveAuthenticationUnsupported, ex.Failure);
    }

    [Fact]
    public void UnknownAuthenticationErrorDoesNotEchoRawServerTextOrCredential()
    {
        using var auth = new SshAuthentication(Options, null);
        var ex = auth.Explain(new SshAuthenticationException("Untrusted prompt: " + Secret));
        Assert.Equal(SshLoginFailure.AuthenticationFailed, ex.Failure);
        Assert.DoesNotContain(Secret, ex.ToString());
        Assert.DoesNotContain("Untrusted prompt", ex.ToString());
        Assert.Null(ex.InnerException);
    }

    private static PasswordPromptResponder Responder() => new("operator", "example.org", Secret);
    private static AuthenticationPromptEventArgs Challenge(string instruction, params AuthenticationPrompt[] prompts) =>
        new("operator", instruction, "en", prompts);

    private sealed class FakeAuthenticationMethod(AuthenticationResult result) : AuthenticationMethod("operator")
    {
        public override string Name => "password";
        public override AuthenticationResult Authenticate(Session session)
        {
            AllowedAuthentications = ["publickey"];
            return result;
        }
    }
}
