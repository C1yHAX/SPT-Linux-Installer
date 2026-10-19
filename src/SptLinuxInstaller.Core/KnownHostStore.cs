using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SptLinuxInstaller.Core;

public sealed class HostKeyMismatchException(string message) : IOException(message);

public sealed class KnownHostStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly object _gate;
    public KnownHostStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _gate = Gates.GetOrAdd(_path, _ => new object());
    }

    public bool Verify(string host, int port, string algorithm, string fingerprint, Func<HostKeyPrompt, bool> trustHost)
    {
        ArgumentNullException.ThrowIfNull(trustHost);
        if (!InputValidation.IsHost(host, true) || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(algorithm) ||
            !Regex.IsMatch(fingerprint ?? "", @"\ASHA256:[A-Za-z0-9+/]{43}\z"))
            throw new ArgumentException("Ungültige SSH-Hostschlüssel-Metadaten.");
        var normalizedHost = InputValidation.NormalizeHost(host);
        var key = $"[{normalizedHost}]:{port}";
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            if (Read().TryGetValue(key, out var existing)) return Match(existing, algorithm, fingerprint!, host, port);
        }
        if (!trustHost(new HostKeyPrompt(normalizedHost, port, algorithm, fingerprint!))) return false;
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            var pins = Read();
            if (pins.TryGetValue(key, out var concurrent)) return Match(concurrent, algorithm, fingerprint!, host, port);
            pins.Add(key, new Pin(algorithm, fingerprint!));
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, pins, new JsonSerializerOptions { WriteIndented = true });
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return true;
    }

    private Dictionary<string, Pin> Read()
    {
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        try
        {
            var pins = JsonSerializer.Deserialize<Dictionary<string, Pin>>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Die Hostschlüsseldatei ist leer oder ungültig.");
            if (pins.Values.Any(p => p is null || string.IsNullOrWhiteSpace(p.Algorithm) ||
                !Regex.IsMatch(p.Fingerprint ?? "", @"\ASHA256:[A-Za-z0-9+/]{43}\z")))
                throw new InvalidDataException("Die Hostschlüsseldatei enthält einen ungültigen Eintrag.");
            return pins;
        }
        catch (JsonException ex) { throw new InvalidDataException("Die Hostschlüsseldatei ist beschädigt; vorhandenes Vertrauen wird nicht zurückgesetzt.", ex); }
    }

    private FileStream AcquireFileLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static bool Match(Pin expected, string algorithm, string fingerprint, string host, int port)
    {
        if (expected.Algorithm != algorithm || expected.Fingerprint != fingerprint)
            throw new HostKeyMismatchException($"SSH-Hostschlüssel für {host}:{port} hat sich geändert. Verbindung abgebrochen; gespeicherter Schlüssel wurde nicht ersetzt.");
        return true;
    }

    private sealed record Pin(string Algorithm, string Fingerprint);
}
