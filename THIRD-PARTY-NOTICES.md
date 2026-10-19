# Third-party notices

The installer itself is licensed under the MIT license in [LICENSE](LICENSE).
It is an independent community project, not an official SPT release or launcher.

The portable Windows application is self-contained and includes the .NET runtime,
the WPF desktop runtime, SSH.NET, and its runtime dependencies. The complete
upstream license and notice texts are included in the adjacent `licenses/` folder:

| Component | Verified package version | License and notices |
| --- | --- | --- |
| SSH.NET | 2026.0.0 | [MIT license and copyright](licenses/SSH.NET/LICENSE) |
| BouncyCastle.Cryptography | 2.7.0 | [MIT license and copyright](licenses/BouncyCastle.Cryptography/LICENSE.md) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | [MIT license](licenses/Microsoft.Extensions.Logging.Abstractions/LICENSE.TXT), [complete upstream notices](licenses/Microsoft.Extensions.Logging.Abstractions/THIRD-PARTY-NOTICES.TXT) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | [MIT license](licenses/Microsoft.Extensions.DependencyInjection.Abstractions/LICENSE.TXT), [complete upstream notices](licenses/Microsoft.Extensions.DependencyInjection.Abstractions/THIRD-PARTY-NOTICES.TXT) |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.13 | [MIT license](licenses/dotnet-runtime/LICENSE.TXT), [complete runtime notices](licenses/dotnet-runtime/THIRD-PARTY-NOTICES.TXT) |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 / WPF | 10.0.13 | [MIT license](licenses/dotnet-windowsdesktop/LICENSE), [WPF notices](licenses/dotnet-windowsdesktop/WPF-THIRD-PARTY-NOTICES.TXT), [Windows Desktop companion notices](licenses/dotnet-windowsdesktop/WindowsForms-THIRD-PARTY-NOTICES.TXT) |

The upstream notices are reproduced in full, without trimming their component
lists. A notice does not mean that every feature or optional component mentioned
there is used by this application. The Windows Desktop package supplies multiple
profiles; this application selects WPF and also retains the companion profile's
upstream notices.

[Source provenance and SHA-256 hashes](licenses/sources.json) identify the exact
NuGet packages and immutable upstream commits used for these files. See
[licenses/README.md](licenses/README.md) for the collection scope and update rules.
When redistributing the portable application, keep this notice, the project's
LICENSE, and the complete `licenses/` folder alongside the executable.

The table describes the verified build dependency set. A later .NET SDK/runtime
patch or package update requires refreshing and checking the corresponding
upstream license/notice files before creating a new release. Development-only
test packages and SDK build tools are not part of the portable application.

SPT server binaries, databases, mods, EFT game assets, profiles, credentials,
and private SSH keys are **not included** in this repository or the application's
download. The remote installer retrieves the chosen official server release
and a Microsoft ASP.NET Core runtime on demand and verifies their published
checksums before use.

SPT's license is separate from this installer's license. At the initial release,
[SPT server 4.1.6](https://github.com/SP-Tushonka/server-csharp/tree/4.1.6)
is distributed under CC BY-NC-SA 4.0. Consult the license in the actual selected
SPT release. This installer's MIT license grants no additional rights to SPT,
Escape from Tarkov, their assets, or their trademarks.
