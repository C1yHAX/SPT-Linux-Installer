using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SptLinuxInstaller.Core;

public enum SshLoginFailure
{
    PublicKeyRequired,
    PasswordRejected,
    InteractiveAuthenticationUnsupported,
    PasswordChangeRequired,
    PrivateKeyRejected,
    AuthenticationMethodUnavailable,
    AuthenticationFailed,
    PrivateKeyUnreadable,
    PrivateKeyPassphraseRequired,
    PrivateKeyInvalid
}

public sealed class SshLoginException : IOException
{
    public SshLoginFailure Failure { get; }

    internal SshLoginException(SshLoginFailure failure) : base(MessageFor(failure)) => Failure = failure;

    private static string MessageFor(SshLoginFailure failure) => failure switch
    {
        SshLoginFailure.PublicKeyRequired =>
            "Der SSH-Server erlaubt für diese Anmeldung derzeit nur einen privaten SSH-Schlüssel (publickey). " +
            "Wähle im Assistenten „Privater SSH-Schlüssel“ und eine passende Schlüsseldatei. " +
            "Ein Passwort kann diese Servervorgabe nicht ersetzen; der Assistent ändert die SSH-Konfiguration nicht.",
        SshLoginFailure.PasswordRejected =>
            "Der SSH-Server hat die Passwortanmeldung abgelehnt. Prüfe Benutzername und Passwort. " +
            "Auch eine Kontosperre oder eine Anmelderichtlinie kann die Ursache sein.",
        SshLoginFailure.InteractiveAuthenticationUnsupported =>
            "Der SSH-Server verlangt zusätzliche oder nicht eindeutig erkennbare interaktive Anmeldeschritte. " +
            "OTP/MFA, mehrere Passwortabfragen und andere Rückfragen werden vom Assistenten nicht unterstützt. " +
            "Das eingegebene Passwort wird nicht als Antwort auf solche Abfragen gesendet.",
        SshLoginFailure.PasswordChangeRequired =>
            "Der SSH-Server verlangt einen Passwortwechsel. Ändere das Passwort zuerst über eine interaktive SSH-Anmeldung " +
            "und versuche es danach erneut. Der Assistent ändert keine Anmeldepasswörter.",
        SshLoginFailure.PrivateKeyRejected =>
            "Der SSH-Server hat den ausgewählten privaten Schlüssel abgelehnt. Prüfe den Benutzernamen und ob der zugehörige " +
            "öffentliche Schlüssel für dieses Konto zugelassen ist.",
        SshLoginFailure.PrivateKeyUnreadable =>
            "Die private SSH-Schlüsseldatei konnte nicht geöffnet werden. Wähle eine vorhandene, lesbare Datei " +
            "und prüfe die Zugriffsrechte. Die SSH-Anmeldung wurde noch nicht gestartet.",
        SshLoginFailure.PrivateKeyPassphraseRequired =>
            "Der private SSH-Schlüssel ist verschlüsselt. Gib die Passphrase dieser Schlüsseldatei ein. " +
            "Gemeint ist die Schlüssel-Passphrase, nicht das SSH-Kontopasswort. Die SSH-Anmeldung wurde noch nicht gestartet.",
        SshLoginFailure.PrivateKeyInvalid =>
            "Die Datei lässt sich nicht als privater SSH-Schlüssel laden. Wähle die private Schlüsseldatei, " +
            "nicht die öffentliche Datei (.pub). Prüfe das Dateiformat und bei verschlüsselten Schlüsseln die Passphrase; " +
            "auch eine beschädigte Datei kann die Ursache sein. Die SSH-Anmeldung wurde noch nicht gestartet.",
        SshLoginFailure.AuthenticationMethodUnavailable =>
            "Der SSH-Server bietet für diese Anmeldung keine passende Anmeldemethode an. " +
            "Prüfe, ob dieses Konto Passwort- oder Schlüsselanmeldung erlaubt, und wähle die passende Methode.",
        _ => "Die SSH-Anmeldung ist fehlgeschlagen. Prüfe Benutzername, die gewählte Anmeldemethode und die Zugriffsregeln des Servers."
    };
}

internal sealed class SshAuthentication : IDisposable
{
    private readonly AuthenticationMode _mode;
    private bool _partialSuccess;
    public ConnectionInfo ConnectionInfo { get; }

