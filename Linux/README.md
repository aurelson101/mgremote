# MGRemote Linux Desktop 0.2.17 — development preview

This branch targets the same desktop workflow as MGRemote Windows. It is **not a feature-equivalent release** and must not replace the published Linux package as a parity-complete build.

Implemented here: connection/folder tree preserving XML container paths; configuration below the tree; resizable panes; File/Sessions/View/Tools/Help menus; closeable session tabs. SSH, SFTP command-line, serial and the FreeRDP parent-window path use embedded X11 clients. Xterm provides a real PTY, ANSI terminal and Ctrl+Shift+C/V clipboard bindings. Switching tabs keeps their native hosts mounted. Closing a tab terminates its owned client tree. SSH host-key checks, NLA and certificate verification remain enabled; no plaintext passwords are stored or passed as command-line arguments.

Dependencies: Linux X11/XWayland, Avalonia native libraries, OpenSSH, FreeRDP and picocom for serial. The .NET runtime and xterm (with its terminal-specific libraries) are bundled. OpenSSH, FreeRDP and desktop libraries remain system dependencies. Native Wayland is not supported by the embedding layer. Multimonitor RDP still requires the explicit external-window action.

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

## Development update 0.2.4: connection-tree actions

Right-click the root, a folder or a connection to create a folder or connection, edit/rename, duplicate, connect, use SFTP or delete as applicable. Right-click selects its target before opening the menu. F2 edits/renames; Insert creates a connection. New connections appear immediately as drafts; configure the host/protocol and save in the Configuration panel before connecting. Empty folders are persisted and imported from XML. Renaming a folder updates descendant paths while retaining encrypted credential fields. Folder deletion is limited to empty folders to prevent unintended subtree removal.

The local store now uses a versioned document holding both connections and folders. Existing JSON array profiles remain readable and are converted on the next save. Older AppImages cannot read the new format; back up connections.json before upgrading if you need to downgrade. Invalid/unknown storage versions block writes. GUI tests operate on an isolated temporary profile and cover menu actions, selection, draft validation, renaming, empty-folder persistence and deletion safety.

## Development update 0.2.5: desktop usability

Folder, Windows/RDP, Linux/SSH and serial vector icons identify tree entries; configured usernames are visible alongside connection names (RDP includes the domain). These indicate connection types, not remote OS discovery. Help now opens an offline guide and About dialog. SSH terminal text uses Ctrl+Shift+C/V; files/folders use SFTP. RDP clipboard is enabled per connection and may be restricted by the server; use explicit folder redirection for file access.

Selecting multimonitor RDP and connecting now opens a managed native fullscreen FreeRDP window with /multimon and /f, while its MGRemote tab owns process cleanup. Credentials may be unlocked for direct RDP as with embedded sessions; gateway/MFA handling remains manual. Physical monitor layouts, DPI/hot-plug, server-side clipboard redirection and real remote authentication remain unverified. CI includes a real local X11 clipboard/embedded-PTY test (copy from terminal and paste into terminal), plus external-process shutdown tests; this is not an end-to-end remote-server test. xclip/xdotool are test dependencies only.

## Development update 0.2.6: profiles and connection properties

The desktop retains mRemoteNG’s connection tree, lower property pane and session tabs, with grouped protocol-specific properties, expand/collapse controls and Ctrl+N/Ctrl+S shortcuts outside the terminal. This is a modern Linux implementation of that workflow, not complete feature parity.

Vector icons automatically use explicit imported icon metadata, unambiguous Windows/Linux/network keywords in saved names/hosts/descriptions, then protocol defaults (RDP → Windows, SSH → Linux). No remote OS scan is performed. Select Auto, Windows, Linux, Network or Serial to override the display.

At startup an unencrypted profile offers **Chiffrer le profil** or **Plus tard — continuer sans chiffrement**. The File menu allows later activation. Encryption covers the full connection/folder/settings document using AES-256-GCM and PBKDF2-HMAC-SHA256 (600000 iterations), a random salt and fresh save nonce. The passphrase is never saved; the derived key exists only for the application session and is cleared on close. Unlocking is required at each startup; canceling or entering an incorrect phrase preserves the file and blocks writes. Keep the passphrase securely: recovery is unavailable. Existing imported passwords keep their separate local passphrase. Older applications cannot load the encrypted profile; encryption does not erase previous backups or filesystem snapshots.

SSH properties support an optional OpenSSH public certificate, a private key, an existing OpenSSH bastion alias and one explicit local forward. The forward binds only 127.0.0.1 on a port from 1024 to 65535 and exists while its SSH session remains open; the remote host is resolved from the SSH server. SFTP uses the same identity/certificate/bastion without opening a duplicate local listener. This is not automatic RDP tunnel orchestration. SSH certificates use local absolute paths; PPK keys still need conversion.

