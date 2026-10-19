# Isolated integration harness

Build with .NET 10 from the repository root:

```powershell
dotnet build tools/SptLinuxInstaller.Smoke/SptLinuxInstaller.Smoke.csproj
dotnet run --project tools/SptLinuxInstaller.Smoke --no-build -- --self-test
```

The self-test is local and does not connect to any server. Remote actions are
deliberate integration operations. Do not invoke them until the test host and
operation have been authorized. Set connection values through the environment:

- `SPT_SMOKE_HOST`: SSH hostname or IP, without URL or port.
- `SPT_SMOKE_KEY`: existing private key file; use an unencrypted test key.
- `SPT_SMOKE_FINGERPRINT`: expected complete `SHA256:...` OpenSSH fingerprint.
- `SPT_SMOKE_USERNAME`: optional SSH username, defaults to `root`.

SSH port is fixed at 22. Every invocation starts with a fresh local pin store and
accepts a host key only if its SHA256 fingerprint exactly matches the environment
value. A mismatch is never accepted interactively or automatically.

Every remote action uses these non-overridable deployment options:

| Option | Value |
| --- | --- |
| Install directory | `/opt/spt-installer-smoke` |
| Service name | `spt-installer-smoke` |
| Backend TCP port | `16969` |
| Bind address | `127.0.0.1` |
| Advertised address | `127.0.0.1` |

The harness cannot target `/srv/existing-server`. Actions are `status`, `install`, `start`,
`stop`, `restart`, `logs`, and `rollback`. Only `install` accepts a second argument:
`4.1.5` fetches and validates that exact official release for an upgrade/rollback
test; otherwise installation uses the currently published stable release. It
does not fabricate a release record or skip the official SHA256 metadata.

Example **after authorization**:

```powershell
dotnet run --project tools/SptLinuxInstaller.Smoke --no-build -- status
dotnet run --project tools/SptLinuxInstaller.Smoke --no-build -- install 4.1.5
dotnet run --project tools/SptLinuxInstaller.Smoke --no-build -- install
dotnet run --project tools/SptLinuxInstaller.Smoke --no-build -- rollback
```

Standard output contains one final JSON result; progress JSON goes to standard
error. Exit codes: 0 successful Core operation, 1 operational failure, 2 invalid
input, 3 changed stored host key, 130 cancellation. A successful `status` may
report `Installed=false` or `Active=false`; callers must inspect the result.
The first-connection fingerprint rejection is an operational failure (1).

Ctrl+C requests cancellation. Core permits cancellation during preparation and
read-only queries; after a modifying command begins it waits for the confirmed
result. A lost connection is not proof that the remote transaction stopped.
The harness itself neither removes the remote test installation nor changes any
other service. Local pin files are temporary and removed after the operation.
