using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SptLinuxInstaller.Core;

namespace SptLinuxInstaller.Smoke;

internal static class Program
{
    private static readonly HashSet<string> Actions = ["status", "install", "start", "stop", "restart", "logs", "rollback"];
    private static readonly DeploymentOptions IsolatedDeployment = new(
        "/opt/spt-installer-smoke", "spt-installer-smoke", 16969, "127.0.0.1", "127.0.0.1");

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--self-test"]) return SelfTests.Run();
        var redact = new List<string>();
        var action = args.FirstOrDefault() ?? "";
        string? pinFile = null;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (args.Length is < 1 or > 2 || !Actions.Contains(action) ||
                (args.Length == 2 && (action != "install" || args[1] != "4.1.5")))
                throw new ArgumentException("Usage: status|install [4.1.5]|start|stop|restart|logs|rollback; offline checks: --self-test.");

            var host = RequiredEnvironment("SPT_SMOKE_HOST");
            var key = RequiredEnvironment("SPT_SMOKE_KEY");
            var expectedFingerprint = RequiredEnvironment("SPT_SMOKE_FINGERPRINT");
            redact.AddRange([key, expectedFingerprint]);
            if (!Regex.IsMatch(expectedFingerprint, @"\ASHA256:[A-Za-z0-9+/]{43}\z"))
                throw new ArgumentException("SPT_SMOKE_FINGERPRINT must be an exact unpadded OpenSSH SHA256 fingerprint.");
            var username = Environment.GetEnvironmentVariable("SPT_SMOKE_USERNAME") ?? "root";
            var connection = new ConnectionOptions(host, 22, username,
                AuthenticationMode.PrivateKey, "", key, "");
            InputValidation.ValidateConnection(connection);
            InputValidation.ValidateDeployment(IsolatedDeployment);

            var artifactDirectory = Path.Combine(FindRepository(), "artifacts", "smoke");
            Directory.CreateDirectory(artifactDirectory);
            pinFile = Path.Combine(artifactDirectory, "known-hosts-" + Guid.NewGuid().ToString("N") + ".json");
            ReleaseInfo? release = action == "install"
                ? args.Length == 2
                    ? await GetPinnedReleaseAsync(args[1], cancellation.Token)
                    : await new ReleaseService().GetLatestAsync(cancellation.Token)
                : null;

            var progress = new DirectProgress(p => Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                @event = "progress", level = p.Level, message = Sanitize(p.Message, redact)
            })));
            var installer = new InstallerService(pinFile);
            var status = await installer.ExecuteAsync(connection, IsolatedDeployment, action, release,
                prompt => string.Equals(prompt.Fingerprint, expectedFingerprint, StringComparison.Ordinal),
                progress, cancellation.Token);
            WriteResult(new
            {
                @event = "result", ok = true, action,
                installDirectory = IsolatedDeployment.InstallDirectory,
                serviceName = IsolatedDeployment.ServiceName, port = IsolatedDeployment.Port,
                requestedVersion = release?.Version,
                data = status with { Detail = Sanitize(status.Detail, redact) }
            });
            return 0;
        }
        catch (OperationCanceledException)
        {
            WriteResult(new { @event = "result", ok = false, action, error = "Operation cancelled before a confirmed result." });
            return 130;
        }
        catch (Exception exception)
        {
            WriteResult(new { @event = "result", ok = false, action,
                errorType = exception.GetType().Name, error = Sanitize(exception.Message, redact) });
            return exception is ArgumentException ? 2 : exception is HostKeyMismatchException ? 3 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (pinFile != null)
            {
                foreach (var file in new[] { pinFile, pinFile + ".lock" })
                {
                    try { if (File.Exists(file)) File.Delete(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static async Task<ReleaseInfo> GetPinnedReleaseAsync(string version, CancellationToken token)
    {
        var url = "https://api.github.com/repos/SP-Tushonka/build/releases/tags/" + version;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(40) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SptLinuxInstaller-Smoke", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.AbsoluteUri != url)
            throw new InvalidDataException("Unexpected release API redirect.");
        var release = ReleaseService.Parse(await response.Content.ReadAsStringAsync(token));
        if (release.Version != version) throw new InvalidDataException("The requested release tag does not match the response.");
        return release;
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new ArgumentException("Required environment variable is missing: " + name);

    internal static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "SptLinuxInstaller.Core", "SptLinuxInstaller.Core.csproj")))
                return directory.FullName;
        throw new InvalidOperationException("Run the smoke harness from its repository build output.");
    }

    internal static string Sanitize(string message, IEnumerable<string> redactions)
    {
        foreach (var value in redactions.Where(value => !string.IsNullOrEmpty(value)).OrderByDescending(value => value.Length))
            message = message.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        message = Regex.Replace(message, @"\x1b\[[0-?]*[ -/]*[@-~]", "");
        message = Regex.Replace(message, @"(?i)(password|passphrase|api[_ -]?key|access[_ -]?token)(\s*[:=]\s*)[^\s,;]+", "$1$2[REDACTED]");
        return new string(message.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
    }

    private static void WriteResult<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value));

    private sealed class DirectProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }
}
