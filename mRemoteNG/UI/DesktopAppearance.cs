// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Collections;
using System.Reflection;
using WeifenLuo.WinFormsUI.Docking;

namespace mRemoteNG.UI;

internal static class DesktopAppearance
{
    // DockPanelSuite has no per-strip cleanup API. Its ThemeBase cache otherwise
    // retains disposed connection menus until the entire shared theme is cleared.
    private static readonly FieldInfo ThemeMenuCache = typeof(ThemeBase).GetField("_stripBefore", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static void ReleaseThemedMenu(ThemeBase theme, ToolStrip menu)
    {
        if (menu != null && ThemeMenuCache?.GetValue(theme) is IDictionary cache) cache.Remove(menu);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void Configure(Form form, Size logicalMinimum)
    {
        form.Font = SystemFonts.MessageBoxFont;
        void UpdateMinimum()
        {
            // Do not force Handle creation while a form constructor is still initializing.
            var area = (form.IsHandleCreated ? Screen.FromHandle(form.Handle) : Screen.FromPoint(Cursor.Position)).WorkingArea;
            float scale = form.DeviceDpi / 96f;
            form.MinimumSize = new Size(Math.Min(area.Width, (int)(logicalMinimum.Width * scale)),
                Math.Min(area.Height, (int)(logicalMinimum.Height * scale)));
        }
        form.DpiChanged += (_, _) => UpdateMinimum();
        form.HandleCreated += (_, _) =>
        {
            UpdateMinimum();
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int round = 2;
                _ = DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int));
            }
        };
        UpdateMinimum();
    }
}
