using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SptLinuxInstaller.App.Dialogs;
using Microsoft.Win32;
using SptLinuxInstaller.Core;

namespace SptLinuxInstaller.App;

public partial class MainWindow : Window
{
    private readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SPT Linux Installer");
    private readonly ReleaseService _releases = new();
    private readonly InstallerService _installer;
    private readonly bool _preview;
    private ReleaseInfo? _release;
    private ServerStatus? _status;
    private bool _busy;
    private string _address = "";

    public MainWindow(bool preview = false)
    {
        _preview = preview;
        _installer = new InstallerService(Path.Combine(_dataDirectory, "known-hosts.json"));
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version;
        VersionFooter.Text = $"SPT Linux Installer {version?.Major}.{version?.Minor}.{version?.Build}  ·  Community-Projekt von C1yHAX  ·  Kein offizieller SPT-Installer";
        if (!preview) Height = Math.Min(Height, SystemParameters.WorkArea.Height - 30);
        if (!preview) LoadPreferences();
        else
        {
            HostTextBox.Text = "server.example.com";
            AdvertisedHostTextBox.Text = "server.example.com";
            _release = new ReleaseInfo("4.1.6", "40743", "", "", 154768026, "", DateTimeOffset.MinValue);
            LatestVersionText.Text = "4.1.6";
            ReleaseDetailText.Text = "EFT-Build 40743 · Offizielles Release · SHA-256 geprüft";
            LogBox.Text = "Bereit für deine erste Installation.\nKeine Mods. Kein Fika. Keine Spieldateien im Download des Assistenten.";
        }
        foreach (var field in new[] { HostTextBox, SshPortTextBox, UserTextBox, InstallDirectoryTextBox,
                     ServiceNameTextBox, GamePortTextBox, AdvertisedHostTextBox })
            field.TextChanged += (_, _) => InvalidateStatus();
        LocalOnlyCheck.Checked += (_, _) => InvalidateStatus();
        LocalOnlyCheck.Unchecked += (_, _) => InvalidateStatus();
        RefreshEnabledState();
        Loaded += async (_, _) => { if (!_preview) await RefreshReleaseAsync(); };
    }

    private ConnectionOptions Connection()
    {
        if (!int.TryParse(SshPortTextBox.Text, out var port)) throw new ArgumentException("Der SSH-Port muss eine Zahl sein.");
        return new(HostTextBox.Text.Trim(), port, UserTextBox.Text.Trim(),
            AuthenticationCombo.SelectedIndex == 1 ? AuthenticationMode.PrivateKey : AuthenticationMode.Password,
            CredentialPasswordBox.Password, KeyPathTextBox.Text.Trim(), KeyPassphraseBox.Password);
    }

    private DeploymentOptions Options()
    {
        if (!int.TryParse(GamePortTextBox.Text, out var port)) throw new ArgumentException("Der Server-Port muss eine Zahl sein.");
        var advertised = AdvertisedHostTextBox.Text.Trim();
        if (string.IsNullOrEmpty(advertised)) advertised = HostTextBox.Text.Trim();
        return new(InstallDirectoryTextBox.Text.Trim(), ServiceNameTextBox.Text.Trim(), port,
            advertised, LocalOnlyCheck.IsChecked == true ? "127.0.0.1" : "0.0.0.0");
    }

    private async Task RefreshReleaseAsync()
    {
        if (_busy) return;
        SetBusy(true, "Offizielle Veröffentlichung wird geprüft …");
        try { ApplyRelease(await _releases.GetLatestAsync()); Log("Aktuelle stabile Veröffentlichung gefunden: SPT " + _release!.Version); StatusText.Text = "Veröffentlichung geprüft. Gib deine Serverdaten ein oder prüfe die Verbindung."; }
        catch (Exception error) { _release = null; LatestVersionText.Text = "Nicht erreichbar"; ReleaseDetailText.Text = "Bitte erneut prüfen. Es wird keine veraltete Version stillschweigend verwendet."; ShowError(error); }
        finally { SetBusy(false); }
    }

    private void ApplyRelease(ReleaseInfo release)
    {
        _release = release;
        LatestVersionText.Text = release.Version;
        ReleaseDetailText.Text = $"EFT-Build {release.GameBuild} · {release.Size / 1024d / 1024d:F0} MB · {release.PublishedAt.LocalDateTime:dd.MM.yyyy}";
    }

