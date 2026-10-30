// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using mRemoteNG.Security;

namespace mRemoteNG.UI.Forms;

internal sealed class AnyDeskForm : Form
{
    public AnyDeskForm(string suggestedTarget = "")
    {
        Text = "MGRemote — AnyDesk (optionnel)";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 300);
        DesktopAppearance.Configure(this, new Size(520, 300));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 6, AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var help = new Label { AutoSize = true, MaximumSize = new Size(550, 0),
            Text = "Ouvre une session dans le client AnyDesk externe. Installez un client officiel ou sélectionnez votre client d’entreprise approuvé. MGRemote ne stocke ni ne transmet de mot de passe AnyDesk." };
        layout.Controls.Add(help, 0, 0); layout.SetColumnSpan(help, 2);
        layout.Controls.Add(new Label { Text = "ID ou alias AnyDesk", AutoSize = true, Margin = new Padding(0, 16, 0, 4) }, 0, 1);
        var target = new TextBox { Name = "AnyDeskTarget", Dock = DockStyle.Fill, MaxLength = 129,
            Text = AnyDeskLauncher.IsValidTarget(suggestedTarget) ? suggestedTarget : "" };
        layout.Controls.Add(target, 0, 2); layout.SetColumnSpan(target, 2);
        var executable = new TextBox { Name = "AnyDeskExecutable", Dock = DockStyle.Fill, Text = AnyDeskLauncher.FindInstalledClient() };
        var browse = new Button { Text = "Parcourir…", AutoSize = true };
        browse.Click += (_, _) => {
            using var picker = new OpenFileDialog { Title = "Client AnyDesk approuvé", Filter = "AnyDesk (*.exe)|AnyDesk*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) == DialogResult.OK) executable.Text = picker.FileName;
        };
        layout.Controls.Add(executable, 0, 3); layout.Controls.Add(browse, 1, 3);
        var error = new Label { Name = "ValidationError", AutoSize = true, MaximumSize = new Size(550, 0), ForeColor = Color.Firebrick };
        layout.Controls.Add(error, 0, 4); layout.SetColumnSpan(error, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var launch = new Button { Name = "LaunchAnyDesk", Text = "Ouvrir AnyDesk", AutoSize = true };
        launch.Click += (_, _) => {
            try {
                using var process = Process.Start(AnyDeskLauncher.BuildStartInfo(executable.Text.Trim(), target.Text))
                    ?? throw new InvalidOperationException("Impossible de démarrer AnyDesk.");
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { error.Text = ex.Message; }
        };
        var close = new Button { Text = "Fermer", DialogResult = DialogResult.Cancel, AutoSize = true };
        actions.Controls.Add(launch); actions.Controls.Add(close);
        layout.Controls.Add(actions, 0, 5); layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = launch; CancelButton = close;
        layout.ClientSizeChanged += (_, _) => { int width = Math.Max(150, layout.ClientSize.Width - layout.Padding.Horizontal - 24); help.MaximumSize = error.MaximumSize = new Size(width, 0); };
    }
}
