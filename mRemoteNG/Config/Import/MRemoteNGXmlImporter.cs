using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using mRemoteNG.App;
using mRemoteNG.Config.DataProviders;
using mRemoteNG.Config.Serializers.ConnectionSerializers.Xml;
using mRemoteNG.Container;
using mRemoteNG.Messages;


namespace mRemoteNG.Config.Import
{
    [SupportedOSPlatform("windows")]
    // ReSharper disable once InconsistentNaming
    public class MRemoteNGXmlImporter : IConnectionImporter<string>
    {
        public void Import(string fileName, ContainerInfo destinationContainer)
        {
            // MGRemote 2026-10-29: validate before mutating the destination tree.
            if (destinationContainer == null)
                throw new System.ArgumentNullException(nameof(destinationContainer), "Select a destination folder before importing.");
            if (string.IsNullOrWhiteSpace(fileName))
                throw new System.ArgumentException("Select a connection XML file to import.", nameof(fileName));

            if (!File.Exists(fileName))
                throw new FileNotFoundException("The connection XML file does not exist.", fileName);

            FileDataProvider dataProvider = new(fileName);
            string xmlString = dataProvider.Load();
            XmlConnectionsDeserializer xmlConnectionsDeserializer = new(
                () => mRemoteNG.Tools.MiscTools.PasswordDialog(Path.GetFileName(fileName), false));
            Tree.ConnectionTreeModel connectionTreeModel = xmlConnectionsDeserializer.Deserialize(xmlString, true);
            ContainerInfo importedRoot = connectionTreeModel?.RootNodes.FirstOrDefault();
            if (importedRoot == null)
                throw new InvalidDataException("The connection XML could not be imported. The file may be empty, or its password verification was cancelled or failed.");

            ContainerInfo rootImportContainer = new() { Name = Path.GetFileNameWithoutExtension(fileName)};
            rootImportContainer.AddChildRange(importedRoot.Children.ToArray());
            destinationContainer.AddChild(rootImportContainer);
        }
    }
}
