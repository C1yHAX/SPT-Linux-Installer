#!/usr/bin/env python3
from __future__ import annotations

import argparse
import contextlib
import hashlib
import ipaddress
import json
import os
import pathlib
import platform
import re
import shutil
import signal
import socket
import ssl
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

OUTPUT_AVAILABLE = True

MAGIC = "spt-linux-installer/v1"
OWNER = ".spt-installer-owner.json"
STATE = "state.json"
ENTRY = "SPT.Server.Linux.dll"
FRAMEWORK_CONFIG = "SPT.Server.Linux.runtimeconfig.json"
SUPPORTED = {"ubuntu": {"22.04", "24.04", "26.04"}, "debian": {"12", "13"}}
HTTPS_HOSTS = {"github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com",
               "github-releases.githubusercontent.com", "patcher.sp-tushonka.com", "mirror.sp-tushonka.com",
               "builds.dotnet.microsoft.com", "dotnetcli.blob.core.windows.net", "download.visualstudio.microsoft.com"}
EXCLUDED = {"spt.launcher.exe", "spt.launcher.linux", "spt.server.exe", "spt.server.dll",
            "spt.server.deps.json", "spt.server.runtimeconfig.json", "argon2_sharp_core.dll"}


class InstallerError(Exception):
    pass


def emit(value):
    global OUTPUT_AVAILABLE
    if OUTPUT_AVAILABLE:
        try:
            print(json.dumps(value, ensure_ascii=False), flush=True)
        except (BrokenPipeError, OSError):
            OUTPUT_AVAILABLE = False
            sys.stdout = open(os.devnull, "w")


def log(message, level="info"):
    emit({"event": "log", "level": level, "message": str(message)})


def run(args, check=True, timeout=120, env=None):
    result = subprocess.run([str(x) for x in args], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            text=True, timeout=timeout, env=env)
    if check and result.returncode:
        raise InstallerError(f"Befehl fehlgeschlagen: {pathlib.Path(str(args[0])).name} (Exit {result.returncode}).")
    return result


