# MGRemote Linux Desktop 0.2.2 â€” work in progress

This branch targets the same desktop workflow as MGRemote Windows. It is **not a feature-equivalent release** and must not replace the published Linux package as a parity-complete build.

Implemented here: connection/folder tree preserving XML container paths; configuration below the tree; resizable panes; File/Sessions/View/Tools/Help menus; closeable session tabs. SSH, SFTP command-line, serial and the FreeRDP parent-window path use embedded X11 clients. Xterm provides a real PTY, ANSI terminal and Ctrl+Shift+C/V clipboard bindings. Switching tabs keeps their native hosts mounted. Closing a tab terminates its owned client tree. SSH host-key checks, NLA and certificate verification remain enabled; no passwords are stored or passed as command-line arguments.

Dependencies: Linux X11/XWayland, Avalonia native libraries, xterm, OpenSSH, FreeRDP and picocom for serial. The .NET runtime is bundled. Native Wayland is not supported by the embedding layer. Multimonitor RDP still requires the explicit external-window action.

Incomplete parity: embedded VNC, advanced RDP gateway/monitor options, interactive bastion management, encrypted XML/vault migration and optional AnyDesk integration. Some platform-specific Windows behavior will require native Linux equivalents rather than Windows DLLs.

Validation: synthetic import/argument/security checks and Ubuntu/Xvfb tests with two actual embedded X11 terminal processes. The tests exercise tab switching, native child-window creation and shutdown; picocom is also tested with pseudo-terminals. Real server authentication, FreeRDP embedding against a real server, physical serial adapters, monitor hot-plug and production desktops remain unverified. See the workflow for the exact tested commit.

Build: .NET 10, PowerShell 7 and squashfs-tools are required. Run `pwsh ./Linux/Build-AppImage.ps1 -SquashfsTool /usr/bin/mksquashfs` from the repository root. AppImage/source hashes and dependency notices are included. Development packages are unsigned.
## Development update 0.2.1

The SFTP action now opens a graphical upload/download panel with file/folder pickers, recursive transfer, overwrite confirmation, embedded progress/authentication and cancellation. Downloads always use a fresh private subdirectory. Partial files remain after cancellation. OpenSSH 9+ is required so scp uses SFTP by default; legacy SCP mode and disabled filename checks are not enabled. Symlinks in upload trees are refused; validation runs on a worker and can be cancelled.

Native session maintenance is driven by X11 structural events and parent size changes. Idle children are not repeatedly queried/resized. Tests check actual operating-system PIDs after closure, preservation across tab changes, idle request counters and invalid transfer input. Live remote file transfers still require validation. This is an unsigned development build, not complete Windows feature parity.

## Development update 0.2.2

Connections can use an optional SSH bastion alias defined in ~/.ssh/config (including its own key/user/port), shared by SSH and SFTP. RDP can explicitly share one selected local folder, read/write; disabled by default. The server can access it as \\tsclient\MGRemote. Connection duplication creates an editable copy without changing the source. Editing preserves imported folder names containing the display separator. Managed RDP/VNC bastion tunnels remain incomplete.

The next Linux development changes group connection properties into collapsible sections and import description, inherited host, clipboard, console session, color depth and audio redirection from mRemoteNG XML. Invalid explicit ports and boolean values abort import instead of silently substituting defaults. Encrypted files/secrets, unsupported protocols and other unmapped Windows options are still not fully compatible.
