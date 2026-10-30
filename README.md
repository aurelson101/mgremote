# MGRemote

MGRemote is an independent remote-connection manager derived from [mRemoteNG](https://github.com/mRemoteNG/mRemoteNG). It provides a Windows desktop edition and a separate Linux prototype. This project is not affiliated with or endorsed by the upstream mRemoteNG team.

## Downloads

| Platform | Current package | Status |
| --- | --- | --- |
| Windows x64 | [MSI 0.1.14](https://github.com/aurelson101/mgremote/releases/tag/windows-v0.1.14) | Unsigned preview |
| Linux x86-64 | [AppImage 0.1.1](https://github.com/aurelson101/mgremote/releases/tag/linux-v0.1.1) | Unsigned prototype; separate session windows |

Download the installer or AppImage from the matching release. SHA-256 manifests and corresponding source archives accompany the packages. The editions have independent version numbers and are not feature-equivalent.

## Windows edition

- Tabbed RDP, SSH and VNC connections, with a separate connection-properties panel.
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
msiexec /i "MGRemote-0.1.14-win-x64-preview.msi" /qn /norestart
msiexec /x "MGRemote-0.1.14-win-x64-preview.msi" /qn /norestart
```

Removal preserves user settings by default. `PURGEUSERDATA=1` explicitly requests cleanup of app-owned data for the current interactive user. It does not erase operating-system or antivirus records, other users' profiles, documents or personal SSH keys. See [Windows implementation notes](MGREMOTE-WINDOWS.md).

## Linux prototype

The Linux edition uses Avalonia and a bundled .NET runtime. It provides connection creation/editing, local metadata storage, XML metadata import, optional SSH keys, interactive SFTP, RDP clipboard selection and multiple-monitor requests. SSH/SFTP use the installed Linux terminal; RDP uses the installed FreeRDP client. Sessions open in separate windows, not integrated tabs.

```sh
chmod +x MGRemote-Linux-0.1.1-x86_64.AppImage
./MGRemote-Linux-0.1.1-x86_64.AppImage
```

Install OpenSSH Client, FreeRDP, a supported terminal (GNOME Terminal, Konsole, XFCE Terminal or xterm), and the [Avalonia Linux native dependencies](https://docs.avaloniaui.net/docs/deployment/linux) using your distribution's package manager. An X11/XWayland desktop is required by this prototype. .NET is bundled; the remote clients and desktop libraries are not. If FUSE is unavailable, try `./MGRemote-Linux-0.1.1-x86_64.AppImage --appimage-extract-and-run`.

XML import does not migrate passwords, Windows key paths, Windows vault data or arbitrary SSH options. Protocol, port, username and domain inheritance is resolved from parent folders. Select an existing Linux OpenSSH/PEM key with private permissions, then review the connection before use. Existing OpenSSH aliases may define ProxyJump. See [Linux instructions](Linux/README.md).

## Security and validation status

These are unsigned development releases. Antivirus results describe a particular local scan, not a guarantee against all detections or SmartScreen warnings. No antivirus exclusions or certificate-validation bypasses are required or recommended.

Windows validation includes targeted security checks, synthetic XML/RDP imports, GUI lifecycle checks, actual offline RDP ActiveX redirection settings, ConPTY terminal rendering and input, context-menu lifecycle, owned-process cleanup and MSI table/maintenance checks. The release includes its Sophos scan result.

Linux validation uses synthetic metadata/argument/import checks. The manually triggered [Linux workflow](.github/workflows/mgremote-linux.yml) builds the AppImage, runs its self-test on Ubuntu and renders its GUI under Xvfb. Check the workflow result and the release manifest for the exact tested environment.

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