def atomic_json(path, value):
    temporary = path.with_name(path.name + ".tmp-" + uuid.uuid4().hex)
    with temporary.open("x", encoding="utf-8") as stream:
        os.chmod(temporary, 0o600)
        json.dump(value, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def read_json(path):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        raise InstallerError(f"JSON-Datei nicht lesbar: {path.name}.") from error


def validate_https(url):
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != "https" or parsed.hostname not in HTTPS_HOSTS or parsed.username or parsed.password:
        raise InstallerError("Downloadadresse ist nicht auf der HTTPS-Zulassungsliste.")
    try:
        if parsed.port not in (None, 443):
            raise InstallerError("Downloadadresse verwendet einen unzulässigen Port.")
    except ValueError as error:
        raise InstallerError("Ungültige Downloadadresse.") from error
    return url


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        validate_https(new_url)
        return super().redirect_request(request, fp, code, message, headers, new_url)


def download(url, destination, expected_hash=None, algorithm="sha256", limit=8 * 1024**3):
    validate_https(url)
    opener = urllib.request.build_opener(SafeRedirect(), urllib.request.ProxyHandler({}))
    digest = hashlib.new(algorithm)
    total = 0
    try:
        with opener.open(urllib.request.Request(url, headers={"User-Agent": "SPT-Linux-Installer/1"}), timeout=60) as response:
            length = int(response.headers.get("Content-Length", "0"))
            if length > limit:
                raise InstallerError("Download überschreitet das erlaubte Größenlimit.")
            if length and shutil.disk_usage(destination.parent).free < length + 128 * 1024**2:
                raise InstallerError("Nicht genügend freier Speicher für den Download.")
            with destination.open("xb") as stream:
                while True:
                    data = response.read(1024**2)
                    if not data:
                        break
                    total += len(data)
                    if total > limit:
                        raise InstallerError("Download überschreitet das erlaubte Größenlimit.")
                    stream.write(data)
                    digest.update(data)
        if expected_hash and digest.hexdigest().lower() != expected_hash.lower():
            raise InstallerError(f"{algorithm.upper()}-Prüfsumme stimmt nicht; Download verworfen.")
    except (OSError, urllib.error.URLError, ValueError) as error:
        destination.unlink(missing_ok=True)
        raise InstallerError("HTTPS-Download fehlgeschlagen; keine Installation aus ungeprüften Dateien.") from error
    except BaseException:
        destination.unlink(missing_ok=True)
        raise
    return total


def validate_request(value, action):
    if not isinstance(value, dict):
        raise InstallerError("Die Anfrage muss ein JSON-Objekt sein.")
    result = dict(value)
    result.setdefault("installDirectory", "/opt/spt-server")
    result.setdefault("serviceName", "spt-server")
    result.setdefault("port", 6969)
    result.setdefault("bindAddress", "0.0.0.0")
    if not isinstance(result["installDirectory"], str) or not re.fullmatch(r"/opt/[a-z0-9][a-z0-9-]{0,47}", result["installDirectory"]):
        raise InstallerError("Installationspfad muss /opt/<name> sein; nur Kleinbuchstaben, Ziffern und Bindestriche.")
    if not isinstance(result["serviceName"], str) or not re.fullmatch(r"[a-z][a-z0-9-]{1,30}", result["serviceName"]):
        raise InstallerError("Dienstname: 2–31 Kleinbuchstaben, Ziffern oder Bindestriche; ohne .service.")
    if type(result["port"]) is not int or not 1024 <= result["port"] <= 65535:
        raise InstallerError("Port muss eine ganze Zahl zwischen 1024 und 65535 sein.")
    if result["bindAddress"] not in ("0.0.0.0", "127.0.0.1"):
        raise InstallerError("Bind-Adresse muss 0.0.0.0 oder 127.0.0.1 sein.")
    host = result.get("advertisedHost", "127.0.0.1")
    if not isinstance(host, str) or len(host) > 253:
        raise InstallerError("Ungültiger Servername.")
    try:
        ipaddress.IPv4Address(host)
        valid_host = True
    except ValueError:
        valid_host = bool(re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*", host))
    if not valid_host or host == "0.0.0.0":
        raise InstallerError("Serveradresse muss eine IPv4-Adresse oder ein DNS-Name ohne Schema und Port sein.")
    result["advertisedHost"] = host
    if action == "install":
        version, url, digest = (result.get(key, "") for key in ("releaseVersion", "releaseUrl", "releaseSha256"))
        if not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+", version):
            raise InstallerError("Ungültige Release-Version.")
        if not isinstance(url, str):
            raise InstallerError("Release-Downloadadresse fehlt.")
        validate_https(url)
        parsed = urllib.parse.urlsplit(url)
        pattern = r"/SP-Tushonka/build/releases/download/" + re.escape(version) + r"/SPT-" + re.escape(version) + r"-\d+-[a-fA-F0-9]{7,40}\.7z"
        if parsed.hostname != "github.com" or not re.fullmatch(pattern, parsed.path) or parsed.query or parsed.fragment:
            raise InstallerError("Es sind nur offizielle SP-Tushonka/build-Release-Archive erlaubt.")
        if not isinstance(digest, str) or not re.fullmatch(r"[a-fA-F0-9]{64}", digest):
            raise InstallerError("Die offizielle SHA-256-Prüfsumme fehlt oder ist ungültig.")
    return result


def safe_archive_path(value):
    if not value or "\x00" in value or "\n" in value or "\r" in value:
        raise InstallerError("Archiv enthält einen ungültigen Pfad.")
    value = value.replace("\\", "/")
    path = pathlib.PurePosixPath(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in value.split("/")) or ":" in value:
        raise InstallerError("Archiv enthält einen unsicheren Pfad.")
    return path.as_posix()


def parse_7z_listing(text):
    records, current = [], {}
    for line in text.splitlines() + [""]:
        if not line.strip():
            if current:
                records.append(current)
                current = {}
        elif " = " in line:
            key, value = line.split(" = ", 1)
            current[key] = value
    seen, valid = set(), []
    for record in records:
        if "Path" not in record:
            continue
        name = safe_archive_path(record["Path"])
        if name in seen:
            raise InstallerError("Archiv enthält doppelte Pfade.")
        seen.add(name)
        attrs = record.get("Attributes", "")
        if any(record.get(key) for key in ("Symbolic Link", "Hard Link")) or re.search(r"(?:^|\s)[lbcps][rwxStTs-]{9}", attrs):
            raise InstallerError("Archivlinks sind nicht erlaubt.")
        if record.get("Encrypted") == "+":
            raise InstallerError("Verschlüsselte Archive werden nicht unterstützt.")
        folder = record.get("Folder") == "+" or attrs.startswith("D")
        size = int(record.get("Size", "0"))
        if size < 0:
            raise InstallerError("Ungültige Archivgröße.")
        valid.append({"path": name, "folder": folder, "size": size})
    if not valid:
        raise InstallerError("Archiv ist leer oder sein Inhaltsverzeichnis unlesbar.")
    files = {row["path"] for row in valid if not row["folder"]}
    for row in valid:
        if any(parent.as_posix() in files for parent in pathlib.PurePosixPath(row["path"]).parents):
            raise InstallerError("Archiv enthält widersprüchliche Datei- und Verzeichnispfade.")
    return valid


def server_files(records):
    selected = []
    for row in records:
        parts = pathlib.PurePosixPath(row["path"]).parts
        if row["folder"] or len(parts) < 2 or parts[0] != "SPT_Runtime":
            continue
        lower = [part.lower() for part in parts[1:]]
        if lower[0] in ("user", "bepinex", "mods", "launcher") or lower[-1] in EXCLUDED:
            continue
        if lower[:2] == ["spt_data", "launcher"] or any(part.startswith("win-") for part in lower):
            continue
        if lower[-1].endswith(".exe"):
            continue
        selected.append(row)
    paths = {row["path"] for row in selected}
    for required in (ENTRY, FRAMEWORK_CONFIG, "SPT_Data/configs/http.json"):
        if "SPT_Runtime/" + required not in paths:
            raise InstallerError(f"Linux-Serverdatei fehlt im Release: {required}.")
    return selected


def ensure_regular_tree(root):
    if root.is_symlink():
        raise InstallerError("Verzeichnislink im verwalteten Datenbestand nicht erlaubt.")
    for current, directories, files in os.walk(root, followlinks=False):
        for name in directories + files:
            path = pathlib.Path(current, name)
            mode = path.lstat().st_mode
            if path.is_symlink() or not (stat.S_ISDIR(mode) or stat.S_ISREG(mode)):
                raise InstallerError("Links oder Spezialdateien im Datenbestand verhindern einen sicheren Vorgang.")


def safe_extract_tar(archive, destination):
    with tarfile.open(archive, "r:gz") as source:
        members = []
        seen = set()
        for member in source.getmembers():
            name = member.name.rstrip("/")
            if name in ("", ".") and member.isdir():
                continue
            if name.startswith("./"):
                name = name[2:]
            name = safe_archive_path(name)
            if name in seen or not (member.isfile() or member.isdir()):
                raise InstallerError("Runtime-Archiv enthält Links, Spezialdateien oder doppelte Pfade.")
            seen.add(name)
            members.append((member, name))
        files = {name for member, name in members if member.isfile()}
        if any(parent.as_posix() in files for _, name in members for parent in pathlib.PurePosixPath(name).parents):
            raise InstallerError("Runtime-Archiv enthält widersprüchliche Pfade.")
        if sum(member.size for member, _ in members) > 2 * 1024**3:
            raise InstallerError("Runtime-Archiv ist unerwartet groß.")
        ensure_regular_tree(destination)
        for member, name in members:
            target = destination / name
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with source.extractfile(member) as src, target.open("xb") as dst:
                    shutil.copyfileobj(src, dst)
                target.chmod((member.mode & 0o755) | 0o400)


def require_owner(root, service_name):
    if root.is_symlink() or root.parent.is_symlink():
        raise InstallerError("Installationspfad darf kein symbolischer Link sein.")
    marker = root / OWNER
    if not root.exists():
        return None
    if not root.is_dir() or not marker.is_file() or marker.is_symlink():
        raise InstallerError("Vorhandener Pfad gehört nicht diesem Installer; Übernahme verweigert.")
    data = read_json(marker)
    if data.get("magic") != MAGIC or data.get("serviceName") != service_name or data.get("root") != str(root):
        raise InstallerError("Besitzmarker passt nicht zu dieser Installation.")
    if not re.fullmatch(r"[a-f0-9]{32}", str(data.get("id", ""))):
        raise InstallerError("Besitzmarker ist beschädigt.")
    if hasattr(os, "geteuid"):
        if any(path.stat().st_uid != 0 or path.stat().st_mode & 0o022 for path in (root, marker)):
            raise InstallerError("Installationswurzel und Besitzmarker müssen root gehören und vor fremden Schreibzugriffen geschützt sein.")
    return data


def remove_inside(root, path):
    if path == root or root.resolve() not in path.resolve().parents:
        raise InstallerError("Unsicheres Löschziel verweigert.")
    if path.is_symlink() or path.is_file():
        path.unlink()
    elif path.exists():
        shutil.rmtree(path)


def check_no_mods(root):
    for relative in ("user/mods", "user/patchers"):
        path = root / relative
        if path.exists() and any(item.name not in (".gitkeep", ".gitignore") for item in path.iterdir()):
            raise InstallerError("Updates und Rollbacks mit nachträglich installierten Mods/Patchern sind gesperrt. Zuerst getrennt sichern und Kompatibilität manuell klären.")


def configure_http(release, request):
    path = release / "SPT_Data/configs/http.json"
    value = read_json(path)
    value.update(ip=request["bindAddress"], backendIp=request["advertisedHost"],
                 port=request["port"], backendPort=request["port"])
    auth = value.setdefault("webAuthenticationConfig", {})
    auth.update(enabled=True, enableDefaultUser=False, requireCredentialsOnLocalhost=True,
                allowDefaultUserFromAnyIp=False)
    atomic_json(path, value)
    path.chmod(0o640)


@contextlib.contextmanager
def config_directory(release):
    descriptors = []
    try:
        flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
        descriptors.append(os.open(release, flags))
        for name in ("SPT_Data", "configs"):
            descriptors.append(os.open(name, flags, dir_fd=descriptors[-1]))
        yield descriptors[-1]
    except OSError as error:
        raise InstallerError("HTTP-Konfigurationspfad ist nicht sicher als regulärer Pfad erreichbar.") from error
    finally:
        for descriptor in reversed(descriptors):
            os.close(descriptor)


def read_http_config(release):
    with config_directory(release) as directory:
        descriptor = os.open("http.json", os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=directory)
        with os.fdopen(descriptor, "rb") as stream:
            info = os.fstat(stream.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > 16 * 1024**2:
                raise InstallerError("HTTP-Konfiguration muss eine reguläre, nicht verlinkte Datei sein.")
            return stream.read()


def write_http_config(release, data):
    with config_directory(release) as directory:
        info = os.stat("http.json", dir_fd=directory, follow_symlinks=False)
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
            raise InstallerError("Unsicheres HTTP-Konfigurationsziel; Wiederherstellung verweigert.")
        temporary = ".http-restore-" + uuid.uuid4().hex
        flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC
        descriptor = os.open(temporary, flags, 0o640, dir_fd=directory)
        try:
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, "http.json", src_dir_fd=directory, dst_dir_fd=directory)
            os.fsync(directory)
        finally:
            with contextlib.suppress(FileNotFoundError):
                os.unlink(temporary, dir_fd=directory)


def chown_tree_no_links(path, uid, gid):
    flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK
    def visit(descriptor):
        info = os.fstat(descriptor)
        if not (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)):
            raise InstallerError("Spezialdatei im verwalteten Bestand verweigert.")
        if stat.S_ISREG(info.st_mode) and info.st_nlink != 1:
            raise InstallerError("Hardlink im verwalteten Bestand verweigert.")
        os.fchown(descriptor, uid, gid)
        if stat.S_ISDIR(info.st_mode):
            for name in os.listdir(descriptor):
                item = os.stat(name, dir_fd=descriptor, follow_symlinks=False)
                if stat.S_ISLNK(item.st_mode):
                    continue
                child = os.open(name, flags, dir_fd=descriptor)
                try:
                    visit(child)
                finally:
                    os.close(child)
    descriptor = os.open(path, flags | os.O_DIRECTORY)
    try:
        visit(descriptor)
    finally:
        os.close(descriptor)


def framework_channel(release):
    options = read_json(release / FRAMEWORK_CONFIG).get("runtimeOptions", {})
    frameworks = options.get("frameworks", [options.get("framework", {})])
    found = {row.get("name"): row.get("version", "") for row in frameworks}
    if not {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"}.issubset(found):
        raise InstallerError("Unerwartete Frameworkanforderungen im Linux-Release.")
    channels = {".".join(found[name].split(".")[:2]) for name in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App")}
    if len(channels) != 1 or not re.fullmatch(r"\d+\.\d+", next(iter(channels))):
        raise InstallerError("Frameworkversionen sind nicht gemeinsam auflösbar.")
    return channels.pop()


def runtime_asset(metadata, channel):
    candidates = []
    for release in metadata.get("releases", []):
        runtime = release.get("aspnetcore-runtime", {})
        version = runtime.get("version", "")
        if not re.fullmatch(re.escape(channel) + r"\.\d+", version):
            continue
        for asset in runtime.get("files", []):
            if asset.get("rid") == "linux-x64" and asset.get("name") == "aspnetcore-runtime-linux-x64.tar.gz":
                if not re.fullmatch(r"[a-fA-F0-9]{128}", asset.get("hash", "")):
                    continue
                validate_https(asset["url"])
                candidates.append((tuple(map(int, version.split("."))), version, asset))
    if not candidates:
        raise InstallerError("Keine passende stabile ASP.NET-Core-Runtime mit SHA-512 gefunden.")
    _, version, asset = max(candidates, key=lambda item: item[0])
    if channel == "10.0" and tuple(map(int, version.split("."))) < (10, 0, 9):
        raise InstallerError("SPT benötigt einen aktuellen .NET-10-Patch (mindestens 10.0.9).")
    return version, asset


def health_check(root, expected_version, port, timeout=150):
    deadline = time.monotonic() + timeout
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                        urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))
    while time.monotonic() < deadline:
        try:
            responses = []
            for route in ("ping", "version"):
                request = urllib.request.Request(f"https://127.0.0.1:{port}/launcher/v2/{route}",
                                                 headers={"responsecompressed": "0"})
                with opener.open(request, timeout=4) as response:
                    data = response.read(65537)
                    if len(data) > 65536:
                        raise ValueError()
                    responses.append(json.loads(data).get("Response"))
            if responses == ["Pong!", expected_version]:
                return True
        except (OSError, ValueError, urllib.error.URLError):
            pass
        time.sleep(2)
    return False


class Service:
    def __init__(self, name, root, owner_id, runner=run, unit_directory=pathlib.Path("/etc/systemd/system")):
        self.name, self.root, self.owner_id, self.runner = name, root, owner_id, runner
        self.unit = unit_directory / (name + ".service")

    def state(self):
        result = self.runner(["systemctl", "show", self.name, "--property=ActiveState", "--property=MainPID", "--property=UnitFileState"], check=False)
        return dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)

    def verify(self):
        if self.unit.is_symlink() or not self.unit.exists() or f"# {MAGIC} {self.owner_id}\n" not in self.unit.read_text():
            raise InstallerError("Systemd-Dienst gehört nicht nachweislich zu dieser Installation.")

    def verify_or_absent(self, installed):
        if self.unit.exists() or self.unit.is_symlink():
            self.verify()
            return
        load = self.runner(["systemctl", "show", self.name, "--property=LoadState", "--value"], check=False)
        if installed or load.stdout.strip() not in ("", "not-found"):
            raise InstallerError("Eigener Dienst fehlt oder der Name gehört zu einem fremden Dienst.")

    def create(self):
        if self.unit.exists() or self.unit.is_symlink():
            self.verify()
        else:
            unit = f"""# {MAGIC} {self.owner_id}
[Unit]
Description=SPT dedicated backend
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
User={self.name}
Group={self.name}
WorkingDirectory={self.root}/current
Environment=DOTNET_ROOT={self.root}/current/.runtime
Environment=DOTNET_NOLOGO=1
ExecStart={self.root}/current/.runtime/dotnet {self.root}/current/{ENTRY}
Restart=on-failure
RestartSec=5
TimeoutStopSec=60
KillSignal=SIGINT
UMask=0027
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths={self.root}
[Install]
WantedBy=multi-user.target
"""
            with self.unit.open("x") as stream:
                stream.write(unit)
            self.unit.chmod(0o644)
        self.runner(["systemctl", "daemon-reload"])

    def action(self, action):
        self.verify()
        self.runner(["systemctl", action, self.name], timeout=90)


