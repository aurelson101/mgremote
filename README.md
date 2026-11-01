# MGRemote

MGRemote is an independent remote-connection manager derived from [mRemoteNG](https://github.com/mRemoteNG/mRemoteNG). It provides a Windows desktop edition and a separate Linux prototype. This project is not affiliated with or endorsed by the upstream mRemoteNG team.

## Downloads

| Platform | Current package | Status |
| --- | --- | --- |
| Windows x64 | [MSI 0.1.15](https://github.com/aurelson101/mgremote/releases/tag/windows-v0.1.15) | Unsigned preview |
| Linux x86-64 | [AppImage 0.2.13](https://github.com/aurelson101/mgremote/releases/tag/linux-v0.2.13) | Unsigned development preview; embedded X11 sessions, incomplete Windows parity |

Download the installer or AppImage from the matching release. SHA-256 manifests and corresponding source archives accompany the packages. The editions have independent version numbers and are not feature-equivalent.

## Windows edition

- Tabbed RDP, SSH and VNC connections, with a separate connection-properties panel.
- Tools → Serial console (COM): integrated ANSI terminal, configurable baud rate, 5–8 data bits, parity, 1/2 stop bits, flow control and optional DTR. Default: 9600 baud, 8N1, no flow control. Serial sessions use independent windows, not imported XML tree nodes.
- Integrated ANSI/VT SSH terminal using Windows OpenSSH, ConPTY and locally bundled xterm.js. Imported SSH2 connections use this terminal even if their saved backend still says PuTTYNG.
- Optional OpenSSH/PEM private keys, native interactive passphrase/password prompts, OpenSSH profiles and managed SSH bastion tunnels.
- SSH copy/paste with Ctrl+Shift+C / Ctrl+Shift+V or the context menu. Multiline paste requires confirmation; Ctrl+C remains an interrupt.
- Bidirectional file/folder transfer through the dedicated SFTP transfer window. Files are not pasted into the remote shell as commands.
- Legacy imported RDP preferences are securely overridden for the session: server authentication and CredSSP/NLA remain required, without rewriting the saved profile.
- Explicit RDP clipboard and selected-drive sharing controls, plus monitor and window-placement recovery. Sharing changes require reconnection; server policies may restrict them.
- mRemoteNG XML import, optional external AnyDesk integration and Windows MSI maintenance/upgrade support.

Install the MSI normally. Windows OpenSSH Client and the Microsoft Edge WebView2 Runtime are required for the integrated SSH terminal. PuTTY PPK keys must be converted to OpenSSH/PEM for OpenSSH use. Always verify the server fingerprint or certificate through a trusted channel.

Silent installation and removal use standard Windows Installer commands:

```powershell
msiexec /i "MGRemote-0.1.15-win-x64-preview.msi" /qn /norestart
msiexec /x "MGRemote-0.1.15-win-x64-preview.msi" /qn /norestart
```

Removal preserves user settings by default. `PURGEUSERDATA=1` explicitly requests cleanup of app-owned data for the current interactive user. It does not erase operating-system or antivirus records, other users' profiles, documents or personal SSH keys. See [Windows implementation notes](MGREMOTE-WINDOWS.md).

## Linux desktop development preview

Automatic vector icons use saved OS metadata and connection type; Auto/Windows/Linux/Network/Serial overrides and imported icons are preserved. Configured usernames identify connections. Help includes an offline guide and About dialog. Multimonitor RDP opens a managed native fullscreen window. Local X11 clipboard copy/paste is tested; remote RDP clipboard and physical monitor behavior still require a server/hardware pilot.

At startup, an unencrypted profile offers optional full-profile AES-256-GCM encryption; choose Later to continue without it. Encrypted profiles require their passphrase before loading or saving. File → Lock profile closes application-owned sessions, clears the active key/editor and requires unlocking again. Search retains folder expansion, selection and unsaved editor text. Enable it later from File. SSH properties include private keys, OpenSSH certificates, a bastion alias and an explicit loopback-only local tunnel; RDP offers dynamic/fixed/fullscreen resolution.

Right-click the connection tree to create folders or connections, edit/rename, duplicate and connect. Empty folders persist across restarts; new connections appear as drafts until configured. F2 edits/renames and Insert creates a connection. The new versioned local profile format reads older JSON arrays, but older AppImages cannot read the new format; keep a profile backup if a downgrade is needed.

The Linux edition uses Avalonia, a bundled .NET runtime and a Windows-style tree/configuration/session layout with resizable panes. X11/XWayland sessions are embedded in closeable tabs using xterm, OpenSSH, FreeRDP and picocom. It includes graphical SFTP file/folder transfers, optional SSH keys and bastion aliases, explicit RDP folder sharing, color/audio/console settings and interactive RDP gateways. Multimonitor RDP uses the external-window action. Windows feature parity remains incomplete.

```sh
chmod +x MGRemote-Linux-0.2.13-x86_64.AppImage
./MGRemote-Linux-0.2.13-x86_64.AppImage
```

Install OpenSSH 9+, FreeRDP, picocom for serial, and Avalonia native dependencies through your distribution. An X11/XWayland desktop is required. The AppImage bundles xterm and its terminal-specific libraries; OpenSSH, FreeRDP, picocom and desktop libraries remain system dependencies. If FUSE is unavailable, use `./MGRemote-Linux-0.2.13-x86_64.AppImage --appimage-extract-and-run`.

Authenticated XML import supports AES/Twofish/Serpent with GCM/EAX, including full-file encryption and inherited connection passwords. Optional password migration reencrypts secrets with a distinct local passphrase; master passphrases are not persisted. Unlock per connection to use an imported password for SSH/SFTP or direct embedded RDP, or choose manual authentication. Automatic SSH requires an already verified known host and an exact destination password prompt. Bastions, private-key passphrases, MFA, gateways and external/multimonitor credentials retain the manual path. Real remote authentication still requires validation.

Legacy Rijndael/CCM, Windows vault migration, unsupported protocols and unmapped Windows options are not fully compatible. Arbitrary SSH options and Windows key paths are not executed/imported. Review the settings before connecting. See the [versioned Linux instructions](https://github.com/aurelson101/mgremote/blob/linux-v0.2.13/Linux/README.md) for requirements, secret limits and authentication restrictions. Serial profiles use Linux device paths and picocom (Ctrl+A then Ctrl+X to exit); device permissions are not changed.

To move a Linux profile, use **File → Export encrypted profile** and choose a `.mgremote` file plus an export passphrase. On the other Linux PC, choose **File → Import encrypted profile**. Import merges saved connections without removing local ones and offers local encryption. SSH private-key files and OpenSSH config are not included; existing nested secrets retain their original local phrase.

## Security and validation status

These are unsigned development releases. Antivirus results describe a particular local scan, not a guarantee against all detections or SmartScreen warnings. No antivirus exclusions or certificate-validation bypasses are required or recommended.

Windows validation includes targeted security checks, synthetic XML/RDP imports, GUI lifecycle checks, actual offline RDP ActiveX redirection settings, ConPTY terminal rendering and input, context-menu lifecycle, owned-process cleanup and MSI table/maintenance checks. The release includes its Sophos scan result.

Linux validation uses synthetic metadata/argument/import checks. The manually triggered [Linux workflow](.github/workflows/mgremote-linux.yml) builds the AppImage, runs its self-test on Ubuntu and renders its GUI under Xvfb. Check the workflow result and the release manifest for the exact tested environment.

Review covers maintained connection, import, persistence, terminal and lifecycle paths, plus compilation and dependency checks; it is not a claim that all upstream and third-party code is defect-free.

Physical USB/serial adapters and real switch consoles remain untested. Serial transport tests cover ANSI/split UTF-8, input delivery and cleanup; Linux validation also uses picocom with pseudo-terminals. SSH1 and Telnet remain disabled by default; serial access does not weaken SSH host-key or RDP certificate checks.

Live RDP/SSH authentication, real remote file transfer, mixed-DPI/hot-plug monitors and clean-machine Windows installation/removal have not been validated in this project session. RDP through an SSH loopback tunnel may fail certificate-name checks; authentication is not weakened to hide that limitation. Linux desktop/distribution compatibility and live sessions require a pilot.

Do not open a public issue containing passwords, private keys or unredacted connection XML. Keep your clients and operating system patched, and verify all first-use host keys and certificates.

## Build

Windows: install .NET 10 and Visual Studio Build Tools with the .NET desktop workload, then run:

```powershell
./Tools/Build-MGRemotePackage.ps1
```

Linux AppImage: install .NET 10, PowerShell 7 and squashfs-tools, then run:

```sh
dotnet run --project Linux/MGRemote.Linux.csproj -- --self-test
pwsh ./Linux/Build-AppImage.ps1 -SquashfsTool /usr/bin/mksquashfs
```

The Linux packager verifies a pinned official AppImage runtime digest and includes dependency notices. A changed upstream runtime deliberately stops packaging until its new digest has been reviewed. Build outputs and user data do not belong in source control.

## Attribution and license

The Windows fork started from mRemoteNG commit `25ef453aaf18f292478dd41e5bb26798ff504312`. Copyright and contributor notices from mRemoteNG and bundled components are retained. MGRemote modifications and the Linux prototype are distributed under [GPL-2.0](COPYING.txt); third-party components retain their respective licenses. Release source archives provide the corresponding source for each published package.