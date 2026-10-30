using System;
using System.Windows.Forms;
using mRemoteNG.Tools;
using mRemoteNG.Resources.Language;
using System.Runtime.Versioning;
using System.Linq;

namespace mRemoteNG.UI.Menu
{
    [SupportedOSPlatform("windows")]
    // This class creates new menu items to menu that appears when you right click the top of the app (where the window title is)
    public class AdvancedWindowMenu(IWin32Window boundControl) : IDisposable
    {
        private readonly WindowMenu _windowMenu = new WindowMenu(boundControl.Handle);
        private Screen[] _screens = Array.Empty<Screen>();
        private bool _disposed;

        public Screen GetScreenById(int id)
        {
            int index = id - 200;
            return index >= 0 && index < _screens.Length
                ? Array.Find(Screen.AllScreens, screen => screen.DeviceName == _screens[index].DeviceName) : null;
        }

        public void OnDisplayChanged(object sender, EventArgs e)
        {
            if (_disposed || boundControl is not Control control || control.IsDisposed || !control.IsHandleCreated) return;
            if (control.InvokeRequired)
            {
                try { control.BeginInvoke(new Action(() => OnDisplayChanged(sender, e))); }
                catch (InvalidOperationException) { /* Window closed while the display notification was queued. */ }
                return;
            }
            ResetScreenList();
            BuildAdditionalMenuItems();
            foreach (var form in Application.OpenForms.Cast<Form>().ToArray())
                if (ReferenceEquals(form, control) || form is WeifenLuo.WinFormsUI.Docking.FloatWindow)
                    WindowPlacement.EnsureVisible(form);
        }

        private void ResetScreenList()
        {
            _windowMenu.Reset();
        }

        public void BuildAdditionalMenuItems()
        {
            if (_disposed) return;
            // option to send main form to another screen
            IntPtr popMen = _windowMenu.CreatePopupMenuItem();
            _screens = Screen.AllScreens;
            for (int i = 0; i < _screens.Length; i++)
            {
                _windowMenu.AppendMenuItem(popMen, WindowMenu.Flags.MF_STRING, new IntPtr(200 + i),
                    $"{Language.Screen} {i + 1} — {_screens[i].Bounds.Width}×{_screens[i].Bounds.Height}");
            }
            _windowMenu.InsertMenuItem(_windowMenu.SystemMenuHandle, 0,
                WindowMenu.Flags.MF_POPUP | WindowMenu.Flags.MF_BYPOSITION, popMen,
                Language.SendTo);
            // option to show/hide menu strips
            _windowMenu.InsertMenuItem(_windowMenu.SystemMenuHandle, 1,
                WindowMenu.Flags.MF_BYPOSITION, new IntPtr(0), 
                Language.ShowHideMenu);
            // separator
            _windowMenu.InsertMenuItem(_windowMenu.SystemMenuHandle, 2,
                                       WindowMenu.Flags.MF_BYPOSITION | WindowMenu.Flags.MF_SEPARATOR, IntPtr.Zero,
                                       null);
        }

        private void Dispose(bool disposing)
        {
            if (!disposing || _disposed) return;
            _disposed = true;

            _windowMenu?.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
