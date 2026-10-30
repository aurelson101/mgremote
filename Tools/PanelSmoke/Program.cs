// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string payload = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (string folder in new[] { payload, Path.Combine(payload, "Assemblies") })
            {
                string path = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(payload, "MGRemote.dll"));
        var runtime = assembly.GetType("mRemoteNG.App.Runtime", true);
        var windows = runtime.GetProperty("WindowList");
        object originalList = windows.GetValue(null) ?? throw new InvalidOperationException("WindowList is null before the startup layout.");
        var messages = runtime.GetProperty("MessageCollector");
        if (messages.GetValue(null) == null) messages.SetValue(null, Activator.CreateInstance(messages.PropertyType));
        var adder = Activator.CreateInstance(assembly.GetType("mRemoteNG.UI.Panels.PanelAdder", true));
        var add = adder.GetType().GetMethod("AddPanel");
        // The isolated process owns the dock host; do not dispose child panes outside its message loop.
        var general = (Form)add.Invoke(adder, new object[] { "General", true });
        var second = (Form)add.Invoke(adder, new object[] { "Regression panel", true });
        if (general == null || second == null) throw new InvalidOperationException("Adding a connection panel failed.");
        if (!ReferenceEquals(originalList, windows.GetValue(null))) throw new InvalidOperationException("Startup replaced the panel registry.");
        bool generalTracked = false, secondTracked = false;
        foreach (object window in (IEnumerable)originalList)
        {
            generalTracked |= ReferenceEquals(window, general);
            secondTracked |= ReferenceEquals(window, second);
        }
        if (!generalTracked || !secondTracked) throw new InvalidOperationException("A docked panel was lost from WindowList.");
        foreach (Form panel in new[] { general, second })
            if (panel.GetType().GetProperty("DockState").GetValue(panel).ToString() != "Document") throw new InvalidOperationException("The connection panel was not docked.");
        Console.WriteLine("PASS: fresh-runtime General and second connection panels dock correctly and remain tracked.");
        Console.Out.Flush();
        // This host tests startup without running the real application's shutdown/save handlers.
        Environment.Exit(0);
    }
}