class Manager:
    def __init__(self, request):
        self.request = request
        self.root = pathlib.Path(request["installDirectory"])
        self.owner = require_owner(self.root, request["serviceName"])
        self.service = Service(request["serviceName"], self.root, self.owner["id"]) if self.owner else None
        self.health = health_check

    def state(self):
        return read_json(self.root / STATE) if (self.root / STATE).exists() else {}

    def status(self):
        state = self.state() if self.owner else {}
        current = state.get("current", {})
        active = bool(self.service and self.service.state().get("ActiveState") == "active")
        existing, warning = discover_existing_servers(self.request["serviceName"] if current else None)
        return {"installed": bool(current), "version": current.get("version"), "active": active,
                "serviceName": self.request["serviceName"], "installDirectory": str(self.root),
                "address": current.get("address", f"https://{self.request['advertisedHost']}:{self.request['port']}"),
                "rollbackAvailable": bool(state.get("previous")), "recoveryRequired": (self.root / "pending.json").exists(),
                "existingServers": existing, "existingDiscoveryWarning": warning,
                "detail": "SPT-Backend verwaltet; Updates erfolgen ausschließlich auf Anforderung." if current else
                    f"Im gewählten Zielpfad {self.root} ist keine vom Assistenten verwaltete Installation vorhanden."}

    def release_path(self, name):
        if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,100}", name):
            raise InstallerError("Ungültiger interner Release-Bezeichner.")
        path = self.root / "releases" / name
        if path.is_symlink() or not path.is_dir():
            raise InstallerError("Gespeicherter Release fehlt oder ist kein reguläres Verzeichnis.")
        return path

    def switch(self, name):
        self.release_path(name)
        target = self.root / (".current-" + uuid.uuid4().hex)
        target.symlink_to("releases/" + name, target_is_directory=True)
        os.replace(target, self.root / "current")

    def chown(self, path):
        import pwd
        account = pwd.getpwnam(self.request["serviceName"])
        chown_tree_no_links(path, account.pw_uid, account.pw_gid)

    def snapshot(self, state):
        ensure_regular_tree(self.root / "user")
        identifier = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + uuid.uuid4().hex[:8]
        target = self.root / "backups" / identifier
        temporary = self.root / "backups" / (".tmp-" + identifier)
        temporary.mkdir(mode=0o700)
        shutil.copytree(self.root / "user", temporary / "user")
        if state.get("current"):
            (temporary / "http.json").write_bytes(read_http_config(self.release_path(state["current"]["release"])))
        atomic_json(temporary / "state.json", state)
        os.replace(temporary, target)
        return identifier

    def restore_snapshot(self, identifier):
        if not re.fullmatch(r"[A-Za-z0-9-]+", str(identifier)):
            raise InstallerError("Ungültiger Sicherungsbezeichner.")
        source = self.root / "backups" / identifier
        if source.is_symlink() or not (source / "state.json").exists():
            raise InstallerError("Passende Sicherung fehlt.")
        ensure_regular_tree(source / "user")
        state = read_json(source / "state.json")
        release = self.release_path(state["current"]["release"]) if state.get("current") else None
        if release:
            read_http_config(release)
        restored = self.root / (".user-restored-" + uuid.uuid4().hex)
        shutil.copytree(source / "user", restored)
        self.chown(restored)
        displaced = self.root / (".user-displaced-" + uuid.uuid4().hex)
        if (self.root / "user").exists():
            if (self.root / "user").is_symlink():
                raise InstallerError("Persistenter Benutzerordner ist unerwartet ein Link.")
            os.replace(self.root / "user", displaced)
        os.replace(restored, self.root / "user")
        if release and (source / "http.json").exists():
            write_http_config(release, (source / "http.json").read_bytes())
            self.chown(release)
        remove_inside(self.root, displaced)
        return state

    def recover(self):
        pending_path = self.root / "pending.json"
        if not pending_path.exists():
            return
        pending = read_json(pending_path)
        log("Unterbrochener Wechsel erkannt; gesicherten Stand wiederherstellen.", "warning")
        self.service.action("stop")
        old_state = self.restore_snapshot(pending["snapshot"])
        if old_state.get("current"):
            self.switch(old_state["current"]["release"])
        else:
            (self.root / "current").unlink(missing_ok=True)
            self.service.action("disable")
        atomic_json(self.root / STATE, old_state)
        if pending.get("wasActive") and old_state.get("current"):
            self.service.action("start")
            old = old_state["current"]
            if not self.health(self.root, old["version"], old["port"]):
                raise InstallerError("Wiederhergestellter Server antwortet nicht; Sicherung und Transaktionsnachweis bleiben erhalten.")
        pending_path.unlink()

    def activate(self, current, restore_from=None):
        check_no_mods(self.root)
        check_port(current["port"], self.service)
        old_state = self.state()
        was_active = self.service.state().get("ActiveState") == "active"
        self.service.action("stop")
        try:
            backup = self.snapshot(old_state)
            atomic_json(self.root / "pending.json", {"snapshot": backup, "wasActive": was_active})
        except BaseException:
            if was_active:
                self.service.action("start")
            raise
        try:
            if restore_from:
                self.restore_snapshot(restore_from)
            self.switch(current["release"])
            self.service.action("start")
            if not self.health(self.root, current["version"], current["port"]):
                raise InstallerError("Neuer Server wurde nicht bereit; vorheriger Stand wird wiederhergestellt.")
            self.service.action("enable")
            state = {"current": current}
            if old_state.get("current"):
                state["previous"] = {**old_state["current"], "snapshot": backup}
            atomic_json(self.root / STATE, state)
            (self.root / "pending.json").unlink()
        except BaseException:
            self.recover()
            raise

    def preflight(self):
        if platform.system() != "Linux" or platform.machine().lower() not in ("x86_64", "amd64"):
            raise InstallerError("Unterstützt wird derzeit ausschließlich Linux x86_64.")
        if os.geteuid() != 0:
            raise InstallerError("Root-Rechte erforderlich. GUI: sudo -n muss für diese Aktion erlaubt sein.")
        os_release = {}
        for line in pathlib.Path("/etc/os-release").read_text().splitlines():
            if "=" in line:
                key, value = line.split("=", 1)
                os_release[key] = value.strip('"')
        distro, version = os_release.get("ID"), os_release.get("VERSION_ID")
        if version not in SUPPORTED.get(distro, set()):
            raise InstallerError("Unterstützte Systeme: Ubuntu 22.04/24.04/26.04 und Debian 12/13.")
        if not pathlib.Path("/run/systemd/system").is_dir() or not shutil.which("systemctl"):
            raise InstallerError("Ein laufendes systemd-System ist erforderlich.")
        if self.owner:
            self.service.verify_or_absent(bool(self.state().get("current")))
            self.verify_account(allow_absent=not bool(self.state().get("current")))
            check_no_mods(self.root)
        else:
            import pwd
            try:
                pwd.getpwnam(self.request["serviceName"])
            except KeyError:
                pass
            else:
                raise InstallerError("Der gewünschte Dienstbenutzer existiert bereits; Übernahme verweigert.")
            import grp
            try:
                grp.getgrnam(self.request["serviceName"])
            except KeyError:
                pass
            else:
                raise InstallerError("Die gewünschte Dienstgruppe existiert bereits; Übernahme verweigert.")
            load = run(["systemctl", "show", self.request["serviceName"], "--property=LoadState", "--value"], check=False)
            if load.stdout.strip() not in ("", "not-found"):
                raise InstallerError("Der Dienstname ist bereits belegt.")
        check_port(self.request["port"], self.service)
        base = self.root if self.root.exists() else self.root.parent
        if shutil.disk_usage(base).free < 2 * 1024**3:
            raise InstallerError("Mindestens 2 GiB freier Speicher werden benötigt.")
        return distro, version

    def verify_account(self, allow_absent=False):
        import pwd
        try:
            account = pwd.getpwnam(self.request["serviceName"])
        except KeyError:
            if allow_absent:
                return False
            raise InstallerError("Verwalteter Dienstbenutzer fehlt.")
        expected = MAGIC + " " + self.owner["id"]
        if (account.pw_uid == 0 or account.pw_gecos != expected or account.pw_dir != str(self.root / "user")
                or account.pw_shell != "/usr/sbin/nologin"):
            raise InstallerError("Vorhandener Dienstbenutzer gehört nicht nachweislich zu dieser Installation.")
        return True

    def claim(self):
        if not self.owner:
            self.root.mkdir(mode=0o755)
            self.owner = {"magic": MAGIC, "id": uuid.uuid4().hex, "serviceName": self.request["serviceName"], "root": str(self.root)}
            atomic_json(self.root / OWNER, self.owner)
        for name in ("releases", "runtimes", "backups", "user", ".staging"):
            directory = self.root / name
            if directory.is_symlink() or (directory.exists() and not directory.is_dir()):
                raise InstallerError("Verwalteter Ordner wurde durch einen fremden Pfad ersetzt.")
            directory.mkdir(mode=0o700 if name in ("backups", ".staging") else 0o755, exist_ok=True)
        if not self.verify_account(allow_absent=True):
            import grp
            try:
                grp.getgrnam(self.request["serviceName"])
            except KeyError:
                pass
            else:
                raise InstallerError("Dienstgruppe ist bereits belegt; Übernahme verweigert.")
            run(["useradd", "--system", "--user-group", "--no-create-home", "--home-dir", str(self.root / "user"),
                 "--comment", MAGIC + " " + self.owner["id"], "--shell", "/usr/sbin/nologin", self.request["serviceName"]])
        for name in ("profiles", "mods", "patchers", "cache", "logs"):
            directory = self.root / "user" / name
            if directory.is_symlink():
                raise InstallerError("Verwalteter Datenordner darf kein Link sein.")
            directory.mkdir(exist_ok=True)
        self.chown(self.root / "user")
        self.service = Service(self.request["serviceName"], self.root, self.owner["id"])

    def prepare_runtime(self, release, staging):
        channel = framework_channel(release)
        metadata_file = staging / "dotnet-releases.json"
        download(f"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json", metadata_file, limit=16 * 1024**2)
        version, asset = runtime_asset(read_json(metadata_file), channel)
        target = self.root / "runtimes" / version
        if target.exists():
            if target.is_symlink() or not (target / ".verified-runtime.json").exists() or read_json(target / ".verified-runtime.json").get("sha512") != asset["hash"].lower():
                raise InstallerError("Vorhandene private Runtime ist nicht als geprüft markiert.")
        else:
            archive = staging / "aspnet-runtime.tar.gz"
            log(f"Private ASP.NET-Core-Runtime {version} herunterladen und SHA-512 prüfen.")
            download(asset["url"], archive, asset["hash"], "sha512", limit=512 * 1024**2)
            extracted = staging / "runtime"
            extracted.mkdir()
            safe_extract_tar(archive, extracted)
            if not (extracted / "dotnet").is_file():
                raise InstallerError("Dotnet-Programm fehlt im geprüften Runtime-Archiv.")
            output = run([extracted / "dotnet", "--list-runtimes"]).stdout
            for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
                if not re.search(r"^" + re.escape(framework) + r" " + re.escape(version) + r" ", output, re.MULTILINE):
                    raise InstallerError("Private Runtime enthält nicht beide benötigten Frameworks in der erwarteten Version.")
            atomic_json(extracted / ".verified-runtime.json", {"version": version, "sha512": asset["hash"].lower()})
            os.replace(extracted, target)
        return version

    def install(self):
        distro, version = self.preflight()
        if self.owner:
            self.recover()
        current = self.state().get("current", {}) if self.owner else {}
        if current:
            old_version = tuple(map(int, current["version"].split(".")))
            new_version = tuple(map(int, self.request["releaseVersion"].split(".")))
            if old_version[:2] != new_version[:2]:
                raise InstallerError("Updates über eine SPT-Haupt-/Nebenversion hinweg sind wegen möglicher Profilinkompatibilität gesperrt. Dafür eine getrennte Installation verwenden.")
            if new_version < old_version:
                raise InstallerError("Downgrade per Installation ist gesperrt; Rollback mit passender Profilsicherung verwenden.")
            requested = {"version": self.request["releaseVersion"], "releaseSha256": self.request["releaseSha256"].lower(),
                         "port": self.request["port"], "bindAddress": self.request["bindAddress"],
                         "address": f"https://{self.request['advertisedHost']}:{self.request['port']}"}
            if all(current.get(key) == value for key, value in requested.items()):
                log("Diese Release-Version mit identischer Prüfsumme und Konfiguration ist bereits installiert; keine Änderung.")
                return
        install_prerequisites(distro, version)
        self.claim()
        staging = self.root / ".staging" / uuid.uuid4().hex
        staging.mkdir()
        try:
            archive = staging / "spt.7z"
            log(f"Offizielles SPT {self.request['releaseVersion']} herunterladen und SHA-256 prüfen.")
            try:
                download(self.request["releaseUrl"], archive, self.request["releaseSha256"])
            except InstallerError:
                name = pathlib.PurePosixPath(urllib.parse.urlsplit(self.request["releaseUrl"]).path).name
                log("GitHub-Download nicht verfügbar; offiziellen Spiegel mit identischer SHA-256 prüfen.", "warning")
                download("https://patcher.sp-tushonka.com/releases/" + name, archive, self.request["releaseSha256"])
            seven = shutil.which("7zz") or shutil.which("7z")
            listing = run([seven, "l", "-slt", "-ba", archive], timeout=120).stdout
            selected = server_files(parse_7z_listing(listing))
            user_bytes = sum(path.stat().st_size for path in (self.root / "user").rglob("*") if path.is_file())
            required = sum(row["size"] for row in selected) + user_bytes * 2 + 1024**3
            if shutil.disk_usage(self.root).free < required:
                raise InstallerError("Nicht genügend Speicher für Release, Runtime und vollständige Profilsicherung.")
            include = staging / "server-files.txt"
            include.write_text("\n".join(row["path"] for row in selected) + "\n", encoding="utf-8")
            extracted = staging / "extracted"
            extracted.mkdir()
            run([seven, "x", "-y", "-bd", "-bb0", "-scsUTF-8", "-spd", f"-o{extracted}", archive, f"@{include}"], timeout=600)
            ensure_regular_tree(extracted)
            actual = {path.relative_to(extracted).as_posix() for path in extracted.rglob("*") if path.is_file()}
            if actual != {row["path"] for row in selected}:
                raise InstallerError("Extraktion stimmt nicht mit der geprüften Serverdateiliste überein.")
            release = extracted / "SPT_Runtime"
            runtime = self.prepare_runtime(release, staging)
            configure_http(release, self.request)
            release_name = self.request["releaseVersion"] + "-" + uuid.uuid4().hex[:10]
            destination = self.root / "releases" / release_name
            os.replace(release, destination)
            (destination / "user").symlink_to("../../user", target_is_directory=True)
            (destination / ".runtime").symlink_to("../../runtimes/" + runtime, target_is_directory=True)
            self.chown(destination)
            self.service.create()
            current = {"release": release_name, "version": self.request["releaseVersion"], "runtimeVersion": runtime,
                       "port": self.request["port"], "bindAddress": self.request["bindAddress"],
                       "address": f"https://{self.request['advertisedHost']}:{self.request['port']}",
                       "releaseSha256": self.request["releaseSha256"].lower()}
            log("Server wechseln; Profile und HTTP-Konfiguration zuvor sichern.")
            self.activate(current)
            log("SPT meldet die erwartete Version und ist bereit.")
        finally:
            remove_inside(self.root, staging)

    def rollback(self):
        if not self.owner:
            raise InstallerError("Keine verwaltete Installation vorhanden.")
        self.service.verify()
        self.recover()
        check_no_mods(self.root)
        state = self.state()
        previous = state.get("previous")
        if not previous:
            raise InstallerError("Kein vorheriger Release mit passender Profilsicherung vorhanden.")
        snapshot = previous["snapshot"]
        current = {key: value for key, value in previous.items() if key != "snapshot"}
        check_port(current["port"], self.service)
        self.activate(current, restore_from=snapshot)


