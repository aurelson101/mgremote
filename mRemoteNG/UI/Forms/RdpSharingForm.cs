// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;

namespace mRemoteNG.UI.Forms;

internal sealed class RdpSharingForm : Form
{
    public RdpSharingForm(ConnectionInfo connection)
    {
        Text = "MGRemote — Presse-papiers et fichiers RDP";
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 440); DesktopAppearance.Configure(this, new Size(520, 380));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var clipboard = new CheckBox { Name = "RdpClipboard", Text = "Presse-papiers bidirectionnel (texte et images)", AutoSize = true, Checked = connection.RedirectClipboard };
        var files = new CheckBox { Name = "RdpFiles", Text = "Autoriser les fichiers et dossiers (presse-papiers / lecteurs)", AutoSize = true,
            Checked = connection.RedirectDiskDrives != RDPDiskDrives.None };
        var help = new Label { AutoSize = true, MaximumSize = new Size(570, 0),
            Text = "Facultatif : cochez les lecteurs à rendre accessibles via " + @"\\tsclient" + " dans la session. Un lecteur coché expose son contenu accessible à la session distante.\nReconnexion nécessaire. Les stratégies du serveur/client peuvent interdire ces fonctions. Pour les gros fichiers, utilisez les lecteurs redirigés." };
        var drives = new CheckedListBox { Name = "RdpDrives", Dock = DockStyle.Fill, CheckOnClick = true, Enabled = files.Checked };
        foreach (var drive in DriveInfo.GetDrives()) {
            bool selected = connection.RedirectDiskDrives == RDPDiskDrives.All ||
                connection.RedirectDiskDrives == RDPDiskDrives.Local && drive.DriveType is DriveType.Fixed or DriveType.Removable ||
                connection.RedirectDiskDrives == RDPDiskDrives.Custom && (connection.RedirectDiskDrivesCustom ?? "").ToUpperInvariant().Contains(char.ToUpperInvariant(drive.Name[0]));
            drives.Items.Add(drive.Name, selected);
        }
        files.CheckedChanged += (_, _) => { drives.Enabled = files.Checked; if (files.Checked) clipboard.Checked = true; };
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var apply = new Button { Name = "ApplyRdpSharing", Text = "Appliquer (reconnexion)", AutoSize = true };
        apply.Click += (_, _) => {
            connection.Inheritance.RedirectClipboard = connection.Inheritance.RedirectDiskDrives = connection.Inheritance.RedirectDiskDrivesCustom = false;
            connection.RedirectClipboard = clipboard.Checked;
            connection.RedirectDiskDrives = files.Checked ? RDPDiskDrives.Custom : RDPDiskDrives.None;
            connection.RedirectDiskDrivesCustom = files.Checked
                ? string.Join(",", drives.CheckedItems.Cast<string>().Select(name => char.ToUpperInvariant(name[0]))) : string.Empty;
            DialogResult = DialogResult.OK;
        };
        var cancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, AutoSize = true };
        actions.Controls.Add(apply); actions.Controls.Add(cancel);
        layout.Controls.Add(clipboard); layout.Controls.Add(files); layout.Controls.Add(help); layout.Controls.Add(drives); layout.Controls.Add(actions);
        Controls.Add(layout); AcceptButton = apply; CancelButton = cancel;
        layout.ClientSizeChanged += (_, _) => help.MaximumSize = new Size(Math.Max(150, layout.ClientSize.Width - 48), 0);
    }
}
