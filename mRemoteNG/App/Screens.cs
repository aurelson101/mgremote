using System.Runtime.Versioning;
using System.Windows.Forms;
using mRemoteNG.UI.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace mRemoteNG.App
{
    public static class Screens
    {
        [SupportedOSPlatform("windows")]
        public static void SendFormToScreen(Screen screen)
        {
            mRemoteNG.UI.WindowPlacement.MoveToScreen(FrmMain.Default, screen);
        }

        public static void SendPanelToScreen(DockContent panel, Screen screen)
        {
            if (panel == null || panel.IsDisposed || screen == null) return;
            panel.DockState = DockState.Float;
            mRemoteNG.UI.WindowPlacement.MoveToScreen(panel.FloatPane?.FloatWindow, screen);
        }
    }
}
