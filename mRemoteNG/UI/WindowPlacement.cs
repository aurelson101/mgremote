// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace mRemoteNG.UI;

internal static class WindowPlacement
{
    internal static Rectangle ClampToWorkingArea(Rectangle bounds, Rectangle area)
    {
        if (area.Width < 1 || area.Height < 1) throw new ArgumentOutOfRangeException(nameof(area));
        int width = Math.Clamp(bounds.Width, 1, area.Width);
        int height = Math.Clamp(bounds.Height, 1, area.Height);
        return new Rectangle(Math.Clamp(bounds.X, area.Left, area.Right - width),
            Math.Clamp(bounds.Y, area.Top, area.Bottom - height), width, height);
    }

    internal static void MoveToScreen(Form form, Screen screen)
    {
        if (form == null || form.IsDisposed || screen == null) return;
        var state = form.WindowState;
        if (state != FormWindowState.Normal) form.WindowState = FormWindowState.Normal;
        var area = screen.WorkingArea;
        form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, area.Width), Math.Min(form.MinimumSize.Height, area.Height));
        form.Bounds = ClampToWorkingArea(new Rectangle(area.Location, form.Size), area);
        if (state == FormWindowState.Maximized) form.WindowState = state;
    }

    internal static void EnsureVisible(Form form)
    {
        if (form.IsDisposed || form.WindowState == FormWindowState.Minimized) return;
        var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        var screen = Screen.FromRectangle(bounds);
        var safe = ClampToWorkingArea(bounds, screen.WorkingArea);
        if (safe == bounds) return;
        var state = form.WindowState;
        form.WindowState = FormWindowState.Normal;
        form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, screen.WorkingArea.Width), Math.Min(form.MinimumSize.Height, screen.WorkingArea.Height));
        form.Bounds = safe;
        if (state == FormWindowState.Maximized) form.WindowState = state;
    }
}