    private async Task ExecuteAsync(string action)
    {
        if (_busy) return;
        SetBusy(true, action == "install" ? "Installation wird vorbereitet …" : "Server wird abgefragt …");
        try
        {
            var connection = Connection();
            var options = Options();
            if (action == "install")
            {
                ApplyRelease(await _releases.GetLatestAsync());
                var verb = _status?.Installed == true ? "Aktualisieren" : "Installieren";
                var message = $"SPT {_release!.Version} auf {connection.Host} {verb.ToLowerInvariant()}?\n\n"
                    + $"Ordner: {options.InstallDirectory}\nDienst: {options.ServiceName}\nSpieladresse: {options.AdvertisedHost}:{options.Port}\n\n"
                    + "Es wird nur der reine SPT-Server installiert. Ein vorhandener, von diesem Assistenten verwalteter Server wird gesichert und neu gestartet. Beende vorher alle Raids. Dein Spielclient muss zur ausgewählten SPT-Version passen.";
                if (_status is { Installed: false, ExistingServers.Count: > 0 })
                    message += "\n\nAuf diesem Host wurde bereits ein anderer SPT-Server gefunden. Diese Aktion würde eine separate Installation im oben genannten Zielordner anlegen. Sie übernimmt den vorhandenen Server nicht.";
                if (DialogWindow.Show(this, message, verb + " bestätigen", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            }
            else if (action is "stop" or "restart" or "rollback")
            {
                var message = action == "rollback"
                    ? "Auf die vorherige gesicherte Version zurücksetzen?\n\nDabei werden auch die zu dieser Sicherung gehörenden Spielstände wiederhergestellt. Fortschritt seit der Sicherung wird vorher gesichert, gehört danach aber nicht zum aktiven Stand. Beende zuerst alle Raids."
                    : "Der Server wird " + (action == "stop" ? "gestoppt" : "neu gestartet") + ". Beende zuerst alle Raids.";
                message += $"\n\nServer: {connection.Host}:{connection.Port}\nDienst: {options.ServiceName}\nOrdner: {options.InstallDirectory}";
                if (DialogWindow.Show(this, message, "Serveraktion bestätigen", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            }
            var progress = new Progress<OperationProgress>(p => { Log(p.Message, p.Level); StatusText.Text = p.Message; });
            var status = await _installer.ExecuteAsync(connection, options, action, action == "install" ? _release : null, TrustHost, progress);
            ApplyStatus(status);
            try { SavePreferences(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Log("Der Servervorgang war erfolgreich. Die lokalen Optionen konnten aber nicht gespeichert werden.", "warning"); }
            Log(action == "install" ? "Installation abgeschlossen. Die angezeigte Adresse kann jetzt im SPT-Launcher verwendet werden." : "Vorgang abgeschlossen.");
            StatusText.Text = status.Installed
                ? (status.Active ? "SPT ist gestartet. Verwende die Adresse unten im SPT-Launcher." : "Die Installation ist vorhanden. Der Dienst ist gestoppt.")
                : status.ExistingServers.Count > 0
                    ? "Vorhandener SPT-Server erkannt. Pfad und Dienst stehen im Bestandsbereich."
                    : $"Verbindung erfolgreich. Im gewählten Ziel {options.InstallDirectory} ist keine verwaltete Installation vorhanden.";
        }
        catch (Exception error) { InvalidateStatus(); ShowError(error); }
        finally { SetBusy(false); }
    }

    private bool TrustHost(HostKeyPrompt key) => Dispatcher.Invoke(() => DialogWindow.Show(this,
        $"Erste Verbindung zu {key.Host}:{key.Port}\n\nAlgorithmus: {key.Algorithm}\nFingerabdruck:\n{key.Fingerprint}\n\nVergleiche den Fingerabdruck mit deinem Serveranbieter oder deiner Serverkonsole. Diesen Schlüssel für zukünftige Verbindungen speichern?",
        "SSH-Server identifizieren", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);

    private void ApplyStatus(ServerStatus status)
    {
        _status = status;
        InstalledText.Text = status.Installed ? "SPT " + status.Version : "Noch nicht installiert";
        ServiceText.Text = status.Installed ? (status.Active ? "Läuft" : "Gestoppt") : "—";
        ExistingServerPanel.Visibility = status.ExistingServers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExistingServerText.Text = string.Join("\n\n", status.ExistingServers.Select(server =>
            $"{server.ServiceName} · {(server.Active ? "Läuft" : "Gestoppt")}"
            + (string.IsNullOrWhiteSpace(server.Version) ? "" : $" · SPT {server.Version}")
            + $"\n{server.InstallDirectory}"
            + (string.IsNullOrWhiteSpace(server.Address) ? "" : $"\n{server.Address}")));
        if (!status.Installed)
        {
            InstalledText.Text = status.ExistingServers.Count > 0 ? "Vorhandener Server erkannt" : "Im Zielpfad nicht eingerichtet";
            if (status.ExistingServers.Count == 1)
                ServiceText.Text = status.ExistingServers[0].Active ? "Läuft (Bestand)" : "Gestoppt (Bestand)";
            else if (status.ExistingServers.Count > 1) ServiceText.Text = $"{status.ExistingServers.Count} Dienste gefunden";
        }
        _address = status.Installed && Uri.TryCreate(status.Address, UriKind.Absolute, out var endpoint)
            ? endpoint.Authority : status.Installed ? status.Address : "";
        if (!status.Installed && status.ExistingServers.Count == 1 &&
            Uri.TryCreate(status.ExistingServers[0].Address, UriKind.Absolute, out var existingEndpoint))
            _address = existingEndpoint.Authority;
        AddressText.Text = string.IsNullOrEmpty(_address) ? "Die Verbindungsadresse erscheint nach der Installation." : "SPT-Launcher: " + _address;
        if (!string.IsNullOrWhiteSpace(status.Detail)) Log(status.Detail);
        if (!string.IsNullOrWhiteSpace(status.ExistingDiscoveryWarning)) Log(status.ExistingDiscoveryWarning, "warning");
    }

    private void InvalidateStatus()
    {
        _status = null;
        _address = "";
        InstalledText.Text = "Bitte Verbindung prüfen";
        ServiceText.Text = "—";
        ExistingServerPanel.Visibility = Visibility.Collapsed;
        ExistingServerText.Text = "";
        AddressText.Text = "Die Verbindungsadresse erscheint nach der Prüfung.";
        RefreshEnabledState();
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        if (text != null) StatusText.Text = text;
        OperationProgressBar.IsIndeterminate = busy;
        RefreshEnabledState();
    }

    private void RefreshEnabledState()
    {
        ConnectionForm.IsEnabled = !_busy;
        DeploymentForm.IsEnabled = !_busy;
        ConnectButton.IsEnabled = !_busy;
        RefreshButton.IsEnabled = !_busy;
        InstallButton.IsEnabled = !_busy && _release != null;
        InstallButton.Content = _status?.Installed == true ? "Neueste Version installieren / aktualisieren"
            : _status?.ExistingServers.Count > 0 ? "Separate Installation einrichten" : "Neueste Version installieren";
        foreach (var button in new[] { StartButton, StopButton, RestartButton, LogsButton, RollbackButton }) button.IsEnabled = !_busy && _status?.Installed == true;
        CopyAddressButton.IsEnabled = !string.IsNullOrWhiteSpace(_address);
    }

    private void Log(string message, string level = "info")
    {
        if (LogBox.Text.StartsWith("Aktivitätsprotokoll")) LogBox.Clear();
        if (LogBox.Text.Length > 150000) LogBox.Text = LogBox.Text[^90000..];
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {level.ToUpperInvariant()}: {message}\n");
        LogBox.ScrollToEnd();
    }

    private void ShowError(Exception error)
    {
        var message = error.Message;
        foreach (var secret in new[] { CredentialPasswordBox.Password, KeyPassphraseBox.Password })
            if (!string.IsNullOrEmpty(secret)) message = message.Replace(secret, "[ausgeblendet]", StringComparison.Ordinal);
        Log(message, "error");
        StatusText.Text = "Vorgang nicht abgeschlossen. Details stehen im Protokoll.";
        DialogWindow.Show(this, message, "Vorgang nicht abgeschlossen", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void LoadPreferences()
    {
        try
        {
            var path = Path.Combine(_dataDirectory, "preferences.json");
            if (!File.Exists(path)) return;
            var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path));
            if (settings == null) return;
            HostTextBox.Text = settings.Host;
            SshPortTextBox.Text = settings.SshPort.ToString();
            UserTextBox.Text = settings.Username;
            AuthenticationCombo.SelectedIndex = settings.UseKey ? 1 : 0;
            KeyPathTextBox.Text = settings.KeyPath;
            InstallDirectoryTextBox.Text = settings.InstallDirectory;
            ServiceNameTextBox.Text = settings.ServiceName;
            GamePortTextBox.Text = settings.Port.ToString();
            AdvertisedHostTextBox.Text = settings.AdvertisedHost;
            LocalOnlyCheck.IsChecked = settings.LocalOnly;
        }
        catch { Log("Gespeicherte Optionen konnten nicht gelesen werden. Bitte die Felder neu ausfüllen.", "warning"); }
    }

    private void SavePreferences()
    {
        var path = Path.Combine(_dataDirectory, "preferences.json");
        if (RememberCheck.IsChecked != true) { if (File.Exists(path)) File.Delete(path); return; }
        var connection = Connection();
        var options = Options();
        var preferences = new Preferences(connection.Host, connection.Port, connection.Username,
            connection.AuthenticationMode == AuthenticationMode.PrivateKey, connection.PrivateKeyPath,
            options.InstallDirectory, options.ServiceName, options.Port, options.AdvertisedHost, options.BindAddress == "127.0.0.1");
        Directory.CreateDirectory(_dataDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Preferences(string Host, int SshPort, string Username, bool UseKey, string KeyPath,
        string InstallDirectory, string ServiceName, int Port, string AdvertisedHost, bool LocalOnly);

    private void Authentication_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordPanel == null || KeyPanel == null) return;
        PasswordPanel.Visibility = AuthenticationCombo.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = AuthenticationCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Host_LostFocus(object sender, RoutedEventArgs e) { if (string.IsNullOrWhiteSpace(AdvertisedHostTextBox.Text)) AdvertisedHostTextBox.Text = HostTextBox.Text.Trim(); }
    private void BrowseKey_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Title = "Privaten SSH-Schlüssel auswählen", CheckFileExists = true, Filter = "Alle Dateien|*.*" }; if (dialog.ShowDialog(this) == true) KeyPathTextBox.Text = dialog.FileName; }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshReleaseAsync();
    private async void Connect_Click(object sender, RoutedEventArgs e) => await ExecuteAsync("status");
    private async void Install_Click(object sender, RoutedEventArgs e) => await ExecuteAsync("install");
    private async void Start_Click(object sender, RoutedEventArgs e) => await ExecuteAsync("start");
    private async void Stop_Click(object sender, RoutedEventArgs e) => await ExecuteAsync("stop");
    private async void Restart_Click(object sender, RoutedEventArgs e) => await ExecuteAsync("restart");
    private async void Logs_Click(object sender, RoutedEventArgs e) => await ExecuteAsync("logs");
    private async void Rollback_Click(object sender, RoutedEventArgs e) => await ExecuteAsync("rollback");
    private void GitHub_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/C1yHAX/SPT-Linux-Installer") { UseShellExecute = true });
    private void CopyAddress_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(_address)) { Clipboard.SetText(_address); StatusText.Text = "Adresse kopiert. Im SPT-Launcher ohne https:// eintragen."; } }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        DialogWindow.Show(this, "Bitte warte, bis der laufende Vorgang abgeschlossen ist. So kann eine Installation oder Wiederherstellung sicher beendet werden.", "Vorgang läuft", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowFrame == null) return;
        var maximized = WindowState == WindowState.Maximized;
        WindowFrame.Margin = maximized ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left) : new Thickness(0);
        MaximizeIcon.Data = Geometry.Parse(maximized ? "M 3 0 L 11 0 L 11 8 M 0 3 L 8 3 L 8 11 L 0 11 Z" : "M 0 0 L 10 0 L 10 10 L 0 10 Z");
        MaximizeButton.ToolTip = maximized ? "Wiederherstellen" : "Maximieren";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
    }
}
