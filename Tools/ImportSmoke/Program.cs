// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text;

internal static class Program
{
    private static Assembly app;
    private static object Call(object target, MethodInfo method, params object[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
    }
    private static object New(string name) => Activator.CreateInstance(app.GetType(name, true));
    private static Exception Rejected(Action action)
    {
        try { action(); } catch (Exception ex) { return ex; }
        throw new InvalidOperationException("Invalid import was accepted.");
    }
    private static int Children(object container) => (int)container.GetType().GetProperty("Children").GetValue(container).GetType().GetProperty("Count").GetValue(container.GetType().GetProperty("Children").GetValue(container));
    [STAThread]
    private static void Main(string[] args)
    {
        string payload = Path.GetFullPath(args[0]);
        string fixture = Path.Combine(Path.GetFullPath(args[1]), "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            foreach (string folder in new[] { payload, Path.Combine(payload, "Assemblies") }) {
                string path = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
        app = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(payload, "MGRemote.dll"));
        var runtime = app.GetType("mRemoteNG.App.Runtime", true);
        object service = runtime.GetProperty("ConnectionsService").GetValue(null);
        var loaded = service.GetType().GetProperty("IsConnectionsFileLoaded");
        loaded.SetValue(service, true);
        var xmlType = app.GetType("mRemoteNG.Config.Serializers.ConnectionSerializers.Xml.XmlConnectionsDeserializer", true);
        object parser = Activator.CreateInstance(xmlType, new object[] { null });
        var deserialize = xmlType.GetMethod("Deserialize", new[] { typeof(string), typeof(bool) });
        try {
            Rejected(() => Call(parser, deserialize, "<Connections ConfVersion=\"1.0\"", true));
            if (!(bool)loaded.GetValue(service)) throw new InvalidOperationException("A failed XML import disabled saving of the currently open connections file.");
            object destination = New("mRemoteNG.Container.ContainerInfo");
            object importer = New("mRemoteNG.Config.Import.MRemoteNGXmlImporter");
            var import = importer.GetType().GetMethod("Import");
            string empty = Path.Combine(fixture, "empty.xml");
            File.WriteAllText(empty, "");
            Exception emptyError = Rejected(() => Call(importer, import, empty, destination));
            if (emptyError is NullReferenceException || Children(destination) != 0) throw new InvalidOperationException("Empty XML import must fail clearly without changing the destination.");
            string xml = Path.Combine(fixture, "synthetic.xml");
            File.WriteAllText(xml, "<Connections Name=\"Synthetic\" ConfVersion=\"1.0\"><Node Name=\"Import test\" Hostname=\"example.invalid\" Protocol=\"RDP\" Type=\"Connection\" /></Connections>");
            Call(importer, import, xml, destination);
            if (Children(destination) != 1 || !(bool)loaded.GetValue(service)) throw new InvalidOperationException("Valid XML import failed or changed the open-file state.");
            // Public synthetic fixture authenticator from the upstream AES-GCM test data.
            string modern = Path.Combine(fixture, "modern.xml");
            File.WriteAllText(modern, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Connections Name=\"Synthetic\" ConfVersion=\"2.8\" EncryptionEngine=\"AES\" BlockCipherMode=\"GCM\" KdfIterations=\"1000\" FullFileEncryption=\"False\" Protected=\"8LmIO3+MWBY0zTmfjfOEdCGxhTAwnlohb1veTGNZFt6lAYvY2UOzWyjVzkx6V93smpbP0ZOuexN15u7rvwJEjawC\"><Node Name=\"Modern import test\" Hostname=\"example.invalid\" Protocol=\"RDP\" Type=\"Connection\" Id=\"822f50c2-e507-4420-b229-b0925d61eb3e\" /></Connections>");
            Call(importer, import, modern, destination);
            if (Children(destination) != 2 || !(bool)loaded.GetValue(service)) throw new InvalidOperationException("AES-GCM XML import failed.");
            string unsafeXml = Path.Combine(fixture, "doctype.xml");
            File.WriteAllText(unsafeXml, "<!DOCTYPE Connections [<!ENTITY blocked SYSTEM 'file:///C:/mgremote/artifacts/nonexistent-test-file'>]><Connections Name=\"Synthetic\" ConfVersion=\"1.0\">&blocked;</Connections>");
            Rejected(() => Call(importer, import, unsafeXml, destination));
            if (Children(destination) != 2 || !(bool)loaded.GetValue(service)) throw new InvalidOperationException("Rejected DTD import changed the destination or disabled saving.");
            object rdpImporter = New("mRemoteNG.Config.Import.RemoteDesktopConnectionImporter");
            string rdp = Path.Combine(fixture, "synthetic.rdp");
            File.WriteAllText(rdp, "full address:s:example.invalid:3390\r\nusername:s:synthetic\r\nauthentication level:i:1\r\nenablecredsspsupport:i:1\r\n", Encoding.Unicode);
            Call(rdpImporter, rdpImporter.GetType().GetMethod("Import"), rdp, destination);
            if (Children(destination) != 3) throw new InvalidOperationException("UTF-16 RDP import failed.");
            Console.WriteLine("PASS: invalid/empty/DTD XML preserves the open file and destination; legacy XML, AES-GCM XML and UTF-16 RDP imports succeed. No remote connections were made.");
        } finally {
            // Only synthetic, individually named files in the freshly created fixture are removed.
            foreach (string name in new[] { "empty.xml", "synthetic.xml", "modern.xml", "doctype.xml", "synthetic.rdp" }) File.Delete(Path.Combine(fixture, name));
            Directory.Delete(fixture, false);
        }
    }
}
