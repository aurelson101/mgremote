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
    private static Control DockTitle(string title)
        => new Border {
            Background = Brush.Parse("#edf0f3"), BorderBrush = PanelLine, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 4) }
        };
    private static Image ToolbarGlyph(string path, int size = 16)
        => new() { Width = size, Height = size, Source = new DrawingImage { Drawing = new GeometryDrawing {
            Geometry = Geometry.Parse(path), Pen = new Pen(Brush.Parse("#65717e"), 1.4)
        } } };
    private static Button ToolButton(string label, string tooltip, string path, Action action)
    {
        var button = Button(label, action);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(ToolbarGlyph(path));
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
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
                Height = 25, MinHeight = 25, Padding = new Thickness(6, 0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                Background = Brush.Parse("#e9edf1"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            heading.Classes.Add("category-header");
            var arrow = new Avalonia.Controls.Shapes.Path {
                Data = Geometry.Parse("M1 2L5 6L9 2"), Stroke = Brush.Parse("#65717e"), StrokeThickness = 1.4,
                Width = 10, Height = 8, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new RotateTransform(owner.IsExpanded ? 0 : -90)
            };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            title.Children.Add(arrow); title.Children.Add(new TextBlock { Text = owner.Header?.ToString(), FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            heading.Content = title;
            heading.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(Expander.IsExpanded)) { Source = owner, Mode = BindingMode.TwoWay });
            heading.IsCheckedChanged += (_, _) => arrow.RenderTransform = new RotateTransform(heading.IsChecked == true ? 0 : -90);
            var body = new ContentPresenter { Padding = new Thickness(0) };
            body.Bind(ContentPresenter.ContentProperty, new Binding(nameof(Expander.Content)) { Source = owner });
            body.Bind(IsVisibleProperty, new Binding(nameof(Expander.IsExpanded)) { Source = owner });
            var panel = new StackPanel(); panel.Children.Add(heading); panel.Children.Add(body); return panel;
        });
    }
    private static Control EmptyWorkspace()
    {
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, Margin = new Thickness(24) };
        var icon = ToolbarGlyph("M2 2H22V16H2Z M8 22H16 M12 16V22", 36); icon.Opacity = 0.45; icon.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = "Aucune session ouverte", FontSize = 15, Foreground = Brush.Parse("#64707d"), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "Double-cliquez sur une connexion pour ouvrir un onglet.", FontSize = 12, Foreground = Brush.Parse("#808b97"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        return panel;
    }
    internal async Task VerifyCompactLayout(string screenshot)
    {
        Width = 900; Height = 600; await Task.Delay(180);
        var actions = ((Control)Content!).GetVisualDescendants().OfType<Button>()
            .Where(b => b.Content is string label && label is "Enregistrer" or "Supprimer").ToArray();
        if (actions.Length != 2) throw new Exception("Configuration actions are missing.");
        foreach (var action in actions) {
            var point = action.TranslatePoint(new Point(), this);
            if (point == null || point.Value.Y < 0 || point.Value.Y + action.Bounds.Height > ClientSize.Height) throw new Exception("Compact layout hides configuration actions.");
        }
        using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)ClientSize.Width, (int)ClientSize.Height));
        image.Render((Visual)Content!); image.Save(screenshot);
        Console.WriteLine("PASS: compact 900x600 desktop layout, visible configuration actions and screenshot.");
    }
}
