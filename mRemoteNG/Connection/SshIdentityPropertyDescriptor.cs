// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Design;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using mRemoteNG.Connection.Protocol;
using mRemoteNG.Connection.Protocol.Terminal;
using mRemoteNG.Security;
using mRemoteNG.Tools.Attributes;

namespace mRemoteNG.Connection;

// An editor facade over the existing SSHOptions field: no private material and no
// additional connection-file schema are introduced.
internal sealed class SshIdentityPropertyDescriptor() : PropertyDescriptor("SshPrivateKeyPath", new Attribute[] {
    new CategoryAttribute("Authentification SSH"), new DisplayNameAttribute("Clé privée SSH (facultatif)"),
    new DescriptionAttribute("Laissez vide pour le mot de passe / ssh-agent. Choisissez une clé OpenSSH/PEM pour Windows OpenSSH, ou PPK pour PuTTY. Pour un tunnel, configurez la clé du bastion dans sa connexion SSH ; la clé ici concerne la destination."),
    new AttributeUsedInProtocol(ProtocolType.SSH2), new EditorAttribute(typeof(SshIdentityFileEditor), typeof(UITypeEditor)) })
{
    public override Type ComponentType => typeof(ConnectionInfo);
    public override Type PropertyType => typeof(string);
    public override bool IsReadOnly => false;
    public override bool ShouldSerializeValue(object component) => false;
    public override bool CanResetValue(object component) => !string.IsNullOrEmpty((string)GetValue(component));
    public override void ResetValue(object component) => SetValue(component, string.Empty);
    public override object GetValue(object component)
    {
        try {
            var tokens = WindowsSecurityPolicy.GetSafePuttyOptions(((ConnectionInfo)GetInvocationTarget(ComponentType, component)).SSHOptions, false);
            for (int i = 0; i + 1 < tokens.Length; i++) if (tokens[i] == "-i") return tokens[i + 1];
        } catch (ArgumentException) { }
        return string.Empty;
    }
    public override void SetValue(object component, object value)
    {
        var info = (ConnectionInfo)GetInvocationTarget(ComponentType, component);
        string path = Convert.ToString(value)?.Trim() ?? string.Empty;
        if (path.Length > 0) SshProfileStore.ValidateExistingPrivateKey(path, info.TerminalBackend == TerminalBackend.PuttyNg);
        var tokens = WindowsSecurityPolicy.GetSafePuttyOptions(info.SSHOptions, false);
        var remaining = new List<string>();
        for (int i = 0; i < tokens.Length; i++) {
            if (tokens[i] == "-i") { i++; continue; }
            remaining.Add(tokens[i].Any(char.IsWhiteSpace) ? "\"" + tokens[i] + "\"" : tokens[i]);
        }
        if (path.Length > 0) remaining.Add("-i \"" + Path.GetFullPath(path) + "\"");
        info.Inheritance.SSHOptions = false;
        info.SSHOptions = string.Join(" ", remaining);
        OnValueChanged(component, EventArgs.Empty);
    }
}

internal sealed class SshIdentityFileEditor : UITypeEditor
{
    public SshIdentityFileEditor() { }
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;
    public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
    {
        using var picker = new OpenFileDialog { Title = "Clé privée SSH (pas la clé publique .pub)", CheckFileExists = true,
            FileName = value as string ?? string.Empty, Filter = "Clés privées SSH|*.*" };
        return picker.ShowDialog(Form.ActiveForm) == DialogResult.OK ? picker.FileName : value;
    }
}
