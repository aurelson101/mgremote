using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;
using mRemoteNG.Messages;
using mRemoteNG.Resources.Language;

namespace mRemoteNG.App.Initialization
{
    [SupportedOSPlatform("windows")]
    public class StartupDataLogger(MessageCollector messageCollector)
    {
        private readonly MessageCollector _messageCollector = messageCollector ?? throw new ArgumentNullException(nameof(messageCollector));

        public void LogStartupData()
        {
            LogApplicationData();
            LogCmdLineArgs();
            LogSystemData();
            LogClrData();
            LogCultureData();
        }

        private void LogSystemData()
        {
            string osData = GetOperatingSystemData();
            string architecture = GetArchitectureData();
            string[] nonEmptyData = Array.FindAll(new[] {osData, architecture}, s => !string.IsNullOrEmpty(s));
            string data = string.Join(" ", nonEmptyData);
            _messageCollector.AddMessage(MessageClass.InformationMsg, data, true);
        }

        private string GetOperatingSystemData()
        {
            return RuntimeInformation.OSDescription;
        }

        private string GetArchitectureData()
        {
            return RuntimeInformation.OSArchitecture.ToString();
        }

        private void LogApplicationData()
        {
            string data = $"{Application.ProductName} {Application.ProductVersion}";
            if (Runtime.IsPortableEdition)
                data += $" {Language.PortableEdition}";
            data += " starting.";
            _messageCollector.AddMessage(MessageClass.InformationMsg, data, true);
        }

        private void LogCmdLineArgs()
        {
            string data = $"Command Line: {string.Join(" ", Environment.GetCommandLineArgs())}";
            _messageCollector.AddMessage(MessageClass.InformationMsg, data, true);
        }

        private void LogClrData()
        {
            string data = $"Microsoft .NET CLR {Environment.Version}";
            _messageCollector.AddMessage(MessageClass.InformationMsg, data, true);
        }

        private void LogCultureData()
        {
            string data = $"System Culture: {Thread.CurrentThread.CurrentUICulture.Name}/{Thread.CurrentThread.CurrentUICulture.NativeName}";
            _messageCollector.AddMessage(MessageClass.InformationMsg, data, true);
        }
    }
}
