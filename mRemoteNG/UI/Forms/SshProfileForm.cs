// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;

namespace mRemoteNG.UI.Forms
{
    public sealed class SshProfileForm : Form
    {
        private readonly TextBox alias = new();
        private readonly TextBox hostname = new();
        private readonly TextBox username = new();
        private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 22 };
        private readonly TextBox identity = new();
        private readonly TextBox certificate = new();
        private readonly TextBox jumpHost = new();
        private readonly ComboBox keyType = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly FlowLayoutPanel actions = new() { AutoSize = true, Dock = DockStyle.Fill };
        private bool busy;
        public string CreatedAlias { get; private set; }
        public int CreatedPort { get; private set; }

        public SshProfileForm() : this(null) { }

        public SshProfileForm(string identityPath)
        {
            identity.Text = identityPath ?? string.Empty;
            Text = "MGRemote - SSH keys and profiles";
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            DesktopAppearance.Configure(this, new Size(620, 480));
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(720, 530);
            MaximizeBox = true;
            keyType.Items.AddRange(new object[] { "ed25519", "ed25519-sk (FIDO2)" });
            keyType.SelectedIndex = 0;
            var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            var help = new Label { Text = "Create an OpenSSH profile or a key. Enter a strong passphrase when prompted. Private keys stay on this PC. Verify server fingerprints independently.", AutoSize = true, MaximumSize = new Size(620, 0) };
            layout.Controls.Add(help, 0, 0);
            layout.SetColumnSpan(help, 3);
            AddRow(layout, "Profile alias", alias, 1);
            AddRow(layout, "Server / IP address", hostname, 2);
            AddRow(layout, "SSH username", username, 3);
            AddRow(layout, "Port", port, 4);
            AddRow(layout, "Private key (optional)", identity, 5, () => Browse(identity));
            AddRow(layout, "SSH certificate", certificate, 6, () => Browse(certificate));
            AddRow(layout, "Jump host alias", jumpHost, 7);
            AddRow(layout, "New key type", keyType, 8);
            var save = new Button { Text = "Save profile", AutoSize = true };
            save.Click += async (_, _) => await RunHelper("Profile");
            var generate = new Button { Text = "Create key...", AutoSize = true };
            generate.Click += async (_, _) =>
            {
                string sshDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
                using var picker = new SaveFileDialog { Title = "Choose a NEW private-key file", FileName = "mgremote_ed25519", InitialDirectory = Directory.Exists(sshDirectory) ? sshDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                identity.Text = picker.FileName;
                await RunHelper("GenerateKey");
            };
            actions.Controls.Add(save);
            actions.Controls.Add(generate);
            var addToAgent = new Button { Text = "Charger dans ssh-agent…", AutoSize = true };
            addToAgent.Click += async (_, _) => await RunHelper("AddToAgent");
            actions.Controls.Add(addToAgent);
            layout.Controls.Add(actions, 0, 9);
            layout.SetColumnSpan(actions, 3);
            var guidance = new Label { Text = "After saving: select SSH2 / WindowsTerminal, use the profile alias as hostname, leave username empty and use the configured port.\n\nKey creation opens a Windows console so you enter the passphrase directly in ssh-keygen. FIDO2 requires a supported OpenSSH build and token.", AutoSize = true, MaximumSize = new Size(620, 0), Padding = new Padding(0, 10, 0, 0) };
            layout.Controls.Add(guidance, 0, 10);
            layout.SetColumnSpan(guidance, 3);
            viewport.Controls.Add(layout);
            Controls.Add(viewport);
            void WrapDescriptions()
            {
                int width = Math.Max(180, viewport.ClientSize.Width - viewport.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
                help.MaximumSize = new Size(width, 0);
                guidance.MaximumSize = new Size(width, 0);
            }
            viewport.ClientSizeChanged += (_, _) => WrapDescriptions();
            WrapDescriptions();
            FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        }

        private static void AddRow(TableLayoutPanel layout, string label, Control control, int row, Action browse = null)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            control.Dock = DockStyle.Fill;
            layout.Controls.Add(control, 1, row);
            if (browse == null) return;
            var button = new Button { Text = "Browse...", AutoSize = true };
            button.Click += (_, _) => browse();
            layout.Controls.Add(button, 2, row);
        }

        private void Browse(TextBox destination)
        {
            using var picker = new OpenFileDialog { CheckFileExists = true, Title = "Choose an existing SSH file" };
            if (picker.ShowDialog(this) == DialogResult.OK) destination.Text = picker.FileName;
        }

        private async Task RunHelper(string action)
        {
            if (busy) return;
            busy = true;
            Controls[0].Enabled = false;
            UseWaitCursor = true;
            try
            {
                string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (action == "AddToAgent")
                {
                    string path = identity.Text.Trim();
                    mRemoteNG.Security.SshProfileStore.ValidateExistingPrivateKey(path);
                    string sshAdd = Path.Combine(system, "OpenSSH", "ssh-add.exe");
                    if (!File.Exists(sshAdd)) throw new FileNotFoundException("Installez le Client OpenSSH Windows.");
                    var agentStart = new ProcessStartInfo(sshAdd) { UseShellExecute = false, CreateNoWindow = false };
                    agentStart.ArgumentList.Add(path);
                    using var agentProcess = Process.Start(agentStart) ?? throw new InvalidOperationException("Impossible de démarrer ssh-add.");
                    await agentProcess.WaitForExitAsync();
                    if (agentProcess.ExitCode != 0) throw new InvalidOperationException("La clé n’a pas été chargée. Vérifiez que le service Windows ssh-agent est démarré, ou utilisez SSH — console Windows. Aucun service n’a été modifié.");
                    MessageBox.Show(this, "Clé chargée dans votre agent Windows. La phrase secrète n’est pas transmise à MGRemote.", "MGRemote SSH", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (action == "Profile")
                {
                    // Snapshot controls on the UI thread; only filesystem work runs on the worker.
                    var values = (Alias: alias.Text.Trim(), Host: hostname.Text.Trim(), User: username.Text.Trim(), Port: (int)port.Value,
                        Key: identity.Text.Trim(), Certificate: certificate.Text.Trim(), Jump: jumpHost.Text.Trim());
                    string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
                    await Task.Run(() => mRemoteNG.Security.SshProfileStore.CreateProfile(values.Alias, values.Host, values.User,
                        values.Port, values.Key, values.Certificate, values.Jump, directory));
                    CreatedAlias = values.Alias;
                    CreatedPort = values.Port;
                    MessageBox.Show(this, "Profile saved. Use the alias as hostname in an SSH2 / WindowsTerminal connection.", "MGRemote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string keygen = Path.Combine(system, "OpenSSH", "ssh-keygen.exe");
                if (!File.Exists(keygen)) throw new FileNotFoundException("Install the Windows OpenSSH Client optional feature first.");
                mRemoteNG.Security.SshProfileStore.ValidateNewKeyPath(identity.Text.Trim());
                var start = new ProcessStartInfo(keygen)
                {
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                foreach (string value in new[] { "-t", keyType.SelectedIndex == 1 ? "ed25519-sk" : "ed25519", "-a", "100", "-f", identity.Text.Trim() }) start.ArgumentList.Add(value);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Windows ssh-keygen.");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new InvalidOperationException("Key creation failed or was cancelled. No existing key was replaced.");
                MessageBox.Show(this, "Key created. Keep the private file on this PC; distribute only the .pub file.", "MGRemote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "MGRemote SSH", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { busy = false; if (!IsDisposed) { Controls[0].Enabled = true; UseWaitCursor = false; } }
        }
    }
}
