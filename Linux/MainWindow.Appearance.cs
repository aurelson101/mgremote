using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MGRemote.Linux;
public sealed partial class MainWindow
{
    private static readonly IBrush PanelLine = Brush.Parse("#d6dbe1");
    internal static readonly IBrush Workspace = Brush.Parse("#eeeef2");
    private static readonly IBrush ActiveCaption = Brush.Parse("#007acc"), InactiveCaption = Brush.Parse("#cfd6e5");
    // Dock caption in the Windows style: blue when active, pale slate otherwise, with pin/close glyphs.
    private static Border DockCaption(Control title, bool active)
    {
        var ink = active ? Brushes.White : Brush.Parse("#2b3a55");
        var glyphs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        glyphs.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M3 1H8 M4 1V5L2 7H9L7 5V1 M5.5 7V11"), Stroke = ink, StrokeThickness = 1.1, Width = 11, Height = 12 });
        glyphs.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M1 1L9 9 M9 1L1 9"), Stroke = ink, StrokeThickness = 1.3, Width = 10, Height = 10 });
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(glyphs, Dock.Right); row.Children.Add(glyphs); row.Children.Add(title);
        return new Border { Background = active ? ActiveCaption : InactiveCaption, Child = row, MinHeight = 26 };
    }
    private static Control DockTitle(string title, bool active = false)
        => DockCaption(new TextBlock { Text = title, FontSize = 13, Foreground = active ? Brushes.White : Brush.Parse("#2b3a55"), Margin = new Thickness(8, 4), VerticalAlignment = VerticalAlignment.Center }, active);
    private static Image ToolbarGlyph(string path, int size = 16)
        => new() { Width = size, Height = size, Source = new DrawingImage { Drawing = new GeometryDrawing {
            Geometry = Geometry.Parse(path), Pen = new Pen(Brush.Parse("#65717e"), 1.4)
        } } };
    private static Button ToolButton(string label, string tooltip, string path, Action action)
    {
        var button = Button(label, action);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(ToolbarGlyph(path));
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        button.Content = content; ToolTip.SetTip(button, tooltip); button.Classes.Add("tool-button");
        return button;
    }
    private static Control BrowseField(Control field, string tooltip, Action action)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,26") };
        var browse = Button("…", action); browse.Width = 26; browse.Padding = new Thickness(0);
        ToolTip.SetTip(browse, tooltip); Avalonia.Automation.AutomationProperties.SetName(browse, tooltip);
        row.Children.Add(field); Grid.SetColumn(browse, 1); row.Children.Add(browse); return row;
    }
    private static void CompactCategory(Expander group)
    {
        group.Margin = new Thickness(0); group.Padding = new Thickness(0); group.BorderThickness = new Thickness(0);
        group.Template = new FuncControlTemplate<Expander>((owner, _) => {
            var heading = new ToggleButton {
                Height = 30, MinHeight = 30, Padding = new Thickness(6, 0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                Background = Brush.Parse("#e9edf1"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            heading.Classes.Add("category-header");
            var arrow = new Avalonia.Controls.Shapes.Path {
                Data = Geometry.Parse("M1 2L5 6L9 2"), Stroke = Brush.Parse("#65717e"), StrokeThickness = 1.4,
                Width = 10, Height = 8, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new RotateTransform(owner.IsExpanded ? 0 : -90)
            };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            title.Children.Add(arrow); title.Children.Add(new TextBlock { Text = owner.Header?.ToString(), FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            heading.Content = title;
            heading.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(Expander.IsExpanded)) { Source = owner, Mode = BindingMode.TwoWay });
            heading.IsCheckedChanged += (_, _) => arrow.RenderTransform = new RotateTransform(heading.IsChecked == true ? 0 : -90);
            var body = new ContentPresenter { Padding = new Thickness(0) };
            body.Bind(ContentPresenter.ContentProperty, new Binding(nameof(Expander.Content)) { Source = owner });
            body.Bind(IsVisibleProperty, new Binding(nameof(Expander.IsExpanded)) { Source = owner });
            var panel = new StackPanel(); panel.Children.Add(heading); panel.Children.Add(body); return panel;
        });
    }
    private Control EmptyWorkspace()
    {
        var panel = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 430, Margin = new Thickness(28) };
        var icon = ToolbarGlyph("M2 2H22V16H2Z M8 22H16 M12 16V22", 40); icon.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = "Votre espace de connexions", FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = DesktopAppearance.Text, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "Sélectionnez une connexion à gauche, puis cliquez sur Connecter. Le double-clic ouvre aussi une session.", FontSize = 13, Foreground = DesktopAppearance.MutedText, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var create = Button("Nouvelle connexion", NewConnectionFromTree); create.Margin = new Thickness(4); actions.Children.Add(create);
        var import = Button("Importer des connexions…", async () => await Import()); import.Margin = new Thickness(4); actions.Children.Add(import);
        panel.Children.Add(actions);
        return panel;
    }
    internal async Task VerifyCompactLayout(string screenshot)
    {
        Width = 900; Height = 600; await Task.Delay(180);
        if (double.TryParse(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"), System.Globalization.CultureInfo.InvariantCulture, out double expectedScale) && Math.Abs(RenderScaling - expectedScale) > 0.01) throw new Exception("Requested HiDPI scale was not applied.");
        string previousStatus = _status.Text ?? "";
        _status.Text = string.Join(" ", Enumerable.Repeat("Un message long ne doit pas déplacer les actions de configuration.", 40));
        await Task.Delay(80);
        if (_status.Bounds.Height > 32 || _status.Bounds.Width > ClientSize.Width || ToolTip.GetTip(_status) is not TextBlock tip || tip.Text != _status.Text) throw new Exception("Long status messages resize the workspace or lose their full text.");
        _status.Text = previousStatus;
        var actions = ((Control)Content!).GetVisualDescendants().OfType<Button>()
            .Where(b => b.Content is string label && label is "Enregistrer" or "Supprimer").ToArray();
        if (actions.Length != 2) throw new Exception("Configuration actions are missing.");
        foreach (var action in actions) {
            var point = action.TranslatePoint(new Point(), this);
            if (point == null || point.Value.Y < 0 || point.Value.Y + action.Bounds.Height > ClientSize.Height) throw new Exception("Compact layout hides configuration actions.");
        }
        using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)(ClientSize.Width * RenderScaling), (int)(ClientSize.Height * RenderScaling)), new Vector(96 * RenderScaling, 96 * RenderScaling));
        image.Render((Visual)Content!); image.Save(screenshot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Console.WriteLine("PASS: compact 900x600 desktop layout, visible configuration actions, stable long-status layout, DPI scale=" + RenderScaling + " and screenshot.");
    }
}
