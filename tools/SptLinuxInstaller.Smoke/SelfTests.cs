using System.Text.Json;
using System.Text.Json.Nodes;
using SptLinuxInstaller.Core;

namespace SptLinuxInstaller.Smoke;

internal static class SelfTests
{
    public static int Run()
    {
        var results = new List<object>();
        var failed = 0;
        void Check(string name, Action assertion)
        {
            try { assertion(); results.Add(new { name, passed = true }); }
            catch (Exception error)
            {
                failed++;
                results.Add(new { name, passed = false, errorType = error.GetType().Name, error = error.Message });
            }
        }

        var deployment = new DeploymentOptions("/opt/spt-installer-smoke", "spt-installer-smoke", 16969, "127.0.0.1", "127.0.0.1");
        Check("isolated deployment accepted", () => InputValidation.ValidateDeployment(deployment));
        foreach (var invalidPath in new[] { "/", "/opt", "/opt/../srv/existing-server", "/opt/spt-installer-smoke/", "/srv/existing-server", "/opt/test\n", "/opt/test;id" })
            Check("reject deployment path " + JsonSerializer.Serialize(invalidPath), () => Reject<ArgumentException>(() =>
                InputValidation.ValidateDeployment(deployment with { InstallDirectory = invalidPath })));
        foreach (var host in new[] { "127.1", "2130706433", "0177.0.0.1", "0x7f000001", "127.0.0.1:6969", "https://host", "host;id", "host\n", "host/" })
            Check("reject advertised host " + JsonSerializer.Serialize(host), () => Reject<ArgumentException>(() =>
                InputValidation.ValidateDeployment(deployment with { AdvertisedHost = host })));
        Check("reject wildcard advertised address", () => Reject<ArgumentException>(() =>
            InputValidation.ValidateDeployment(deployment with { AdvertisedHost = "0.0.0.0" })));
        Check("reject privileged backend port", () => Reject<ArgumentException>(() =>
            InputValidation.ValidateDeployment(deployment with { Port = 443 })));
        Check("reject arbitrary bind address", () => Reject<ArgumentException>(() =>
            InputValidation.ValidateDeployment(deployment with { BindAddress = "192.0.2.1" })));
        Check("connection record does not print password or passphrase", () =>
        {
            var text = new ConnectionOptions("example.com", 22, "root", AuthenticationMode.Password,
                "password-marker", "key-path", "passphrase-marker").ToString();
            Require(!text.Contains("password-marker") && !text.Contains("passphrase-marker"), "Record leaked credentials.");
        });

        Check("valid release is parsed", () => Require(ReleaseService.Parse(Release().ToJsonString()).Version == "4.1.6", "Unexpected version."));
        foreach (var flag in new[] { "draft", "prerelease" })
            Check("reject " + flag, () =>
            {
                var data = Release(); data[flag] = true;
                Reject<InvalidDataException>(() => ReleaseService.Parse(data.ToJsonString()));
            });
        Check("reject missing SHA256", () =>
        {
            var data = Release(); data["assets"]![0]!["digest"] = null;
            Reject<InvalidDataException>(() => ReleaseService.Parse(data.ToJsonString()));
        });
        Check("reject duplicate matching release assets", () =>
        {
            var data = Release(); var assets = data["assets"]!.AsArray(); assets.Add(assets[0]!.DeepClone());
            Reject<InvalidDataException>(() => ReleaseService.Parse(data.ToJsonString()));
        });
        Check("reject foreign asset download origin", () =>
        {
            var data = Release(); data["assets"]![0]!["browser_download_url"] = "https://example.com/SPT-4.1.6-40743-731d7a2.7z";
            Reject<InvalidDataException>(() => ReleaseService.Parse(data.ToJsonString()));
        });
        Check("reject branch disguised as stable version", () =>
        {
            var data = Release(); data["tag_name"] = "4.1.6-BE";
            Reject<InvalidDataException>(() => ReleaseService.Parse(data.ToJsonString()));
        });
        Check("reject asset from another patch version", () =>
        {
            var data = Release(); data["assets"]![0]!["name"] = "SPT-4.1.5-40743-731d7a2.7z";
            Reject<InvalidDataException>(() => ReleaseService.Parse(data.ToJsonString()));
        });
        Check("reject empty release archive", () =>
        {
            var data = Release(); data["assets"]![0]!["size"] = 0;
            Reject<InvalidDataException>(() => ReleaseService.Parse(data.ToJsonString()));
        });

        var testDirectory = Path.Combine(Program.FindRepository(), "artifacts", "smoke", "self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            var expected = "SHA256:" + new string('A', 43);
            var other = "SHA256:" + new string('B', 43);
            Check("unaccepted first key is not persisted", () =>
            {
                var path = Path.Combine(testDirectory, "declined.json");
                var store = new KnownHostStore(path);
                Require(!store.Verify("server.example", 22, "ssh-ed25519", expected, _ => false), "Unknown key accepted.");
                Require(!File.Exists(path), "Rejected key persisted.");
            });
            Check("exact known key survives host canonicalization", () =>
            {
                var path = Path.Combine(testDirectory, "valid.json");
                var store = new KnownHostStore(path);
                Require(store.Verify("SERVER.example.", 22, "ssh-ed25519", expected, p => p.Fingerprint == expected), "Expected key rejected.");
                Require(store.Verify("server.example", 22, "ssh-ed25519", expected, _ => throw new Exception("Known key prompted again.")), "Stored key rejected.");
                Reject<HostKeyMismatchException>(() => store.Verify("server.example", 22, "ssh-ed25519", other, _ => true));
                Require(store.Verify("server.example", 22, "ssh-ed25519", expected, _ => false), "Mismatch replaced trusted key.");
            });
            Check("corrupt known-host store fails closed", () =>
            {
                var path = Path.Combine(testDirectory, "corrupt.json");
                File.WriteAllText(path, "{not JSON}");
                var asked = false;
                Reject<InvalidDataException>(() => new KnownHostStore(path).Verify("server.example", 22, "ssh-ed25519", expected, _ => { asked = true; return true; }));
                Require(!asked, "Corrupt store became a new trust prompt.");
            });
            Check("progress redacts key path and credential-shaped fields", () =>
            {
                var sanitized = Program.Sanitize("\u001b[31mkey-path password=secret api_key=token\u0000", ["key-path"]);
                Require(!sanitized.Contains("key-path") && !sanitized.Contains("secret") && !sanitized.Contains("=token") &&
                    !sanitized.Contains('\u001b') && !sanitized.Contains('\0'), "Progress sanitization failed.");
            });
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(testDirectory)) File.Delete(file);
            Directory.Delete(testDirectory);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "self-test", ok = failed == 0, total = results.Count, failed, results }));
        return failed == 0 ? 0 : 1;
    }

    private static JsonObject Release() => new()
    {
        ["draft"] = false, ["prerelease"] = false, ["tag_name"] = "4.1.6",
        ["html_url"] = "https://github.com/SP-Tushonka/build/releases/tag/4.1.6", ["published_at"] = "2026-09-18T15:45:30Z",
        ["assets"] = new JsonArray(new JsonObject
        {
            ["name"] = "SPT-4.1.6-40743-731d7a2.7z", ["size"] = 154768026,
            ["browser_download_url"] = "https://github.com/SP-Tushonka/build/releases/download/4.1.6/SPT-4.1.6-40743-731d7a2.7z",
            ["digest"] = "sha256:a0713f6e5f3dbaf5c1cad16c550f28ec9241a8353262d900cea1ca2261adbc3c"
        })
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
    }
}