RDP resolution can follow the pane, use a fixed size, or open a managed native fullscreen window. Multiple monitors also use a managed native window and ignore fixed-size settings. NLA and certificate checks remain enabled. Live SSH/certificate/bastion/tunnel/RDP authentication and physical monitors still require a real-server pilot; synthetic tests do not establish server compatibility.

## Development update 0.2.7: compact desktop appearance

Neutral gray toolbars, small vector toolbar icons, muted selection colors and compact property-table rows replace the saturated buttons and card-style sections. The tree/configuration sidebar is narrower and resizable; session tabs use normal desktop text sizes and the empty workspace is deliberately quiet. UI rendering is checked at 1280×850 and 900×600; connection, tunnel and encryption behavior is unchanged.

## Development update 0.2.8: profile and client lifecycle

File → Verrouiller le profil closes the application-owned sessions/transfers, clears the active profile key and editor, and requires unlocking again. It is available for encrypted profiles and asks confirmation before discarding unsaved edits. Profile reads are bounded, refuse symbolic-link paths, and run off the UI thread during startup. Existing encrypted bytes must authenticate before replacement; incorrect keys and altered profiles cannot be silently overwritten.

Search is debounced and retains collapsed folders, selection and unsaved editor text. Searching temporarily opens matching branches; clearing it restores the previous tree state.

Explicit external windows now have an owning tab, including SSH and serial. Destination SSH clients disable X11 forwarding, GSSAPI credential delegation, connection multiplexing and post-authentication backgrounding. Existing user-managed bastion masters/configuration remain independent and are not shut down by MGRemote. Host-key verification, NLA and RDP certificate checks remain enabled. Live remote authentication still requires a server pilot.

## Development update 0.2.9: stale-save and tab lifecycle fixes

The editor keeps the revision read from disk and rejects a save if another instance/program has changed or deleted the profile. Restart MGRemote to load the new version; unsaved edits are not written over it. Saving authenticates the existing profile once and compares its revision immediately before replacement instead of decrypting it twice. This detects stale editors and changes observed during save; it is not a cross-process database transaction. The on-disk format is unchanged.

Closing a background tab preserves the active session; closing the active tab selects its neighbor. Manual profile locking waits for application-owned clients and SFTP shutdown before reporting completion, and reports unconfirmed shutdown separately.

## Development update 0.2.10: portable encrypted profiles

To move saved connections to another Linux computer, select **Fichier → Exporter le profil chiffré…**, choose a `.mgremote` file and confirm an export passphrase of at least 12 characters. The export is always AES-256-GCM encrypted, even if the current profile is not. It does not change the active profile or the destination folder’s permissions. Confirming replacement can update an existing encrypted export; unrelated files and the active profile cannot be overwritten.

Copy this file to the other computer and use **Fichier → Importer un profil chiffré…** with its passphrase. Existing saved connections remain; exact duplicates are skipped. If the local profile is unencrypted, **Chiffrer le profil local avec cette phrase** is selected by default and can be unchecked. An already encrypted local profile keeps its existing key/passphrase. Wrong phrases, altered archives and cancellation preserve the local profile. Windows 0.1.15 does not implement this Linux profile format.

Only saved connections, folders and configuration are included. Private SSH key files, certificates, OpenSSH config aliases, shared folders and serial devices are not packaged; their paths/settings are preserved and must be checked on the destination PC. Previously imported connection passwords remain encrypted with their original local secret phrase, which may also be needed when connecting. Share the export passphrase separately from the file and keep it securely: there is no recovery.

## Development update 0.2.11: SSH startup and key selection

The key action immediately opens a dialog with a direct path field, including `~/.ssh/...`; the system file chooser is optional. Invalid paths, public keys, PPK files and unsafe permissions display an inline error. Selection stores a reference to the existing key; save the connection to persist it.

The AppImage includes xterm and its terminal-specific libraries, built for Ubuntu 24.04 x86-64, with pinned SHA-256 package/source hashes from signed Ubuntu indexes. System xterm takes precedence when installed. The bundle includes copyright notices and corresponding source under `usr/bin/terminal`. Packaging requires Linux, Python 3.11+, dpkg-deb and the existing .NET/SquashFS tools. No packages are installed on the build host. Missing session prerequisites are checked before creating a session tab and shown in a dialog.

## Development update 0.2.12: bounded SSH validation and visible session failures

