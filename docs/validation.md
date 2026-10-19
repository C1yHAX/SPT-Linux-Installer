# Testing

Release: 1.0.0. Tested on Windows x64 and Ubuntu 26.04 x86_64.

## Automated checks

- 124 .NET tests cover release verification, input validation, SSH authentication,
  host-key pinning, key-file loading, credential redaction and server discovery.
- 50 Python tests cover installation, backup, rollback, configuration handling,
  systemd discovery and filesystem safety. All pass on Linux. Five cases require
  Linux directory descriptors and are skipped on Windows.
- WPF runtime checks cover scrolling, window layout, modal dialogs, existing
  server display and management-button availability.
- The portable Windows executable is built, started and rendered before release.

Run the test suites from the repository root:

```powershell
dotnet test tests/SptLinuxInstaller.Tests/SptLinuxInstaller.Tests.csproj -c Release
python -m unittest discover -s tests/linux -v
```

## Linux integration

The SSH/SFTP integration was tested on Ubuntu 26.04 using a private key and a
previously verified host fingerprint. Deployment tests used a separate directory,
service, account and loopback port.

The tests covered fresh installation of SPT 4.1.5, update to 4.1.6, repeated
installation with unchanged settings, rollback in both directions, restoration
of matching user-data snapshots, start, stop, restart, status and journal output.
Archive and runtime checksums were verified. The health endpoints reported the
expected SPT version, and the server contained no mods or Windows game files.

Status queries also detected an existing SPT service outside the selected
installation target, including its version, working directory and configured
address. Discovery did not change the existing service.

## Coverage limits

The live SSH host allowed key authentication only. Password prompts, rejected
credentials, MFA challenges and password-change requests are covered by unit
tests; a successful password login was not tested against a live host.

Other supported Linux distributions have explicit package mappings but were not
separately provisioned. GUI and backend checks are separate; no complete gameplay
session is part of the test suite. Server discovery covers direct native SPT 4
systemd services, not containers, Wine or arbitrary launch scripts.

Releases include dependency license texts and a SHA-256 checksum. The Windows
executable is not code-signed.
