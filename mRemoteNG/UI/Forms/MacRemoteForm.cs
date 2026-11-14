using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace mRemoteNG.UI.Forms;

internal sealed class MacRemoteForm : Form
{
    public MacRemoteForm()
    {
        Text = "MGRemote — Mac / Korben Remote Desktop";
        ClientSize = new Size(650, 240); StartPosition = FormStartPosition.CenterParent;
        DesktopAppearance.Configure(this, new Size(520, 240));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(600, 0), Text = "Sur le Mac, activez l’accès web de l’agent Korben Remote Desktop et collez son lien temporaire HTTPS. Vérifiez l’empreinte du certificat affichée par l’agent avant d’accepter dans le navigateur. Le lien n’est pas enregistré." }, 0, 0);
        var address = new TextBox { Name = "MacRemoteUrl", Dock = DockStyle.Fill, PlaceholderText = "https://…" }; layout.Controls.Add(address, 0, 1);
        var open = new Button { Text = "Ouvrir dans le navigateur", AutoSize = true }; layout.Controls.Add(open, 0, 2);
        open.Click += (_, _) => {
            if (!Uri.TryCreate(address.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host)) {
                MessageBox.Show(this, "Indiquez le lien HTTPS fourni par l’agent, sans identifiants dans l’adresse."); return;
            }
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); address.Clear(); }
            catch (Exception) { MessageBox.Show(this, "Impossible d’ouvrir le navigateur. Vérifiez son installation et le lien de l’agent."); }
        };
        Controls.Add(layout);
    }
}