SSH keys and certificates are read through a descriptor opened without following symbolic links in the file or parent directories. Permissions are checked on that descriptor and reads are bounded even if a file grows. Private keys require complete PEM armor with valid base64; encrypted PKCS#8, OpenSSH and legacy encrypted PEM armor remain supported. This validates the container; OpenSSH validates and decrypts the actual key when connecting.

Key validation runs in the background, can be canceled without changing the connection, and repeated clicks reuse the existing dialog. Actions remain visible in a compact dialog, with Enter/Escape support and an accessible path label. Embedded clients that exit before displaying a window or fail to display within ten seconds now show an error pane. Startup polling uses a monotonic clock and reduces its frequency once the client is ready.

## Development update 0.2.13: linked SSH keys and folders

SSH key and certificate paths may contain symbolic links. Permissions and bounded content are checked on the actual opened target, including paths through linked directories. Broken links, loops, invalid key containers and unsafe target permissions are rejected. Selected paths are preserved; no key is copied or modified. Profile files still refuse symbolic links. This fixes the overly strict SSH link restriction introduced in 0.2.12.

## Development update 0.2.14: optional SSH keys, automatic saving and startup profiles

SSH keys remain optional: leave the field empty or use “Sans clé” in the key dialog to clear the explicit key and certificate. OpenSSH may then use a password, an agent or its normal configuration. No private key is required to save a connection.

Valid connection edits are saved automatically after a short pause and before changing the selection or closing. The existing encryption and stale-write guards are preserved. Invalid edits display “Non enregistré” and block selection/closing until corrected; changing profiles offers an explicit discard choice. “Enregistrer” remains available for an immediate save.

At startup, the last successfully opened profile is preselected (only its local path is remembered). Choose a local profile, create an independent profile, restore a JSON/.mgremote/.bak file into a new profile, or remove a profile through confirmation. Removal renames the file to a recoverable .bak archive; restoring never overwrites another profile. A forgotten encryption phrase cannot be recovered, and encrypted backups retain their original phrase. “Fichier → Changer / gérer les profils” saves pending valid changes and stops managed sessions before switching. New profiles are stored beneath the application data directory in profiles/<name>-<unique-id>/connections.json. Existing parent-directory permissions are preserved when opening an external profile.

## Development update 0.2.15: directly entered SSH home paths

Paths such as `~/.ssh/ia` are expanded to the current user’s home directory during validation and SSH/SFTP command construction, including certificate paths. Previously only the file-selection dialog expanded `~/`. Stored profile paths are preserved, and no shell expansion is used.

## Development update 0.2.16: bidirectional clipboard and file workflows

SSH text keeps Ctrl+Shift+C/V in the terminal. Each SSH session now offers a direct SFTP shortcut. The SFTP pane accepts native clipboard files/folders, drag-and-drop, multiple file selection and one local path per line (up to 100 elements). Folder mode is selected when a pasted/dropped directory is present. Multiple uploads require an existing remote directory ending with `/`; duplicate source basenames, including case-only differences, are rejected to prevent collisions. Pasting or dropping only prepares the form: sending still requires explicit overwrite confirmation. Downloads support recursive folders and use a new private reception directory. Inputs are frozen during preparation/transfer; close the transfer before preparing another.

RDP text/image clipboard remains available. The explicit bidirectional file mode requires the xfreerdp3 executable and uses [FreeRDP 3 clipboard direction options](https://github.com/FreeRDP/FreeRDP/blob/3.0.0/client/common/cmdline.c). With FreeRDP 3, files are disabled when that mode is unchecked; older clients keep their existing global clipboard behavior. For folders and a consistent file workflow, configure an explicit shared local directory and access `\\tsclient\MGRemote` from the remote Windows Explorer. The session toolbar copies this path or opens the local directory. Only the selected directory is shared. Server policies and client support may limit clipboard formats; remote RDP/SSH servers still require live validation.

## Development update 0.2.17: mouse copy/paste

SSH terminals now copy a left-button text selection to both PRIMARY and CLIPBOARD when the button is released. Right-click pastes CLIPBOARD without appending Enter; Ctrl+Shift+C/V and normal middle-button behavior remain available. Hold Shift when a remote application captures mouse events, as described in the [xterm manual](https://invisible-island.net/xterm/manpage/xterm.html). RDP keeps the remote application’s native right-click Copy/Paste menus and the existing clipboard settings.

SFTP fields now expose right-click text and file/folder commands. Copy local files or the latest received directory to the system clipboard, then paste through the local file manager. The received directory can contain partial files if the transfer was interrupted. These clipboard actions do not start transfers.
