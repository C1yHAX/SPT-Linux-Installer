# Native Linux installer

`install.py` uses only the Python standard library. It is executed on the target
Linux host by the desktop application's SSH connection, as root or through
`sudo -n`. It installs the SPT backend only. It does not upload a game, install
client plugins, install Fika, or adopt an existing unmanaged SPT installation.

Supported hosts are x86_64 Ubuntu 22.04, 24.04, 26.04 and Debian 12, 13 with a
running systemd. Missing distribution dependencies are installed with apt;
Ubuntu must have its standard package repositories, including Universe for
7-Zip, available. A system .NET installation is neither required nor modified.

```sh
sudo -n python3 -u install.py --request request.json --action install
```

Other actions: `status`, `start`, `stop`, `restart`, `logs`, `rollback`.
The request is UTF-8 JSON:

```json
{
  "installDirectory": "/opt/spt-server",
  "serviceName": "spt-server",
  "port": 6969,
  "advertisedHost": "your-server.example.org",
  "bindAddress": "0.0.0.0",
  "releaseVersion": "4.1.6",
  "releaseUrl": "https://github.com/SP-Tushonka/build/releases/download/4.1.6/SPT-4.1.6-40743-731d7a2.7z",
  "releaseSha256": "a0713f6e5f3dbaf5c1cad16c550f28ec9241a8353262d900cea1ca2261adbc3c"
}
```

Release fields are required for `install` only. The GUI resolves them from the
official release API; the example above is a pinned illustration, not an update
schedule. Names are limited to safe lowercase slugs; install roots must be one
direct child of `/opt`. The bind address is either `0.0.0.0` or `127.0.0.1`.
The advertised address is a bare IPv4 address or DNS name and the port is
1024–65535. Firewall rules and router forwarding are managed by the operator;
the installer does not change them.

Every stdout line is a JSON object. Progress uses
`{"event":"log","level":"info","message":"..."}`. One final record uses
`{"event":"result","data":{...}}` with `installed`, `version`, `active`,
`address`, `serviceName`, `rollbackAvailable`, `recoveryRequired`, and `detail`.
Failures add `data.error` and return a nonzero exit code. A lost SSH output pipe
or SIGHUP does not interrupt a running transaction. After a connection failure,
query status before issuing another mutation; requests are not automatically
retried by the client.

## Managed files and updates

The root contains a root-owned ownership marker, `state.json`, `releases/`,
private verified runtimes under `runtimes/`, persistent `user/`, protected
`backups/`, and `current`, an atomic symbolic link to a release. Each release
links to the same persistent user directory and its corresponding private
runtime. A separate system account, identified by the ownership marker in its
account comment, runs the systemd service.

The archive must come from the official SP-Tushonka GitHub release path. HTTPS
redirect destinations are allowlisted. The official mirror is used only with
the same required GitHub SHA-256. Only native Linux server files under
`SPT_Runtime` are extracted. Archive paths, links, special files, duplicates,
and extraction results are checked. The private ASP.NET runtime is selected
from Microsoft's release metadata for the framework channel declared by SPT,
verified using SHA-512, and checked for both required shared frameworks.

The service starts only after preparation finishes. Readiness requires both
the SPT launcher ping and the exact installed version over loopback HTTPS.
The loopback health probe permits SPT's self-signed certificate; downloads
always validate TLS. The web administration default account is disabled,
authentication is enabled, and credentials remain required on localhost.

Updates are explicit actions. A request with the same version, archive hash,
bind address, advertised address and port is a no-op. Changes to connection
settings use the same backed-up transaction as a patch update. In-place
updates across a major or minor SPT version and downgrades through `install`
are refused. Use a separate installation for another SPT channel. Updates
and rollbacks are refused if user mods or patchers have been added.

Before switching, the installer stops its own service and snapshots the
entire user directory plus the old HTTP configuration. A failed new startup
restores the old release and its matching snapshot. `rollback` also restores
that release's matching profile snapshot: progress made after that snapshot
is not applied to the older version. The pre-rollback state receives another
backup. Backups and releases are retained; no scheduled update or automatic
backup deletion is installed.

Privileged access to an existing release's HTTP configuration uses pinned
directory descriptors with `O_NOFOLLOW`, including both parent directories.
Restoration replaces the file atomically through that descriptor. Ownership
changes also use descriptors and reject hardlinks and special files, so a
service-created file or directory link cannot redirect a root operation.

An interrupted switch is recorded by `pending.json`. A subsequent managed
mutation attempts recovery first. If recovery itself fails, the pending record
and backup are preserved and the result reports the problem. Never delete
the marker, state, pending record or backups to force an installation over
unknown contents. Foreign directories, service accounts, services and ports
are refused rather than adopted or stopped.

## Local verification

```sh
python3 -m unittest discover -s tests/linux -v
python3 -m py_compile installer/install.py
```

The tests use temporary files and fake systemd/network operations; they do not
install packages or access a server. They exercise archive traversal and link
rejection, foreign installation protection, exact runtime selection, mod and
version barriers, update/rollback profile matching, interrupted swaps, failed
startup recovery and a closed SSH output pipe. On Windows without symbolic
link privileges, the symbolic-link syscall and Linux-only descriptor helpers
are modeled; Linux executes the transaction tests with the real filesystem
operations. Four additional Linux-only regressions check file and parent
symlinks, hardlinks, descriptor-based ownership changes, and a parent-directory
swap during the privileged configuration write.
