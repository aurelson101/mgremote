# MGRemote Linux Desktop 0.2.3 â€” work in progress

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

## Encrypted import on the development branch

XML full-file and individual-password import supports AES, Twofish and Serpent with GCM/EAX, using the upstream salt/nonce/authentication layout and PBKDF2-HMAC-SHA1 compatibility. Legacy Rijndael and CCM are explicitly unsupported. XML source KDF counts are restricted to 1000–1000000; secret imports have a 64-secret / 10-million-source-iteration budget per operation. Unsupported protocols and unmapped attributes are still not fully compatible.

After choosing an XML, enter its master password (blank uses mRemoteNG's known default key). Password import is optional. When selected, choose and confirm a distinct local passphrase of at least 12 characters. Each connection password is reencrypted with AES-256-GCM, a fresh 16-byte salt and 12-byte nonce, PBKDF2-HMAC-SHA256 with 600000 iterations and an authenticated version marker. Only the encrypted envelope enters connections.json; neither master passphrase is persisted. The source file remains unchanged. Back up the local passphrase securely: there is no recovery.

Select a connection and choose **Secret importé…** to unlock its password temporarily. It remains masked until explicitly shown; nothing is automatically copied or sent to SSH/RDP/SFTP. Automatic credential injection, gateway passwords/access tokens and a desktop keyring are not implemented. Managed plaintext strings can remain in process memory until garbage collection; closing the dialog clears its controls but does not guarantee memory erasure.

RDP gateway hostname/port and direct/detect usage are editable and imported from RDGatewayUsageMethod/RDGatewayHostname. Gateway authentication remains interactive; certificate validation and NLA remain enabled. Live gateway connections still require testing.

## Development update 0.2.3: use imported secrets

At connection time, unlock the imported password with the local passphrase or choose manual authentication. This also applies when starting a graphical SFTP transfer. Credentials are delivered once through a private Unix-domain socket (0700 parent, 0600 socket, random capability token), expire after 90 seconds and are removed on cancellation/closure. Passwords are absent from arguments, environment variables and plaintext files. The environment carries a temporary local IPC capability, not the password. Processes with the same UID and root remain within the local trust boundary. Managed plaintext strings remain subject to garbage collection.

SSH/SFTP uses a bundled OpenSSH askpass helper, allows one password attempt, retains public-key authentication and requires StrictHostKeyChecking=yes. The broker only answers the exact destination user/host password prompt. Unknown hosts, aliases resolving to another prompt, bastion passwords, key passphrases and interactive/MFA prompts require the manual connection path. First connect manually and verify the server fingerprint through a trusted channel.

Direct RDP uses FreeRDP /from-stdin:force through a child process with a private stdin pipe; the username must be configured. Certificate verification and NLA remain enabled. Gateway credentials and external/multimonitor connections remain manual. Offline tests exercise the actual askpass helper and stdin delivery to a synthetic local client, refusal of invalid tokens/wrong targets/key prompts, restrictive permissions and socket cleanup. Real server authentication still requires validation. This section supersedes the earlier manual-only credential description.