def check_port(port, service=None):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        try:
            probe.bind(("0.0.0.0", port))
            return
        except OSError:
            pass
    state = service.state() if service else {}
    if service and state.get("ActiveState") == "active" and shutil.which("ss"):
        result = run(["ss", "-H", "-ltnp", f"sport = :{port}"])
        pids = re.findall(r"pid=(\d+)", result.stdout)
        if pids and all(pid == state.get("MainPID") for pid in pids):
            return
    raise InstallerError(f"Port {port} ist bereits durch einen anderen Prozess belegt; nichts wurde beendet.")


def install_prerequisites(distro, version):
    libraries = {("ubuntu", "22.04"): ("libicu70", "libssl3"), ("ubuntu", "24.04"): ("libicu74", "libssl3t64"),
                 ("ubuntu", "26.04"): ("libicu78", "libssl3t64"), ("debian", "12"): ("libicu72", "libssl3"),
                 ("debian", "13"): ("libicu76", "libssl3t64")}
    packages = ["ca-certificates", "curl", "zlib1g", "libgcc-s1", "libstdc++6", *libraries[(distro, version)]]
    if not (shutil.which("7zz") or shutil.which("7z")):
        packages.append("p7zip-full" if (distro, version) in (("ubuntu", "22.04"), ("debian", "12")) else "7zip")
    missing = []
    for package in packages:
        result = run(["dpkg-query", "-W", "-f=${Status}", package], check=False)
        if result.returncode or result.stdout.strip() != "install ok installed":
            missing.append(package)
    if missing:
        log("Fehlende Distributionspakete installieren: " + ", ".join(missing))
        env = {**os.environ, "DEBIAN_FRONTEND": "noninteractive"}
        run(["apt-get", "update"], timeout=600, env=env)
        run(["apt-get", "install", "-y", "--no-install-recommends", *missing], timeout=900, env=env)


