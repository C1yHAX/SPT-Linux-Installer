using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SptLinuxInstaller.Core;

public sealed class InstallerService
{
    private readonly KnownHostStore _knownHosts;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private static readonly HashSet<string> Actions = ["install", "status", "start", "stop", "restart", "logs", "rollback"];
    internal const short RestrictedFileMode = 600;

    public InstallerService(string knownHostsFile) => _knownHosts = new KnownHostStore(knownHostsFile);

    public async Task<ServerStatus> ExecuteAsync(ConnectionOptions connection, DeploymentOptions options,
        string action, ReleaseInfo? release, Func<HostKeyPrompt, bool> trustHost,
        IProgress<OperationProgress>? progress, CancellationToken token = default)
    {
        InputValidation.ValidateConnection(connection);
        InputValidation.ValidateDeployment(options);
        ArgumentNullException.ThrowIfNull(trustHost);
        if (!Actions.Contains(action)) throw new ArgumentException("Unbekannte Serveraktion.", nameof(action));
        if (action == "install" && release is null) throw new ArgumentException("Für die Installation wird ein geprüfter Release benötigt.");
        if (release is not null) ReleaseService.ValidateRelease(release);
        await _executionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ExecuteCoreAsync(connection, options, action, release,
                trustHost, progress, token), token).ConfigureAwait(false);
        }
        finally { _executionGate.Release(); }
    }

    private async Task<ServerStatus> ExecuteCoreAsync(ConnectionOptions connection, DeploymentOptions options,
        string action, ReleaseInfo? release, Func<HostKeyPrompt, bool> trustHost,
        IProgress<OperationProgress>? progress, CancellationToken token)
    {
        void Report(string message, string level = "info")
        {
            try { progress?.Report(new OperationProgress(Sanitize(message, connection), level)); } catch { }
        }
        using var key = connection.AuthenticationMode == AuthenticationMode.PrivateKey
            ? LoadPrivateKey(connection.PrivateKeyPath, connection.KeyPassphrase) : null;
        using var sshAuthentication = new SshAuthentication(connection, key);
        using var sftpAuthentication = new SshAuthentication(connection, key);
        using var ssh = new SshClient(sshAuthentication.ConnectionInfo) { KeepAliveInterval = TimeSpan.FromSeconds(20) };
        using var sftp = new SftpClient(sftpAuthentication.ConnectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20), OperationTimeout = TimeSpan.FromSeconds(60)
        };
        Exception? hostKeyError = null;
        void VerifyHost(object? sender, HostKeyEventArgs args)
        {
            args.CanTrust = false;
            try
            {
                args.CanTrust = _knownHosts.Verify(connection.Host, connection.Port, args.HostKeyName,
                    "SHA256:" + args.FingerPrintSHA256, trustHost);
                if (!args.CanTrust) hostKeyError = new IOException("Der SSH-Hostschlüssel wurde nicht bestätigt.");
            }
            catch (Exception ex) { hostKeyError = ex; }
        }
        ssh.HostKeyReceived += VerifyHost;
        sftp.HostKeyReceived += VerifyHost;

        var temporary = "/tmp/spt-linux-installer-" + Guid.NewGuid().ToString("N");
        var scriptPath = temporary + "/install.py";
        var requestPath = temporary + "/request.json";
        bool temporaryCreated = false, dispatched = false, completed = false;
        try
        {
            Report("SSH-Verbindung wird hergestellt; Hostschlüssel wird geprüft.");
            try { await ssh.ConnectAsync(token).ConfigureAwait(false); }
            catch (SshAuthenticationException ex) { throw sshAuthentication.Explain(ex); }
            token.ThrowIfCancellationRequested();
            await RunPreparationAsync(ssh,
                "command -v python3 >/dev/null && if [ \"$(id -u)\" != 0 ]; then sudo -n true; fi", token).ConfigureAwait(false);
            await RunPreparationAsync(ssh, "umask 077; mkdir -- " + InputValidation.ShellQuote(temporary), token).ConfigureAwait(false);
            temporaryCreated = true;
            await RunPreparationAsync(ssh, "umask 077; : > " + InputValidation.ShellQuote(scriptPath) +
                "; : > " + InputValidation.ShellQuote(requestPath) + "; chmod 600 -- " +
                InputValidation.ShellQuote(scriptPath) + " " + InputValidation.ShellQuote(requestPath), token).ConfigureAwait(false);

            Report("Installationsprogramm und geprüfte Einstellungen werden per SFTP übertragen.");
            try { await sftp.ConnectAsync(token).ConfigureAwait(false); }
            catch (SshAuthenticationException ex) { throw sftpAuthentication.Explain(ex); }
            using (var script = Assembly.GetExecutingAssembly().GetManifestResourceStream("SptLinuxInstaller.Core.install.py")
                ?? throw new InvalidOperationException("Das eingebettete Linux-Installationsprogramm fehlt."))
                await sftp.UploadFileAsync(script, scriptPath, token).ConfigureAwait(false);
            using (var request = new MemoryStream(BuildRequest(options, release)))
                await sftp.UploadFileAsync(request, requestPath, token).ConfigureAwait(false);
            sftp.ChangePermissions(scriptPath, RestrictedFileMode);
            sftp.ChangePermissions(requestPath, RestrictedFileMode);
            sftp.Disconnect();
            token.ThrowIfCancellationRequested();

            var modifies = action is not ("status" or "logs");
            var invocation = "python3 -u " + InputValidation.ShellQuote(scriptPath) +
                " --request " + InputValidation.ShellQuote(requestPath) + " --action " + InputValidation.ShellQuote(action);
            var commandText = "trap '' HUP; if [ \"$(id -u)\" = 0 ]; then exec " + invocation +
                "; else exec sudo -n " + invocation + "; fi";
            using var command = ssh.CreateCommand(commandText);
            command.CommandTimeout = modifies ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(2);
            if (modifies) Report("Serveraktion startet. Ein Abbruch wartet ab jetzt auf den sicheren Abschluss.");
            var cancellation = modifies ? CancellationToken.None : token;
            using var cancellationNotice = modifies ? token.Register(() =>
                Report("Abbruch angefordert: Die laufende Serveraktion wird bis zu ihrem Ergebnis weiter überwacht.", "warning")) : default;
            var protocol = new RemoteProtocol();
            token.ThrowIfCancellationRequested();
            var execution = command.ExecuteAsync(cancellation);
            dispatched = true;
            var stdout = ReadLinesAsync(command.OutputStream, line => protocol.Accept(line, Report));
            var stderr = ReadLinesAsync(command.ExtendedOutputStream, line => Report(line, "warning"));
            try { await Task.WhenAll(execution, stdout, stderr).ConfigureAwait(false); }
            finally { completed = command.ExitStatus.HasValue; }
            if (command.ExitStatus != 0)
                throw new IOException(Sanitize(protocol.Error ?? $"Die Serveraktion endete mit Exitcode {command.ExitStatus?.ToString() ?? "unbekannt"}.", connection));
            if (protocol.Error is not null) throw new IOException(Sanitize(protocol.Error, connection));
            if (protocol.Invalid || protocol.Status is null)
                throw new InvalidDataException("Der Server lieferte kein gültiges abschließendes Installationsergebnis.");
            return SanitizeStatus(protocol.Status, connection);
        }
        catch (Exception) when (hostKeyError is not null) { throw hostKeyError; }
        catch (OperationCanceledException) { throw; }
        catch (SshLoginException) { throw; }
        catch (Exception ex) when (ex is not HostKeyMismatchException)
        {
            var state = dispatched && !completed
                ? " Die SSH-Verbindung endete ohne bestätigten Abschluss. Die Serveraktion kann noch laufen; zuerst Status/Logs prüfen und nicht erneut installieren." : "";
            throw new IOException(Sanitize(ex.Message, connection) + state);
        }
        finally
        {
            if (temporaryCreated && ssh.IsConnected && (!dispatched || completed))
            {
                try
                {
                    await RunPreparationAsync(ssh, "rm -f -- " + InputValidation.ShellQuote(scriptPath) + " " +
                        InputValidation.ShellQuote(requestPath) + "; rmdir -- " + InputValidation.ShellQuote(temporary),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { Report("Temporäre Übertragungsdateien konnten nicht vollständig entfernt werden: " + temporary, "warning"); }
            }
            else if (temporaryCreated) Report("Temporäre Dateien bleiben für die laufende/ungeklärte Aktion erhalten: " + temporary, "warning");
        }
    }

    internal static byte[] BuildRequest(DeploymentOptions options, ReleaseInfo? release) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            installDirectory = options.InstallDirectory, serviceName = options.ServiceName, port = options.Port,
            advertisedHost = options.AdvertisedHost, bindAddress = options.BindAddress,
            releaseVersion = release?.Version, releaseUrl = release?.DownloadUrl, releaseSha256 = release?.Sha256
        });

    internal static PrivateKeyFile LoadPrivateKey(string path, string? passphrase)
    {
        FileStream file;
        try { file = File.OpenRead(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new SshLoginException(SshLoginFailure.PrivateKeyUnreadable);
        }
        using (file)
        {
            try { return new PrivateKeyFile(file, passphrase ?? ""); }
            catch (SshPassPhraseNullOrEmptyException)
            { throw new SshLoginException(SshLoginFailure.PrivateKeyPassphraseRequired); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                throw new SshLoginException(SshLoginFailure.PrivateKeyInvalid);
            }
        }
    }

    private static async Task RunPreparationAsync(SshClient ssh, string text, CancellationToken token)
    {
        using var command = ssh.CreateCommand(text);
        command.CommandTimeout = TimeSpan.FromSeconds(30);
        await command.ExecuteAsync(token).ConfigureAwait(false);
        if (command.ExitStatus != 0)
            throw new IOException("SSH-Vorbereitung fehlgeschlagen. Python 3, Schreibrecht auf /tmp sowie root oder passwortloses sudo werden benötigt. " + command.Error);
    }

    private static async Task ReadLinesAsync(Stream stream, Action<string> consume)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            if (!string.IsNullOrWhiteSpace(line)) consume(line.Length <= 65536 ? line : "[Remoteausgabe überschreitet das Zeilenlimit]");
    }

    internal static string Sanitize(string text, ConnectionOptions connection)
    {
        foreach (var secret in new[] { connection.Password, connection.KeyPassphrase }.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        text = Regex.Replace(text, @"\x1b\[[0-?]*[ -/]*[@-~]", "");
        text = Regex.Replace(text, @"(?i)(password|passphrase|api[_ -]?key|access[_ -]?token)(\s*[:=]\s*)[^\s,;]+", "$1$2[REDACTED]");
        return text;
    }

    internal static ServerStatus SanitizeStatus(ServerStatus status, ConnectionOptions connection) => status with
    {
        Detail = Sanitize(status.Detail, connection),
        InstallDirectory = Sanitize(status.InstallDirectory, connection),
        ExistingDiscoveryWarning = Sanitize(status.ExistingDiscoveryWarning, connection),
        ExistingServers = Array.AsReadOnly(status.ExistingServers.Select(server => server with
        {
            ServiceName = Sanitize(server.ServiceName, connection),
            InstallDirectory = Sanitize(server.InstallDirectory, connection),
            Version = Sanitize(server.Version, connection),
            Address = Sanitize(server.Address, connection)
        }).ToArray())
    };
}

