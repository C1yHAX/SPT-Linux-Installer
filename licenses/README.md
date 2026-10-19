# Bundled third-party license texts

These are complete upstream license and notice files for the portable Windows
application, collected on 2026-10-19. They were copied without content changes.
The application uses the WPF profile of the self-contained Windows Desktop
runtime. Full notices are intentionally retained, including the Windows Desktop
companion profile's notices, rather than deleting upstream entries based on
which features are exercised at runtime.

## Sources

- SSH.NET 2026.0.0: `LICENSE` from upstream commit
  `7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e`, which is recorded in the official
  restored NuGet package's repository metadata. The NuGet package declares MIT
  but does not itself contain a standalone license text.
- BouncyCastle.Cryptography 2.7.0: `LICENSE.md` from its official NuGet package.
- Microsoft.Extensions.Logging.Abstractions 8.0.3 and
  Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2: `LICENSE.TXT` and
  `THIRD-PARTY-NOTICES.TXT` from each official NuGet package.
- Microsoft.NETCore.App.Runtime.win-x64 10.0.13: `LICENSE.TXT` and
  `THIRD-PARTY-NOTICES.TXT` from the official self-contained runtime package.
- Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.13: `LICENSE` from its
  official runtime package. The WPF and Windows Forms third-party notices are
  copied from the package's recorded `dotnet/dotnet` source commit
  `4099f83977f4d29604a9aeb971fb0a7bc1d5f699` because the package does not contain
  standalone third-party notice files.

[sources.json](sources.json) records every source URL, source path inside its
package where applicable, byte count, and SHA-256 checksum. Package copies were
checked against their restored source files after copying. No machine-specific
paths or credentials are recorded in the manifest.
The local `.gitattributes` disables line-ending conversion so those upstream
bytes and checksums remain reproducible after a Git checkout.

## Updating and packaging

1. Restore and publish the Windows application for the intended release.
2. Check its resolved runtime dependencies and self-contained .NET/Windows
   Desktop runtime versions. SDK patch selection can change runtime versions.
3. Refresh the corresponding complete license and notice texts from those
   exact official package versions or their recorded source commits. Update
   `sources.json` and the version table in `THIRD-PARTY-NOTICES.md` together.
4. Include the entire `licenses/` directory and root `THIRD-PARTY-NOTICES.md`
   in the portable ZIP alongside the executable and root project `LICENSE`.
   Verify the packaged license files against the manifest.

These texts cover the libraries redistributed inside the Windows application.
They do not relicense SPT, EFT, downloaded server/runtime packages, or Linux
distribution packages. Those downloads retain their own upstream license files.
SPT/EFT binaries, mods, game assets and player profiles are not contained here.

Test packages and SDK build tools (such as the ILLink build tasks) are development
dependencies and are not shipped as part of the Windows application download.