DISCOVERY_UNIT_LIMIT = 512
DISCOVERY_RESULT_LIMIT = 64
DISCOVERY_JSON_LIMIT = 40 * 1024
DISCOVERY_UNIT_PATTERN = re.compile(r"[A-Za-z0-9_.@:\\-]{1,240}\.service")


@contextlib.contextmanager
def discovery_directory(path):
    if not isinstance(path, str) or len(path) > 4096 or any(ord(char) < 32 for char in path):
        raise InstallerError("Ungeeigneter Systemd-Arbeitsordner.")
    candidate = pathlib.Path(path)
    if not candidate.is_absolute():
        raise InstallerError("Systemd-Arbeitsordner ist nicht absolut.")
    resolved = candidate.resolve(strict=True)
    parts = resolved.parts
    if len(parts) > 65 or len(parts) < 2 or parts[1] in ("dev", "proc", "sys"):
        raise InstallerError("Ungeeigneter Systemd-Arbeitsordner.")
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
    descriptors = []
    try:
        descriptors.append(os.open(resolved.anchor, flags))
        for part in parts[1:]:
            descriptors.append(os.open(part, flags, dir_fd=descriptors[-1]))
        yield descriptors[-1]
    finally:
        for descriptor in reversed(descriptors):
            os.close(descriptor)


