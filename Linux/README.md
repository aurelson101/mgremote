# MGRemote Linux 0.1.2 — prototype

Independent Avalonia/.NET 10 Linux x86-64 application. Windows sources and installer are unchanged.

Download the AppImage, then run `chmod +x MGRemote-Linux-0.1.2-x86_64.AppImage` and `./MGRemote-Linux-0.1.2-x86_64.AppImage`.
An X11 desktop (including XWayland), the distribution's Avalonia/.NET native dependencies, OpenSSH client, FreeRDP and gnome-terminal, konsole, xfce4-terminal or xterm are required. The .NET runtime is bundled; SSH, FreeRDP and desktop libraries are not. FUSE may be required; alternatively use `./MGRemote-Linux-0.1.2-x86_64.AppImage --appimage-extract-and-run`.

Create a connection, select SSH/RDP and save. Sessions open in separate native Linux windows. SSH authentication and clipboard are handled by the Linux terminal; private keys are optional and must use OpenSSH/PEM format with private permissions. SFTP opens an interactive native client (`put`, `get`, recursive directory transfer according to the installed client). No passwords are stored or passed as process arguments. RDP keeps NLA and certificate validation, supports optional text clipboard and multiple monitors; drive sharing is not enabled.

Import accepts SSH2/RDP metadata in non-fully-encrypted mRemoteNG XML. Protocol, port, username and domain inheritance is resolved from parent folders. Passwords and Windows keys/options are not migrated; verify each imported connection. Connection metadata is stored in the Linux local application-data folder under `MGRemoteLinux/connections.json`. Existing OpenSSH config aliases may define ProxyJump and identities. There is no Windows vault conversion or integrated tunnel editor yet.

Build with .NET 10 and PowerShell 7: `pwsh ./Build-AppImage.ps1 -SquashfsTool /usr/bin/mksquashfs`. The script pins and verifies the official AppImage runtime hash; output includes an AppImage, corresponding source ZIP and SHA-256 manifest. Under Windows a trusted MSYS2 mksquashfs and its DLLs can be supplied instead. `dotnet run --project MGRemote.Linux.csproj -- --self-test` runs synthetic offline checks without loading saved user data.

This prototype is unsigned. Published packages are built and exercised on Ubuntu 24.04 x86-64 with Xvfb; the release manifest records the exact validation environment. Packaged self-tests, GUI rendering and picocom pseudo-terminal checks do not establish physical serial compatibility, FUSE mounting, other distribution support or live RDP/SSH functionality. No parity with the Windows edition is claimed.

## Serial consoles and stability changes

Select Serial, set /dev/ttyUSB0 or a /dev/serial/by-id/ path, and configure baud rate, data bits, parity, stop bits and flow control. Install picocom using your distribution package manager. The app never changes device permissions or runs sudo. Exit picocom with Ctrl+A then Ctrl+X. Default is 9600 baud, 8N1, no flow control. Hardware settings must match the switch.

The editor provides search and fixed action buttons. Failed connection-file loads block saving/importing until the file is repaired and the application restarted. Import reports unsupported protocol counts and rejects fully encrypted XML explicitly. CI checks serial input/output and shutdown with pseudo-terminals; physical adapters and switches still require a pilot.