    public SshAuthentication(ConnectionOptions options, PrivateKeyFile? key)
    {
        _mode = options.AuthenticationMode;
        AuthenticationMethod[] methods;
        if (_mode == AuthenticationMode.Password)
        {
            var password = new PasswordAuthenticationMethod(options.Username, options.Password);
            password.PasswordExpired += (_, _) => throw new SshLoginException(SshLoginFailure.PasswordChangeRequired);
            var responder = new PasswordPromptResponder(options.Username, options.Host, options.Password);
            var interactive = new KeyboardInteractiveAuthenticationMethod(options.Username);
            interactive.AuthenticationPrompt += responder.Respond;
            methods = [Observe(password), Observe(interactive)];
        }
        else
        {
            ArgumentNullException.ThrowIfNull(key);
            methods = [Observe(new PrivateKeyAuthenticationMethod(options.Username, key))];
        }
        ConnectionInfo = new ConnectionInfo(options.Host, options.Port, options.Username, methods)
        { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal ObservedAuthenticationMethod Observe(AuthenticationMethod method) =>
        new(method, result =>
        {
            if (result != AuthenticationResult.PartialSuccess) return;
            _partialSuccess = true;
            throw new SshLoginException(SshLoginFailure.InteractiveAuthenticationUnsupported);
        });

    public SshLoginException Explain(SshAuthenticationException exception)
    {
        if (_partialSuccess) return new(SshLoginFailure.InteractiveAuthenticationUnsupported);

        var missing = Regex.Match(exception.Message,
            @"\ANo suitable authentication method found to complete authentication \((?<methods>[a-z0-9@._,-]+)\)\.\z",
            RegexOptions.CultureInvariant);
        if (missing.Success)
        {
            var methods = missing.Groups["methods"].Value.Split(',');
            return new(_mode == AuthenticationMode.Password && methods.All(m => m == "publickey")
                ? SshLoginFailure.PublicKeyRequired : SshLoginFailure.AuthenticationMethodUnavailable);
        }
        if (exception.Message == "No authentication methods defined on SSH server.")
            return new(SshLoginFailure.AuthenticationMethodUnavailable);
        if (_mode == AuthenticationMode.Password && exception.Message is
            "Permission denied (password)." or "Permission denied (keyboard-interactive).")
            return new(SshLoginFailure.PasswordRejected);
        if (_mode == AuthenticationMode.PrivateKey && exception.Message == "Permission denied (publickey).")
            return new(SshLoginFailure.PrivateKeyRejected);
        return new(SshLoginFailure.AuthenticationFailed);
    }

    public void Dispose()
    {
        foreach (var method in ConnectionInfo.AuthenticationMethods) method.Dispose();
    }
}

internal sealed class ObservedAuthenticationMethod(AuthenticationMethod method, Action<AuthenticationResult> record)
    : AuthenticationMethod(method.Username)
{
    public override string Name => method.Name;

    public override AuthenticationResult Authenticate(Session session)
    {
        var result = method.Authenticate(session);
        AllowedAuthentications = method.AllowedAuthentications;
        record(result);
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) method.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class PasswordPromptResponder(string username, string host, string password)
{
    private bool _answered;
    private static readonly Regex AdditionalFactor = new(
        @"\b(?:otp|mfa|2fa|totp|hotp|passcode|token|authenticator|duo|push|verification|security\s+code|one[ -]?time|two[ -]?factor|einmal\w*|bestätigungs\w*|sicherheitscode)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PasswordChange = new(
        @"\b(?:password\s+(?:(?:has|is)\s+)?expired|new\s+password|change\w*\s+(?:your\s+)?password|password\s+change|passwort\s+(?:ist\s+)?abgelaufen|neues?\s+passwort|passwort\s+ändern)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public void Respond(object? sender, AuthenticationPromptEventArgs args)
    {
        if (PasswordChange.IsMatch(args.Instruction ?? "") ||
            args.Prompts.Any(prompt => PasswordChange.IsMatch(prompt.Request ?? "")))
            throw new SshLoginException(SshLoginFailure.PasswordChangeRequired);
        if (AdditionalFactor.IsMatch(args.Instruction ?? "") || args.Prompts.Count > 1)
            throw new SshLoginException(SshLoginFailure.InteractiveAuthenticationUnsupported);
        if (args.Prompts.Count == 0) return;
        var prompt = args.Prompts[0];
        if (_answered || prompt.IsEchoed || !IsPasswordPrompt(prompt.Request))
            throw new SshLoginException(SshLoginFailure.InteractiveAuthenticationUnsupported);
        prompt.Response = password;
        _answered = true;
    }

    private bool IsPasswordPrompt(string? request)
    {
        if (request is null || request.Length > 256) return false;
        var value = request.Trim();
        return new[]
        {
            "Password:", "Passwort:",
            username + "'s password:", username + "@" + host + "'s password:",
            "Password for " + username + ":", "Passwort für " + username + ":"
        }.Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}
