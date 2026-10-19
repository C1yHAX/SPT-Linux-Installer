# Reporting a security issue

Please avoid publishing passwords, private keys, player profiles, or authentication
cookies in an issue. Report a vulnerability through this repository's private
security reporting facility when available, or contact the repository owner
without including exploit credentials.

The installer verifies SSH host keys, official archive hashes and runtime hashes.
Passwords and key passphrases are kept in memory and are not written to settings
or sent in installer JSON. A changed SSH host key is rejected rather than accepted
automatically.

The remote service runs as an unprivileged dedicated user. Public game access and
SSH administration are separate. The SPT web default account and localhost login
bypass are disabled by the managed configuration. This does not make SPT's public
game API a private or independently authenticated service; limit network access
to your intended players when appropriate. Cloud firewall changes are not made by
this application.

Installation is limited to marked directories under `/opt`. The installer refuses
unknown existing installations and occupied ports. Installing/updating third-party
mods is outside the supported scope.