internal sealed class RemoteProtocol
{
    internal const int MaximumExistingServers = 64;
    public ServerStatus? Status { get; private set; }
    public string? Error { get; private set; }
    public bool Invalid { get; private set; }
    private bool _hasResult;

    public void Accept(string line, Action<string, string> report)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var kind = root.GetProperty("event").GetString();
            if (_hasResult) { Invalid = true; return; }
            if (kind == "log")
            {
                var level = root.TryGetProperty("level", out var severity) ? severity.GetString() : "info";
                report(root.GetProperty("message").GetString() ?? "", level is "warning" or "error" or "success" ? level : "info");
            }
            else if (kind == "result")
            {
                _hasResult = true;
                var data = root.GetProperty("data");
                if (data.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(error.GetString()))
                { Error = error.GetString(); return; }
                var discovery = ReadDiscovery(data);
                Status = new ServerStatus(data.GetProperty("installed").GetBoolean(), Text(data, "version"),
                    data.GetProperty("active").GetBoolean(), Text(data, "address"), Text(data, "serviceName"), Text(data, "detail"))
                {
                    ExistingServers = discovery.Servers,
                    InstallDirectory = discovery.InstallDirectory,
                    ExistingDiscoveryWarning = discovery.Warning
                };
            }
            else Invalid = true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { Invalid = true; }
    }

    private static string Text(JsonElement data, string key) => data.TryGetProperty(key, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString()! : "";

    private static (IReadOnlyList<ExistingServer> Servers, string InstallDirectory, string Warning) ReadDiscovery(JsonElement data)
    {
        var servers = new List<ExistingServer>();
        bool incomplete = false;
        if (!OptionalText(data, "installDirectory", 4096, out var installDirectory) ||
            (installDirectory.Length > 0 && !installDirectory.StartsWith('/')))
        { installDirectory = ""; incomplete = true; }
        if (!OptionalText(data, "existingDiscoveryWarning", 4096, out var warning))
        { warning = ""; incomplete = true; }
        if (data.TryGetProperty("existingServers", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array) incomplete = true;
            else
            {
                incomplete |= list.GetArrayLength() > MaximumExistingServers;
                foreach (var item in list.EnumerateArray().Take(MaximumExistingServers))
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !OptionalText(item, "serviceName", 256, out var service) || string.IsNullOrWhiteSpace(service) || service != service.Trim() ||
                        !OptionalText(item, "installDirectory", 4096, out var directory) || !directory.StartsWith('/') ||
                        !item.TryGetProperty("active", out var active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                        !OptionalText(item, "version", 128, out var version) ||
                        !OptionalText(item, "address", 2048, out var address))
                    { incomplete = true; continue; }
                    servers.Add(new ExistingServer(service, directory, active.GetBoolean(), version, address));
                }
            }
        }
        if (incomplete)
        {
            const string note = "Die Erkennung bestehender Server ist unvollständig: Ungültige oder überzählige Angaben wurden nicht übernommen.";
            warning = warning.Length == 0 ? note : warning + " " + note;
        }
        return (servers.AsReadOnly(), installDirectory, warning);
    }

    private static bool OptionalText(JsonElement item, string key, int maximumLength, out string value)
    {
        value = "";
        if (!item.TryGetProperty(key, out var field)) return true;
        if (field.ValueKind != JsonValueKind.String) return false;
        var text = field.GetString()!;
        if (text.Length > maximumLength || text.Any(char.IsControl)) return false;
        value = text;
        return true;
    }
}
