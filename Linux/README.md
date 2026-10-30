# MGRemote Linux 0.1.1 — prototype

Independent Avalonia/.NET 10 Linux x86-64 application. Windows sources and installer are unchanged.

Download the AppImage, then run `chmod +x MGRemote-Linux-0.1.1-x86_64.AppImage` and `./MGRemote-Linux-0.1.1-x86_64.AppImage`.
An X11 desktop (including XWayland), the distribution's Avalonia/.NET native dependencies, OpenSSH client, FreeRDP and gnome-terminal, konsole, xfce4-terminal or xterm are required. The .NET runtime is bundled; SSH, FreeRDP and desktop libraries are not. FUSE may be required; alternatively use `./MGRemote-Linux-0.1.1-x86_64.AppImage --appimage-extract-and-run`.

Create a connection, select SSH/RDP and save. Sessions open in separate native Linux windows. SSH authentication and clipboard are handled by the Linux terminal; private keys are optional and must use OpenSSH/PEM format with private permissions. SFTP opens an interactive native client (`put`, `get`, recursive directory transfer according to the installed client). No passwords are stored or passed as process arguments. RDP keeps NLA and certificate validation, supports optional text clipboard and multiple monitors; drive sharing is not enabled.

Import accepts SSH2/RDP metadata in non-fully-encrypted mRemoteNG XML. Protocol, port, username and domain inheritance is resolved from parent folders. Passwords and Windows keys/options are not migrated; verify each imported connection. Connection metadata is stored in the Linux local application-data folder under `MGRemoteLinux/connections.json`. Existing OpenSSH config aliases may define ProxyJump and identities. There is no Windows vault conversion or integrated tunnel editor yet.

Build with .NET 10 and PowerShell 7: `pwsh ./Build-AppImage.ps1 -SquashfsTool /usr/bin/mksquashfs`. The script pins and verifies the official AppImage runtime hash; output includes an AppImage, corresponding source ZIP and SHA-256 manifest. Under Windows a trusted MSYS2 mksquashfs and its DLLs can be supplied instead. `dotnet run --project MGRemote.Linux.csproj -- --self-test` runs synthetic offline checks without loading saved user data.

This first prototype is unsigned. It is cross-built on Windows; structural packaging checks and synthetic tests do not establish Linux startup, distribution compatibility or live RDP/SSH functionality. No parity with the Windows edition is claimed.
