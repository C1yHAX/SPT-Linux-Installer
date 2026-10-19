import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import types
import unittest
from unittest.mock import patch, Mock

SOURCE = Path(__file__).resolve().parents[2] / "installer" / "install.py"
SPEC = importlib.util.spec_from_file_location("spt_installer", SOURCE)
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)


def request(**changes):
    value = {"installDirectory": "/opt/spt-test", "serviceName": "spt-test", "port": 16969,
             "advertisedHost": "example.org", "bindAddress": "0.0.0.0", "releaseVersion": "4.1.6",
             "releaseUrl": "https://github.com/SP-Tushonka/build/releases/download/4.1.6/SPT-4.1.6-40743-731d7a2.7z",
             "releaseSha256": "a" * 64}
    value.update(changes)
    return value


def listing(*names):
    return "\n\n".join(f"Path = {name}\nSize = 12\nAttributes = A -rw-r--r--\nEncrypted = -" for name in names)


class ValidationTests(unittest.TestCase):
    def test_valid_request_and_defaults(self):
        self.assertEqual(installer.validate_request(request(), "install")["port"], 16969)
        self.assertEqual(installer.validate_request({}, "status")["installDirectory"], "/opt/spt-server")

    def test_reject_dangerous_paths_and_service_names(self):
        for name in ("/opt", "/opt/../etc", "/srv/spt", "/home/user/spt", "/opt/a/b", "/opt/UPPER", "/opt/a;id", "/opt/a\\b", "/opt/a/"):
            with self.subTest(name=name), self.assertRaises(installer.InstallerError):
                installer.validate_request(request(installDirectory=name), "install")
        for name in ("x", "a.service", "a;id", "../test", "A-b", "a" * 32):
            with self.subTest(name=name), self.assertRaises(installer.InstallerError):
                installer.validate_request(request(serviceName=name), "install")

    def test_ports_hosts_bind_and_version(self):
        for key, values in {"port": (True, "6969", 80, 65536), "advertisedHost": ("https://x", "x:6969", "x.", "-x", "0.0.0.0", "::1", "x\ny"),
                            "bindAddress": ("192.0.2.1", "::"), "releaseVersion": ("v4.1.6", "4.1.6-beta", "4.1", None)}.items():
            for value in values:
                with self.subTest(key=key, value=value), self.assertRaises(installer.InstallerError):
                    installer.validate_request(request(**{key: value}), "install")

    def test_download_url_is_exact_official_release(self):
        base = request()["releaseUrl"]
        for url in (base.replace("https:", "http:"), base.replace("github.com", "github.com.evil.test"),
                    base.replace("github.com", "user:pass@github.com"), base + "?x=y", base.replace("40743-731d7a2", "../../evil"),
                    base.replace("/4.1.6/", "/4.1.5/"), base.replace("40743-731d7a2", "random")):
            with self.subTest(url=url), self.assertRaises(installer.InstallerError):
                installer.validate_request(request(releaseUrl=url), "install")
        with self.assertRaises(installer.InstallerError):
            installer.validate_request(request(releaseSha256="bad"), "install")

    def test_redirect_cannot_escape_https_allowlist(self):
        redirect = installer.SafeRedirect()
        with self.assertRaises(installer.InstallerError):
            redirect.redirect_request(None, None, 302, "", {}, "https://evil.example/file")
        with self.assertRaises(installer.InstallerError):
            redirect.redirect_request(None, None, 302, "", {}, "http://github.com/file")

    def test_foreign_empty_directory_is_never_adopted(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "foreign"
            root.mkdir()
            with self.assertRaises(installer.InstallerError):
                installer.require_owner(root, "spt-test")
            self.assertEqual(list(root.iterdir()), [])
            self.assertIsNone(installer.require_owner(root / "absent", "spt-test"))

    def test_mismatched_owner_marker_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            installer.atomic_json(root / installer.OWNER, {"magic": installer.MAGIC, "root": str(root), "serviceName": "other", "id": "a" * 32})
            with self.assertRaises(installer.InstallerError):
                installer.require_owner(root, "spt-test")

    def test_unknown_unit_never_overwritten(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            unit = directory / "spt-test.service"
            unit.write_text("[Service]\nExecStart=/usr/bin/other\n")
            service = installer.Service("spt-test", directory, "a" * 32, runner=Mock(), unit_directory=directory)
            with self.assertRaises(installer.InstallerError):
                service.create()
            self.assertIn("/usr/bin/other", unit.read_text())
            service.runner.assert_not_called()

    def test_partial_owned_install_allows_absent_unit_only(self):
        with tempfile.TemporaryDirectory() as temporary:
            runner = Mock(return_value=types.SimpleNamespace(stdout="not-found\n"))
            service = installer.Service("spt-test", Path(temporary), "a" * 32, runner=runner, unit_directory=Path(temporary))
            service.verify_or_absent(False)
            with self.assertRaises(installer.InstallerError):
                service.verify_or_absent(True)
            runner.return_value.stdout = "loaded\n"
            with self.assertRaises(installer.InstallerError):
                service.verify_or_absent(False)

    def test_unknown_port_owner_is_not_terminated(self):
        sock = Mock()
        sock.__enter__ = Mock(return_value=sock)
        sock.__exit__ = Mock(return_value=False)
        sock.bind.side_effect = OSError("in use")
        with patch.object(installer.socket, "socket", return_value=sock), patch.object(installer, "run") as runner:
            with self.assertRaises(installer.InstallerError):
                installer.check_port(16969)
            runner.assert_not_called()


class ArchiveTests(unittest.TestCase):
    def test_reject_7z_path_traversal_absolute_and_duplicate(self):
        for name in ("../escape", "/etc/passwd", "C:\\outside", "a/../../bad", "a//b", "a/./b", "a\\..\\b", "a\x00b"):
            with self.subTest(name=name), self.assertRaises(installer.InstallerError):
                installer.parse_7z_listing(listing(name))
        with self.assertRaises(installer.InstallerError):
            installer.parse_7z_listing(listing("x", "x"))
        with self.assertRaises(installer.InstallerError):
            installer.parse_7z_listing(listing("x", "x/y"))

    def test_reject_7z_links_devices_and_encryption(self):
        for detail in ("Symbolic Link = /etc", "Hard Link = other", "Attributes = A lrwxrwxrwx", "Attributes = A brw-r--r--", "Encrypted = +"):
            with self.subTest(detail=detail), self.assertRaises(installer.InstallerError):
                installer.parse_7z_listing(listing("link") + "\n" + detail)

    def test_extract_only_linux_server_preserves_spectre_resources(self):
        required = ["SPT_Runtime/" + item for item in (installer.ENTRY, installer.FRAMEWORK_CONFIG, "SPT_Data/configs/http.json")]
        keep = ["SPT_Runtime/libargon2_sharp_core.so", "SPT_Runtime/Argon2Sharp.dll", "SPT_Runtime/SPT_Data/dotnet/de/Spectre.Console.Cli.resources.dll"]
        excluded = ["BepInEx/plugins/test.dll", "EscapeFromTarkov.exe", "SPT_Runtime/SPT.Launcher.exe", "SPT_Runtime/SPT.Launcher.Linux",
                    "SPT_Runtime/SPT.Server.dll", "SPT_Runtime/argon2_sharp_core.dll", "SPT_Runtime/SPT_Data/Launcher/file.json",
                    "SPT_Runtime/user/mods/malicious.dll", "SPT_Runtime/runtimes/win-x64/file.dll"]
        selected = installer.server_files(installer.parse_7z_listing(listing(*(required + keep + excluded))))
        self.assertEqual({item["path"] for item in selected}, set(required + keep))

    def test_archive_without_linux_entry_refused(self):
        with self.assertRaises(installer.InstallerError):
            installer.server_files(installer.parse_7z_listing(listing("SPT_Runtime/SPT.Server.dll")))

    def make_tar(self, directory, entries):
        target = directory / "test.tar.gz"
        with tarfile.open(target, "w:gz") as archive:
            for name, kind, data in entries:
                info = tarfile.TarInfo(name)
                info.type = kind
                info.mode = 0o7777
                if kind == tarfile.REGTYPE:
                    info.size = len(data)
                    archive.addfile(info, io.BytesIO(data))
                else:
                    info.linkname = "../../escape"
                    archive.addfile(info)
        return target

    def test_tar_checks_entire_archive_before_any_write(self):
        for name, kind in (("../escape", tarfile.REGTYPE), ("/outside", tarfile.REGTYPE), ("link", tarfile.SYMTYPE), ("hard", tarfile.LNKTYPE), ("device", tarfile.CHRTYPE)):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                output = root / "output"
                output.mkdir()
                archive = self.make_tar(root, [("first", tarfile.REGTYPE, b"ok"), (name, kind, b"bad")])
                with self.assertRaises(installer.InstallerError):
                    installer.safe_extract_tar(archive, output)
                self.assertEqual(list(output.iterdir()), [])

    def test_tar_accepts_dot_prefix_and_strips_privileged_bits(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            output = root / "output"
            output.mkdir()
            archive = self.make_tar(root, [("./", tarfile.DIRTYPE, b""), ("./dotnet", tarfile.REGTYPE, b"runtime")])
            installer.safe_extract_tar(archive, output)
            self.assertEqual((output / "dotnet").read_bytes(), b"runtime")
            self.assertFalse((output / "dotnet").stat().st_mode & 0o7000)

    def test_runtime_asset_ignores_targeting_composite_wrong_rid_preview(self):
        normal = {"name": "aspnetcore-runtime-linux-x64.tar.gz", "rid": "linux-x64", "hash": "a" * 128,
                  "url": "https://builds.dotnet.microsoft.com/runtime.tar.gz"}
        release = {"aspnetcore-runtime": {"version": "10.0.13", "files": [{**normal, "name": "aspnetcore-runtime-composite-linux-x64.tar.gz"}, normal]}}
        version, selected = installer.runtime_asset({"releases": [release]}, "10.0")
        self.assertEqual(version, "10.0.13")
        self.assertIs(selected, normal)
        for updates in ({"name": "targeting-pack.tar.gz"}, {"rid": "linux-arm64"}, {"hash": "bad"}):
            with self.subTest(updates=updates), self.assertRaises(installer.InstallerError):
                installer.runtime_asset({"releases": [{"aspnetcore-runtime": {"version": "10.0.13", "files": [{**normal, **updates}]}}]}, "10.0")

    def test_http_keeps_custom_settings_and_disables_default_web_login(self):
        with tempfile.TemporaryDirectory() as temporary:
            release = Path(temporary)
            path = release / "SPT_Data/configs/http.json"
            path.parent.mkdir(parents=True)
            path.write_text(json.dumps({"customSetting": 42, "webAuthenticationConfig": {"defaultUser": {"username": "test"}}}))
            installer.configure_http(release, request())
            value = json.loads(path.read_text())
            self.assertEqual(value["customSetting"], 42)
            self.assertEqual(value["backendIp"], "example.org")
            self.assertEqual(value["webAuthenticationConfig"], {"defaultUser": {"username": "test"}, "enabled": True,
                             "enableDefaultUser": False, "requireCredentialsOnLocalhost": True, "allowDefaultUserFromAnyIp": False})


class FakeService:
    def __init__(self, active=True):
        self.active = active
        self.actions = []

    def state(self):
        return {"ActiveState": "active" if self.active else "inactive", "MainPID": "123"}

    def action(self, action):
        self.actions.append(action)
        if action in ("start", "restart"):
            self.active = True
        elif action == "stop":
            self.active = False

    def verify(self):
        pass


class TransactionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        for path in ("releases", "backups", "user/profiles", "user/mods", "user/patchers"):
            (self.root / path).mkdir(parents=True, exist_ok=True)
        self.manager = installer.Manager.__new__(installer.Manager)
        self.manager.request = request()
        self.manager.root = self.root
        self.manager.owner = {"id": "a" * 32}
        self.manager.service = FakeService()
        self.manager.chown = Mock()
        self.manager.health = Mock(return_value=True)
        self.simulated_links = os.name == "nt"
        if self.simulated_links:
            links = patch.object(Path, "symlink_to", lambda path, target, target_is_directory=False: path.write_text(str(target)))
            links.start()
            self.addCleanup(links.stop)
            reader = patch.object(installer, "read_http_config", lambda release: (release / "SPT_Data/configs/http.json").read_bytes())
            writer = patch.object(installer, "write_http_config", lambda release, data: (release / "SPT_Data/configs/http.json").write_bytes(data))
            reader.start()
            writer.start()
            self.addCleanup(reader.stop)
            self.addCleanup(writer.stop)
        self.profile = self.root / "user/profiles/player.json"
        self.profile.write_text('{"level":5}')
        self.old = self.make_release("old", "4.1.5", {"port": 16969, "label": "old"})
        self.new = self.make_release("new", "4.1.6", {"port": 16969, "label": "new"})
        installer.atomic_json(self.root / installer.STATE, {"current": self.old})
        self.manager.switch("old")
        self.port = patch.object(installer, "check_port")
        self.port.start()
        self.addCleanup(self.port.stop)

    def make_release(self, name, version, config):
        directory = self.root / "releases" / name
        (directory / "SPT_Data/configs").mkdir(parents=True)
        (directory / "SPT_Data/configs/http.json").write_text(json.dumps(config))
        return {"release": name, "version": version, "port": 16969, "bindAddress": "0.0.0.0",
                "address": "https://example.org:16969", "releaseSha256": "a" * 64}

    def current_target(self):
        link = self.root / "current"
        return (self.root / link.read_text()).resolve() if self.simulated_links else link.resolve()

    def test_update_then_rollback_restores_matching_profile_and_http(self):
        self.manager.activate(self.new)
        after = self.manager.state()
        self.assertEqual(after["current"]["version"], "4.1.6")
        self.assertEqual(after["previous"]["version"], "4.1.5")
        self.profile.write_text('{"level":99}')
        old_http = self.root / "releases/old/SPT_Data/configs/http.json"
        old_http.write_text('{"modified":true}')
        self.manager.rollback()
        self.assertEqual(self.manager.state()["current"]["version"], "4.1.5")
        self.assertEqual(json.loads(self.profile.read_text()), {"level": 5})
        self.assertEqual(json.loads(old_http.read_text())["label"], "old")
        self.assertEqual(self.current_target(), self.root / "releases/old")
        self.assertFalse((self.root / "pending.json").exists())
        self.manager.rollback()
        self.assertEqual(self.manager.state()["current"]["version"], "4.1.6")
        self.assertEqual(json.loads(self.profile.read_text()), {"level": 99})

    def test_failed_new_server_restores_profile_state_and_old_active_service(self):
        def health(root, version, port):
            if version == "4.1.6":
                self.profile.write_text('{"migration":"bad"}')
                return False
            return True
        self.manager.health = health
        with self.assertRaises(installer.InstallerError):
            self.manager.activate(self.new)
        self.assertEqual(json.loads(self.profile.read_text()), {"level": 5})
        self.assertEqual(self.manager.state()["current"], self.old)
        self.assertTrue(self.manager.service.active)
        self.assertFalse((self.root / "pending.json").exists())

    def test_failed_initial_start_keeps_empty_owned_state_stopped_and_disabled(self):
        installer.atomic_json(self.root / installer.STATE, {})
        self.manager.service.active = False
        self.manager.health.return_value = False
        with self.assertRaises(installer.InstallerError):
            self.manager.activate(self.new)
        self.assertEqual(self.manager.state(), {})
        self.assertFalse(self.manager.service.active)
        self.assertIn("disable", self.manager.service.actions)
        self.assertFalse((self.root / "current").exists())

    def test_pending_write_failure_restarts_previous_service(self):
        actual = installer.atomic_json
        def write(path, value):
            if path.name == "pending.json":
                raise OSError("full disk")
            actual(path, value)
        with patch.object(installer, "atomic_json", side_effect=write), self.assertRaises(OSError):
            self.manager.activate(self.new)
        self.assertTrue(self.manager.service.active)
        self.assertEqual(self.manager.service.actions, ["stop", "start"])
        self.assertEqual(self.manager.state()["current"], self.old)

    def test_crash_recovery_when_user_directory_rename_was_interrupted(self):
        snapshot = self.manager.snapshot(self.manager.state())
        installer.atomic_json(self.root / "pending.json", {"snapshot": snapshot, "wasActive": True})
        os.replace(self.root / "user", self.root / ".user-displaced-crash")
        self.manager.switch("new")
        self.manager.recover()
        self.assertEqual(json.loads(self.profile.read_text()), {"level": 5})
        self.assertEqual(self.current_target(), self.root / "releases/old")
        self.assertFalse((self.root / "pending.json").exists())

    def test_failed_recovery_keeps_marker_and_snapshot_for_next_retry(self):
        self.manager.health.return_value = False
        with self.assertRaises(installer.InstallerError):
            self.manager.activate(self.new)
        self.assertTrue((self.root / "pending.json").exists())
        pending = installer.read_json(self.root / "pending.json")
        self.assertTrue((self.root / "backups" / pending["snapshot"] / "user/profiles/player.json").exists())

    def test_modded_update_is_refused_before_stopping(self):
        (self.root / "user/mods/my-mod").mkdir()
        with self.assertRaises(installer.InstallerError):
            self.manager.activate(self.new)
        self.assertEqual(self.manager.service.actions, [])
        self.assertEqual(self.manager.state()["current"], self.old)

    def test_same_release_and_config_install_is_noop(self):
        installer.atomic_json(self.root / installer.STATE, {"current": self.new})
        self.manager.preflight = Mock(return_value=("ubuntu", "24.04"))
        with patch.object(installer, "install_prerequisites") as prerequisites, patch.object(installer, "download") as download:
            self.manager.install()
        prerequisites.assert_not_called()
        download.assert_not_called()
        self.assertEqual(self.manager.service.actions, [])

    def test_cross_channel_and_downgrade_refused_without_mutation(self):
        self.manager.preflight = Mock(return_value=("ubuntu", "24.04"))
        for version in ("4.2.0", "5.0.0", "4.1.4"):
            self.manager.request["releaseVersion"] = version
            with self.subTest(version=version), patch.object(installer, "install_prerequisites") as prerequisites:
                with self.assertRaises(installer.InstallerError):
                    self.manager.install()
                prerequisites.assert_not_called()
        self.assertEqual(self.manager.service.actions, [])

    def test_foreign_same_name_account_is_rejected(self):
        fake = types.SimpleNamespace(pw_uid=1001, pw_gecos="someone else", pw_dir=str(self.root / "user"), pw_shell="/usr/sbin/nologin")
        with patch.dict(sys.modules, {"pwd": types.SimpleNamespace(getpwnam=lambda _: fake)}):
            with self.assertRaises(installer.InstallerError):
                self.manager.verify_account(True)

    def test_partial_claim_can_resume_after_download_failure(self):
        fake = types.SimpleNamespace(pw_uid=1001, pw_gecos=installer.MAGIC + " " + self.manager.owner["id"],
                                     pw_dir=str(self.root / "user"), pw_shell="/usr/sbin/nologin")
        with patch.dict(sys.modules, {"pwd": types.SimpleNamespace(getpwnam=lambda _: fake)}), patch.object(installer, "run") as runner:
            self.manager.claim()
            self.manager.claim()
            runner.assert_not_called()
        self.assertTrue((self.root / ".staging").is_dir())
        self.assertEqual(json.loads(self.profile.read_text()), {"level": 5})


class ProtocolTests(unittest.TestCase):
    def test_closed_output_pipe_does_not_abort_transaction(self):
        broken = Mock()
        broken.write.side_effect = BrokenPipeError()
        previous = installer.OUTPUT_AVAILABLE
        try:
            with patch.object(installer.sys, "stdout", broken):
                installer.log("progress")
                installer.emit({"event": "result", "data": {"active": True}})
                self.assertFalse(installer.OUTPUT_AVAILABLE)
                installer.sys.stdout.close()
        finally:
            installer.OUTPUT_AVAILABLE = previous

    def test_invalid_request_returns_json_result_and_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "request.json"
            path.write_text(json.dumps(request(installDirectory="/etc")))
            result = subprocess.run([sys.executable, str(SOURCE), "--request", str(path), "--action", "install"], capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            records = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(records[-1]["event"], "result")
            self.assertIn("error", records[-1]["data"])


class ExistingServerDiscoveryTests(unittest.TestCase):
    @staticmethod
    def properties(name="spt-manual.service", active="active", working="/srv/manual/server"):
        return {"Id": name, "LoadState": "loaded", "ActiveState": active, "WorkingDirectory": working,
                "ExecStart": "{ path=/usr/bin/dotnet ; argv[]=/usr/bin/dotnet /srv/manual/server/SPT.Server.Linux.dll ; ignore_errors=no ; }"}

    @staticmethod
    def metadata(directory, relative, limit, read=True):
        if relative == installer.ENTRY:
            return None
        if relative == installer.FRAMEWORK_CONFIG:
            return {"runtimeOptions": {"frameworks": []}}
        if relative.endswith(".deps.json"):
            return {"libraries": {"SPT.Server.Linux/4.1.6-RELEASE+731d7a2.20260918": {}}}
        if relative.endswith("http.json"):
            return {"ip": "0.0.0.0", "backendIp": "example.org", "port": 6969, "backendPort": 6969,
                    "webAuthenticationConfig": {"password": "must-not-be-output"}}
        raise AssertionError(relative)

    @staticmethod
    def fake_runner(properties):
        def run(arguments, **kwargs):
            if arguments[0] != "systemctl" or arguments[1] not in ("list-units", "list-unit-files", "show"):
                raise AssertionError("Discovery attempted a non-read-only command")
            if arguments[1] != "show":
                output = "\n".join(row["Id"] + " enabled" for row in properties)
            else:
                requested = set(arguments)
                output = "\n\n".join("\n".join(key + "=" + value for key, value in row.items())
                                       for row in properties if row["Id"] in requested)
            return types.SimpleNamespace(returncode=0, stdout=output, stderr="")
        return Mock(side_effect=run)

    def test_native_manual_service_detected_without_running_it(self):
        runner = self.fake_runner([self.properties()])
        with patch.object(installer, "discovery_directory", return_value=installer.contextlib.nullcontext(123)), \
                patch.object(installer, "discovery_file", side_effect=self.metadata):
            found, warning = installer.discover_existing_servers(runner=runner)
        self.assertEqual(warning, "")
        self.assertEqual(found, [{"serviceName": "spt-manual", "installDirectory": "/srv/manual/server",
                                "active": True, "version": "4.1.6", "address": "https://example.org:6969"}])
        self.assertNotIn("must-not-be-output", json.dumps(found))
        self.assertTrue(all(call.args[0][0] == "systemctl" for call in runner.call_args_list))

    def test_status_keeps_selected_uninstalled_target_separate_from_existing_server(self):
        manager = installer.Manager.__new__(installer.Manager)
        manager.request = request()
        manager.root = Path("/opt/spt-test")
        manager.owner = manager.service = None
        other = {"serviceName": "spt-manual", "installDirectory": "/srv/manual/server", "active": True,
                 "version": "4.1.6", "address": "https://example.org:6969"}
        with patch.object(installer, "discover_existing_servers", return_value=([other], "")) as discovery:
            status = manager.status()
        self.assertFalse(status["installed"])
        self.assertFalse(status["active"])
        self.assertEqual(status["serviceName"], "spt-test")
        self.assertEqual(status["existingServers"], [other])
        self.assertIn("gewählten Zielpfad", status["detail"])
        self.assertIn(str(manager.root), status["detail"])
        discovery.assert_called_once_with(None)
        self.assertIsNone(manager.owner)

    def test_stopped_existing_server_remains_visible_and_own_service_is_excluded(self):
        rows = [self.properties("spt-own.service"), self.properties("manual.service", active="inactive")]
        with patch.object(installer, "discovery_directory", return_value=installer.contextlib.nullcontext(123)), \
                patch.object(installer, "discovery_file", side_effect=self.metadata):
            found, warning = installer.discover_existing_servers("spt-own", runner=self.fake_runner(rows))
        self.assertEqual(len(found), 1)
        self.assertEqual(found[0]["serviceName"], "manual")
        self.assertFalse(found[0]["active"])
        self.assertEqual(warning, "")

    def test_spt_name_or_argument_alone_does_not_identify_server(self):
        for command in ("{ path=/usr/bin/echo ; argv[]=/usr/bin/echo SPT.Server.Linux.dll ; }",
                        "{ path=/usr/bin/dotnet ; argv[]=/usr/bin/dotnet Other.dll ; }",
                        "{ path=/bin/bash ; argv[]=/bin/bash start-spt.sh ; }"):
            properties = {**self.properties(), "ExecStart": command}
            with patch.object(installer, "discovery_directory") as directory:
                self.assertEqual(installer.inspect_existing_server(properties), (None, False))
                directory.assert_not_called()

    def test_missing_optional_metadata_is_visible_as_unknown_with_warning(self):
        def missing(directory, relative, limit, read=True):
            if relative.endswith(".deps.json") or relative.endswith("http.json"):
                raise FileNotFoundError()
            return self.metadata(directory, relative, limit, read)
        with patch.object(installer, "discovery_directory", return_value=installer.contextlib.nullcontext(123)), \
                patch.object(installer, "discovery_file", side_effect=missing):
            found, warning = installer.discover_existing_servers(runner=self.fake_runner([self.properties()]))
        self.assertEqual(found[0]["version"], "")
        self.assertEqual(found[0]["address"], "")
        self.assertTrue(warning)

    def test_missing_server_files_do_not_create_a_false_positive(self):
        with patch.object(installer, "discovery_directory", return_value=installer.contextlib.nullcontext(123)), \
                patch.object(installer, "discovery_file", side_effect=FileNotFoundError()):
            found, warning = installer.discover_existing_servers(runner=self.fake_runner([self.properties()]))
        self.assertEqual(found, [])
        self.assertTrue(warning)

    def test_failed_systemd_inventory_returns_warning_not_an_installation_failure(self):
        runner = Mock(side_effect=subprocess.TimeoutExpired("systemctl", 5))
        found, warning = installer.discover_existing_servers(runner=runner)
        self.assertEqual(found, [])
        self.assertTrue(warning)

    def test_template_units_are_not_queried_but_instances_are(self):
        properties = [self.properties("spt-manual.service"), self.properties("getty@.service"), self.properties("getty@tty1.service")]
        runner = self.fake_runner(properties)
        with patch.object(installer, "inspect_existing_server", return_value=(None, False)):
            installer.discover_existing_servers(runner=runner)
        show = [argument for call in runner.call_args_list if call.args[0][1] == "show" for argument in call.args[0]]
        self.assertNotIn("getty@.service", show)
        self.assertIn("getty@tty1.service", show)

    def test_partial_systemctl_error_keeps_valid_service_blocks(self):
        base_runner = self.fake_runner([self.properties()])
        def partial(arguments, **kwargs):
            result = base_runner(arguments, **kwargs)
            if arguments[1] == "show":
                result.returncode = 1
            return result
        with patch.object(installer, "discovery_directory", return_value=installer.contextlib.nullcontext(123)), \
                patch.object(installer, "discovery_file", side_effect=self.metadata):
            found, warning = installer.discover_existing_servers(runner=partial)
        self.assertEqual(len(found), 1)
        self.assertEqual(found[0]["serviceName"], "spt-manual")
        self.assertTrue(warning)

    def test_inventory_and_result_counts_are_bounded(self):
        properties = [self.properties(f"spt-{number:04}.service") for number in range(installer.DISCOVERY_UNIT_LIMIT + 10)]
        runner = self.fake_runner(properties)
        with patch.object(installer, "inspect_existing_server", return_value=(None, False)):
            found, warning = installer.discover_existing_servers(runner=runner)
        inspected = [argument for call in runner.call_args_list if call.args[0][1] == "show"
                     for argument in call.args[0] if argument.endswith(".service")]
        self.assertEqual(len(inspected), installer.DISCOVERY_UNIT_LIMIT)
        self.assertEqual(found, [])
        self.assertTrue(warning)
        with patch.object(installer, "discovery_directory", return_value=installer.contextlib.nullcontext(123)), \
                patch.object(installer, "discovery_file", side_effect=self.metadata):
            found, warning = installer.discover_existing_servers(runner=self.fake_runner(properties[:70]))
        self.assertEqual(len(found), installer.DISCOVERY_RESULT_LIMIT)
        self.assertTrue(warning)

    def test_address_never_uses_wildcard_ssh_host_or_credential_url(self):
        for host in ("0.0.0.0", "::", "https://secret@example.org", "bad\nvalue"):
            self.assertEqual(installer.discovery_address({"backendIp": host, "backendPort": 6969}), "")
        self.assertEqual(installer.discovery_address({"ip": "127.0.0.1", "port": 6969}), "https://127.0.0.1:6969")
        self.assertEqual(installer.discovery_address({"backendIp": "2001:db8::1", "backendPort": 6969}), "https://[2001:db8::1]:6969")

    def test_discovery_json_budget_preserves_complete_core_protocol_result(self):
        properties = [self.properties(f"spt-{number}.service", working="/srv/" + "a" * 1000) for number in range(64)]
        with patch.object(installer, "discovery_directory", return_value=installer.contextlib.nullcontext(123)), \
                patch.object(installer, "discovery_file", side_effect=self.metadata):
            found, warning = installer.discover_existing_servers(runner=self.fake_runner(properties))
        self.assertGreater(len(found), 0)
        self.assertLess(len(found), 64)
        self.assertLessEqual(len(json.dumps(found, ensure_ascii=False).encode("utf-8")), installer.DISCOVERY_JSON_LIMIT)
        self.assertTrue(warning)

    def test_deeply_nested_metadata_reports_incomplete_discovery(self):
        def nested(directory, relative, limit, read=True):
            if relative == installer.FRAMEWORK_CONFIG:
                return json.loads("[" * 40000 + "0" + "]" * 40000)
            return self.metadata(directory, relative, limit, read)
        with patch.object(installer, "discovery_directory", return_value=installer.contextlib.nullcontext(123)), \
                patch.object(installer, "discovery_file", side_effect=nested):
            found, warning = installer.discover_existing_servers(runner=self.fake_runner([self.properties()]))
        self.assertEqual(found, [])
        self.assertTrue(warning)

    def test_deadline_is_checked_between_candidates_inside_one_batch(self):
        elapsed = [0]
        def inspect(properties):
            elapsed[0] += 1
            return None, False
        properties = [self.properties(f"spt-{number}.service") for number in range(64)]
        with patch.object(installer.time, "monotonic", side_effect=lambda: elapsed[0]), \
                patch.object(installer, "inspect_existing_server", side_effect=inspect) as inspection:
            found, warning = installer.discover_existing_servers(runner=self.fake_runner(properties))
        self.assertLessEqual(inspection.call_count, 30)
        self.assertEqual(found, [])
        self.assertTrue(warning)


@unittest.skipUnless(os.name == "posix", "Requires Linux directory descriptors and real symlinks")
class DescriptorSafetyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.release = self.root / "release"
        self.config = self.release / "SPT_Data/configs"
        self.config.mkdir(parents=True)
        (self.config / "http.json").write_bytes(b"old")
        self.outside = self.root / "outside"
        self.outside.mkdir()
        (self.outside / "http.json").write_bytes(b"protected")

    def test_http_file_symlink_and_parent_symlink_refused(self):
        target = self.config / "http.json"
        target.unlink()
        target.symlink_to(self.outside / "http.json")
        for operation in (lambda: installer.read_http_config(self.release), lambda: installer.write_http_config(self.release, b"new")):
            with self.assertRaises((installer.InstallerError, OSError)):
                operation()
        target.unlink()
        self.config.rmdir()
        self.config.symlink_to(self.outside, target_is_directory=True)
        for operation in (lambda: installer.read_http_config(self.release), lambda: installer.write_http_config(self.release, b"new")):
            with self.assertRaises((installer.InstallerError, OSError)):
                operation()
        self.assertEqual((self.outside / "http.json").read_bytes(), b"protected")

    def test_parent_pivot_after_open_cannot_redirect_root_write(self):
        real_open = os.open
        saved = self.config.with_name("pinned-configs")
        changed = False
        def pivot(name, flags, *args, **kwargs):
            nonlocal changed
            if str(name).startswith(".http-restore-") and not changed:
                changed = True
                self.config.rename(saved)
                self.config.symlink_to(self.outside, target_is_directory=True)
            return real_open(name, flags, *args, **kwargs)
        with patch.object(installer.os, "open", side_effect=pivot):
            installer.write_http_config(self.release, b"new")
        self.assertTrue(changed)
        self.assertEqual((self.outside / "http.json").read_bytes(), b"protected")
        self.assertEqual((saved / "http.json").read_bytes(), b"new")

    def test_chown_never_follows_directory_or_file_symlink(self):
        (self.release / "external-directory").symlink_to(self.outside, target_is_directory=True)
        (self.release / "external-file").symlink_to(self.outside / "http.json")
        visited = []
        with patch.object(installer.os, "fchown", side_effect=lambda descriptor, uid, gid: visited.append(os.fstat(descriptor).st_ino)):
            installer.chown_tree_no_links(self.release, os.getuid(), os.getgid())
        self.assertNotIn(self.outside.stat().st_ino, visited)
        self.assertNotIn((self.outside / "http.json").stat().st_ino, visited)
        self.assertIn((self.config / "http.json").stat().st_ino, visited)

    def test_chown_rejects_hardlink_to_other_file(self):
        os.link(self.outside / "http.json", self.release / "hardlink")
        with patch.object(installer.os, "fchown"), self.assertRaises(installer.InstallerError):
            installer.chown_tree_no_links(self.release, os.getuid(), os.getgid())

    def test_discovery_rejects_file_links_special_files_and_large_metadata(self):
        file = self.config / "http.json"
        file.unlink()
        file.symlink_to(self.outside / "http.json")
        with installer.discovery_directory(str(self.release)) as directory:
            with self.assertRaises((installer.InstallerError, OSError)):
                installer.discovery_file(directory, "SPT_Data/configs/http.json", 1024)
        file.unlink()
        os.mkfifo(file)
        with installer.discovery_directory(str(self.release)) as directory:
            with self.assertRaises(installer.InstallerError):
                installer.discovery_file(directory, "SPT_Data/configs/http.json", 1024)
        file.unlink()
        file.write_bytes(b"x" * 128)
        with installer.discovery_directory(str(self.release)) as directory:
            with self.assertRaises(installer.InstallerError):
                installer.discovery_file(directory, "SPT_Data/configs/http.json", 64)


if __name__ == "__main__":
    unittest.main()
