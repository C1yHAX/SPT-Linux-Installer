using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace SptLinuxInstaller.Core;

public static class InputValidation
{
    public static void ValidateConnection(ConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsHost(options.Host, allowIpv6: true))
            throw new ArgumentException("SSH-Host muss eine IP-Adresse oder ein DNS-Name ohne Schema und Port sein.");
        if (options.Port is < 1 or > 65535)
            throw new ArgumentException("SSH-Port muss zwischen 1 und 65535 liegen.");
        if (!Regex.IsMatch(options.Username ?? "", @"\A[a-zA-Z_][a-zA-Z0-9_.-]{0,63}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Ungültiger SSH-Benutzername.");
        if (!Enum.IsDefined(options.AuthenticationMode))
            throw new ArgumentException("Unbekannte SSH-Anmeldemethode.");
        if (options.AuthenticationMode == AuthenticationMode.Password && string.IsNullOrEmpty(options.Password))
            throw new ArgumentException("Ein SSH-Passwort ist erforderlich.");
        if (options.AuthenticationMode == AuthenticationMode.PrivateKey &&
            (string.IsNullOrWhiteSpace(options.PrivateKeyPath) || !File.Exists(options.PrivateKeyPath)))
            throw new ArgumentException("Die ausgewählte private SSH-Schlüsseldatei fehlt.");
    }

    public static void ValidateDeployment(DeploymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Regex.IsMatch(options.InstallDirectory ?? "", @"\A/opt/[a-z0-9][a-z0-9-]{0,47}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Installationsverzeichnis muss /opt/<name> sein; erlaubt sind Kleinbuchstaben, Ziffern und Bindestriche.");
        if (!Regex.IsMatch(options.ServiceName ?? "", @"\A[a-z][a-z0-9-]{1,30}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Dienstname benötigt 2–31 Kleinbuchstaben, Ziffern oder Bindestriche und muss mit einem Buchstaben beginnen.");
        if (options.Port is < 1024 or > 65535)
            throw new ArgumentException("SPT-Port muss zwischen 1024 und 65535 liegen.");
        if (!IsHost(options.AdvertisedHost, allowIpv6: false) || options.AdvertisedHost == "0.0.0.0" || options.AdvertisedHost.EndsWith('.'))
            throw new ArgumentException("Die Serveradresse muss eine IPv4-Adresse oder ein DNS-Name ohne Schema und Port sein.");
        if (options.BindAddress is not ("0.0.0.0" or "127.0.0.1"))
            throw new ArgumentException("Bind-Adresse muss 0.0.0.0 oder 127.0.0.1 sein.");
    }

    public static string ShellQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("Shellargument enthält ein Nullzeichen.");
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    internal static string NormalizeHost(string host) => IPAddress.TryParse(host, out var ip)
        ? ip.ToString() : host.TrimEnd('.').ToLowerInvariant();

    internal static bool IsHost(string? value, bool allowIpv6)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 253 ||
            value.Contains('%') || value.Contains('/') || value.Contains('@')) return false;
        if (IPAddress.TryParse(value, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return allowIpv6;
            return value == ip.ToString();
        }
        if (value.All(c => char.IsAsciiDigit(c) || c == '.')) return false;
        return value.TrimEnd('.').Split('.').All(label =>
            Regex.IsMatch(label, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\z", RegexOptions.CultureInvariant));
    }
}