def discovery_file(directory, relative, limit, read=True):
    parts = safe_archive_path(relative).split("/")
    parents = []
    try:
        for part in parts[:-1]:
            directory = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=directory)
            parents.append(directory)
        descriptor = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=directory)
        with os.fdopen(descriptor, "rb") as stream:
            info = os.fstat(stream.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or not 0 < info.st_size <= limit:
                raise InstallerError("Ungeeignete oder zu große Servermetadatei.")
            if not read:
                return None
            data = stream.read(limit + 1)
            if len(data) > limit:
                raise InstallerError("Servermetadatei überschreitet das Größenlimit.")
            return json.loads(data.decode("utf-8-sig"))
    finally:
        for descriptor in reversed(parents):
            os.close(descriptor)


def discovery_address(config):
    if not isinstance(config, dict):
        return ""
    host = config.get("backendIp") or config.get("ip")
    port = config.get("backendPort") or config.get("port")
    if not isinstance(host, str) or len(host) > 253 or type(port) is not int or not 1 <= port <= 65535:
        return ""
    try:
        address = ipaddress.ip_address(host)
        if address.is_unspecified:
            return ""
        if address.version == 6:
            host = "[" + host + "]"
    except ValueError:
        if not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*", host):
            return ""
    return f"https://{host}:{port}"


def inspect_existing_server(properties):
    command = properties.get("ExecStart", "")
    executable = re.search(r"(?:^|[ {;])path=([^ ;]+)", command)
    if not executable:
        return None, False
    basename = pathlib.PurePosixPath(executable.group(1)).name
    if basename not in ("dotnet", "SPT.Server.Linux"):
        return None, False
    if basename == "dotnet" and not re.search(r"(?:^|[ /])SPT\.Server\.Linux\.dll(?=[ ;}]|$)", command):
        return None, False
    working = properties.get("WorkingDirectory", "")
    row = {"serviceName": properties["Id"].removesuffix(".service"), "installDirectory": working,
           "active": properties.get("ActiveState") == "active", "version": "", "address": ""}
    incomplete = False
    try:
        with discovery_directory(working) as directory:
            discovery_file(directory, ENTRY, 512 * 1024**2, read=False)
            runtime = discovery_file(directory, FRAMEWORK_CONFIG, 128 * 1024)
            if not isinstance(runtime, dict) or not isinstance(runtime.get("runtimeOptions"), dict):
                return None, True
            try:
                dependencies = discovery_file(directory, "SPT.Server.Linux.deps.json", 8 * 1024**2)
                libraries = dependencies.get("libraries", {}) if isinstance(dependencies, dict) else {}
                for identity in libraries if isinstance(libraries, dict) else ():
                    match = re.fullmatch(r"SPT\.Server\.Linux/(\d+\.\d+\.\d+)(?:[-+][A-Za-z0-9.+-]+)?", identity)
                    if match:
                        row["version"] = match.group(1)
                        break
                incomplete |= not bool(row["version"])
            except (OSError, ValueError, RecursionError, InstallerError):
                incomplete = True
            try:
                row["address"] = discovery_address(discovery_file(directory, "SPT_Data/configs/http.json", 1024**2))
                incomplete |= not bool(row["address"])
            except (OSError, ValueError, RecursionError, InstallerError):
                incomplete = True
    except (OSError, ValueError, RecursionError, InstallerError):
        return None, True
    return row, incomplete


def discover_existing_servers(exclude_service=None, runner=None):
    runner = runner or run
    deadline = time.monotonic() + 30
    units, rows, warnings = set(), [], set()
    row_bytes = 2
    failed = "Vorhandene SPT-Dienste konnten nicht vollständig ermittelt werden; die Liste kann unvollständig sein."
    try:
        for command in ("list-units", "list-unit-files"):
            arguments = ["systemctl", command, "--type=service", "--no-legend", "--no-pager", "--plain"]
            if command == "list-units":
                arguments.append("--all")
            result = runner(arguments, check=False, timeout=5)
            if result.returncode:
                warnings.add(failed)
            if len(result.stdout) > 2 * 1024**2:
                warnings.add(failed)
                continue
            for line in result.stdout.splitlines():
                token = line.split(maxsplit=1)[0] if line.strip() else ""
                if DISCOVERY_UNIT_PATTERN.fullmatch(token) and not token.endswith("@.service"):
                    units.add(token)
        ordered = sorted(units, key=lambda name: (not any(word in name.lower() for word in ("spt", "tarkov")), name))
        if len(ordered) > DISCOVERY_UNIT_LIMIT:
            warnings.add(failed)
        ordered = ordered[:DISCOVERY_UNIT_LIMIT]
        for offset in range(0, len(ordered), 64):
            remaining = deadline - time.monotonic()
            if remaining <= 0 or len(rows) >= DISCOVERY_RESULT_LIMIT:
                warnings.add(failed)
                break
            result = runner(["systemctl", "show", *ordered[offset:offset + 64], "--no-pager",
                             "--property=Id", "--property=LoadState", "--property=ActiveState",
                             "--property=WorkingDirectory", "--property=ExecStart"], check=False, timeout=min(5, remaining))
            if result.returncode:
                warnings.add(failed)
            if len(result.stdout) > 4 * 1024**2:
                warnings.add(failed)
                continue
            for block in result.stdout.strip().split("\n\n"):
                if time.monotonic() >= deadline:
                    warnings.add(failed)
                    break
                properties = dict(line.split("=", 1) for line in block.splitlines() if "=" in line)
                identifier = properties.get("Id", "")
                if not DISCOVERY_UNIT_PATTERN.fullmatch(identifier) or properties.get("LoadState") != "loaded":
                    continue
                if exclude_service and identifier == exclude_service.removesuffix(".service") + ".service":
                    continue
                row, incomplete = inspect_existing_server(properties)
                if incomplete:
                    warnings.add("Ein SPT-Dienst hat fehlende oder nicht sicher lesbare Metadaten; Version oder Adresse können unbekannt bleiben.")
                if row and not any(existing["serviceName"] == row["serviceName"] for existing in rows):
                    if len(rows) >= DISCOVERY_RESULT_LIMIT:
                        warnings.add(failed)
                        break
                    size = len(json.dumps(row, ensure_ascii=False).encode("utf-8")) + 2
                    if row_bytes + size > DISCOVERY_JSON_LIMIT:
                        warnings.add(failed)
                        continue
                    rows.append(row)
                    row_bytes += size
        return rows, " ".join(sorted(warnings))
    except (OSError, ValueError, RecursionError, InstallerError, subprocess.SubprocessError):
        warnings.add(failed)
        return rows, " ".join(sorted(warnings))


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", required=True, type=pathlib.Path)
    parser.add_argument("--action", required=True, choices=("install", "status", "start", "stop", "restart", "logs", "rollback"))
    arguments = parser.parse_args(argv)
    manager = None
    try:
        request = validate_request(read_json(arguments.request), arguments.action)
        if platform.system() != "Linux" or not hasattr(os, "geteuid") or os.geteuid() != 0:
            raise InstallerError("Dieses Werkzeug muss auf Linux mit Root-Rechten ausgeführt werden.")
        if arguments.action not in ("status", "logs"):
            signal.signal(signal.SIGHUP, signal.SIG_IGN)
            def interrupted(signum, frame):
                raise InstallerError("Vorgang unterbrochen; ein begonnener Wechsel wird zurückgesetzt.")
            signal.signal(signal.SIGTERM, interrupted)
            signal.signal(signal.SIGINT, interrupted)
        import fcntl
        lock_path = pathlib.Path("/run/lock") / ("spt-linux-installer-" + request["serviceName"] + ".lock")
        descriptor = os.open(lock_path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
        with os.fdopen(descriptor, "a") as lock:
            info = os.fstat(lock.fileno())
            if info.st_uid != 0 or not stat.S_ISREG(info.st_mode):
                raise InstallerError("Unsichere Sperrdatei; Vorgang verweigert.")
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError as error:
                raise InstallerError("Für diese Installation läuft bereits ein Vorgang.") from error
            manager = Manager(request)
            if arguments.action == "install":
                manager.install()
            elif arguments.action == "rollback":
                manager.rollback()
            elif arguments.action in ("start", "stop", "restart"):
                if not manager.owner or not manager.state().get("current"):
                    raise InstallerError("Keine vollständig eingerichtete Installation vorhanden.")
                manager.service.verify()
                manager.recover()
                manager.service.action(arguments.action)
                if arguments.action != "stop":
                    current = manager.state()["current"]
                    if not manager.health(manager.root, current["version"], current["port"]):
                        raise InstallerError("Dienst gestartet, aber die erwartete Backend-Version antwortet nicht.")
            elif arguments.action == "logs":
                if not manager.owner:
                    raise InstallerError("Keine verwaltete Installation vorhanden.")
                manager.service.verify()
                result = run(["journalctl", "-u", request["serviceName"], "-n", "150", "--no-pager", "-o", "cat"])
                for line in result.stdout.splitlines():
                    log(line)
            data = manager.status()
            emit({"event": "result", "data": data})
        return 0
    except (InstallerError, OSError, ValueError, subprocess.SubprocessError) as error:
        message = str(error) if isinstance(error, InstallerError) else f"Systemvorgang fehlgeschlagen ({type(error).__name__})."
        log(message, "error")
        try:
            data = manager.status() if manager else {"installed": False, "active": False}
        except Exception:
            data = {"installed": False, "active": False}
        data.update(error=message, detail=message)
        emit({"event": "result", "data": data})
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
