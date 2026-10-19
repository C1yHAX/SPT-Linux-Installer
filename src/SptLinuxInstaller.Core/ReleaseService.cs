using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SptLinuxInstaller.Core;

public sealed class ReleaseService
{
    public const string LatestApiUrl = "https://api.github.com/repos/SP-Tushonka/build/releases/latest";
    private const string Repository = "https://github.com/SP-Tushonka/build";
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(40) };
    private readonly HttpClient _http;

    public ReleaseService(HttpClient? httpClient = null) => _http = httpClient ?? DefaultClient;

    public async Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SptLinuxInstaller", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.AbsoluteUri is string finalUri && finalUri != LatestApiUrl)
            throw new InvalidDataException("Die Release-API hat auf eine unerwartete Adresse umgeleitet.");
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public static ReleaseInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Der angebotene Release ist nicht stabil oder noch unveröffentlicht.");
        var tag = RequiredString(root, "tag_name");
        if (!Regex.IsMatch(tag, @"\A(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Die Release-Version ist keine stabile semantische Version.");
        var version = tag;
        var releaseUrl = RequiredString(root, "html_url");
        if (releaseUrl != $"{Repository}/releases/tag/{tag}")
            throw new InvalidDataException("Der Release stammt nicht aus dem offiziellen SPT-Build-Repository.");
        var published = DateTimeOffset.Parse(RequiredString(root, "published_at"), CultureInfo.InvariantCulture);
        var pattern = @"\ASPT-" + Regex.Escape(version) + @"-(?<build>[0-9]+)-[a-f0-9]{7,40}\.7z\z";
        var matches = root.GetProperty("assets").EnumerateArray()
            .Where(asset => Regex.IsMatch(RequiredString(asset, "name"), pattern, RegexOptions.CultureInvariant)).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("Genau ein passendes offizielles SPT-.7z-Paket wird benötigt.");
        var item = matches[0];
        var name = RequiredString(item, "name");
        var downloadUrl = RequiredString(item, "browser_download_url");
        if (downloadUrl != $"{Repository}/releases/download/{tag}/{name}")
            throw new InvalidDataException("Das SPT-Paket hat eine unerwartete Downloadadresse.");
        var digest = RequiredString(item, "digest");
        if (!Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Für das offizielle Paket fehlt eine gültige SHA-256-Prüfsumme.");
        var size = item.GetProperty("size").GetInt64();
        if (size <= 0) throw new InvalidDataException("Das Release-Paket ist leer.");
        var info = new ReleaseInfo(version, Regex.Match(name, pattern).Groups["build"].Value,
            downloadUrl, digest[7..].ToLowerInvariant(), size, releaseUrl, published);
        ValidateRelease(info);
        return info;
    }

    internal static void ValidateRelease(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!Regex.IsMatch(release.Version ?? "", @"\A(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\z") ||
            !Regex.IsMatch(release.GameBuild ?? "", @"\A[0-9]+\z") ||
            !Regex.IsMatch(release.Sha256 ?? "", @"\A[a-fA-F0-9]{64}\z") || release.Size <= 0)
            throw new InvalidDataException("Ungültige Release-Metadaten.");
        var tag = release.Version;
        if (release.ReleaseUrl != $"{Repository}/releases/tag/{tag}")
            throw new InvalidDataException("Ungültige offizielle Release-Adresse.");
        var downloadPattern = @"\A" + Regex.Escape($"{Repository}/releases/download/{tag}/SPT-{release.Version}-{release.GameBuild}-") + @"[a-f0-9]{7,40}\.7z\z";
        if (!Regex.IsMatch(release.DownloadUrl ?? "", downloadPattern, RegexOptions.CultureInvariant))
            throw new InvalidDataException("Ungültige offizielle Paketadresse.");
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(value.GetString()) ? value.GetString()! :
            throw new InvalidDataException($"Release-Metadatum fehlt: {name}.");
}
