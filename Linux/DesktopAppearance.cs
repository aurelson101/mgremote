using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MGRemote.Linux;

internal static class DesktopAppearance
{
    internal static readonly IBrush Surface = Brush.Parse("#ffffff");
    internal static readonly IBrush Chrome = Brush.Parse("#f4f5f7");
    internal static readonly IBrush Divider = Brush.Parse("#c9cdd2");
    internal static readonly IBrush Text = Brush.Parse("#28313a");
    internal static readonly IBrush MutedText = Brush.Parse("#65717c");
    internal static readonly IBrush Selection = Brush.Parse("#dfe6ed");

    internal static void Apply(Application app)
    {
        foreach (var theme in app.Styles.OfType<FluentTheme>()) {
            theme.DensityStyle = DensityStyle.Compact;
            theme.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = Color.Parse("#62788d") };
        }
        app.Resources["ControlCornerRadius"] = new CornerRadius(2);
        app.Resources["OverlayCornerRadius"] = new CornerRadius(2);
        app.Resources["ButtonPadding"] = new Thickness(8, 3);
        foreach (string name in new[] { "ButtonBackgroundDisabled", "TextControlBackgroundDisabled", "ComboBoxBackgroundDisabled", "NumericUpDownBackgroundDisabled" }) app.Resources[name] = Brush.Parse("#f3f4f6");
        foreach (string state in new[] { "", "PointerOver", "Pressed" }) {
            var fill = state == "Pressed" ? Brush.Parse("#d9dfe5") : state == "PointerOver" ? Brush.Parse("#e6eaee") : Chrome;
            foreach (string prefix in new[] { "Button", "AccentButton" }) {
                app.Resources[prefix + "Background" + state] = fill;
                app.Resources[prefix + "Foreground" + state] = Text;
                app.Resources[prefix + "BorderBrush" + state] = Divider;
            }
        }
        foreach (string suffix in new[] { "Selected", "SelectedPointerOver", "SelectedPressed", "SelectedUnfocused" }) {
            app.Resources["TreeViewItemBackground" + suffix] = Selection;
            app.Resources["TreeViewItemForeground" + suffix] = Text;
        }

        void Style(Func<Selector?, Selector> selector, params Setter[] setters)
        {
            var style = new Style(selector);
            foreach (var setter in setters) style.Setters.Add(setter);
            app.Styles.Add(style);
        }
        Style(s => s.OfType<Window>(),
            new Setter(TemplatedControl.FontFamilyProperty, new FontFamily("Noto Sans, DejaVu Sans")),
            new Setter(TemplatedControl.FontSizeProperty, 13d));
        Style(s => s.OfType<Button>(),
            new Setter(TemplatedControl.FontSizeProperty, 13d),
            new Setter(Layoutable.MinHeightProperty, 30d),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 3)),
            new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(2)),
            new Setter(TemplatedControl.BackgroundProperty, Chrome),
            new Setter(TemplatedControl.ForegroundProperty, Text),
            new Setter(TemplatedControl.BorderBrushProperty, Divider),
            new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)));
        foreach (string state in new[] { ":pointerover", ":pressed" }) {
            var fill = Brush.Parse(state == ":pressed" ? "#d9dfe5" : "#e6eaee");
            Style(s => s.OfType<Button>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                new Setter(ContentPresenter.BackgroundProperty, fill),
                new Setter(ContentPresenter.ForegroundProperty, Text),
                new Setter(ContentPresenter.BorderBrushProperty, Divider));
        }
        Style(s => s.OfType<Menu>(),
            new Setter(TemplatedControl.BackgroundProperty, Brush.Parse("#eeeef2")),
            new Setter(TemplatedControl.ForegroundProperty, Text),
            new Setter(Layoutable.MinHeightProperty, 30d));
        Style(s => s.OfType<MenuItem>(),
            new Setter(TemplatedControl.FontSizeProperty, 13d),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 3)),
            new Setter(Layoutable.MinHeightProperty, 34d));
        Style(s => s.OfType<TabStripItem>(),
            new Setter(TemplatedControl.FontSizeProperty, 13d),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 4)),
            new Setter(Layoutable.MinHeightProperty, 34d));
        Style(s => s.OfType<TabStripItem>().Class(":selected"),
            new Setter(TemplatedControl.BackgroundProperty, Surface),
            new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse("#007acc")),
            new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0, 2, 0, 0)));
        foreach (string state in new[] { "", ":checked", ":pointerover", ":pressed" }) {
            var fill = Brush.Parse(state is ":pointerover" or ":pressed" ? "#dfe5eb" : "#e9edf1");
            Style(s => {
                var selector = s.OfType<ToggleButton>().Class("category-header");
                return state.Length == 0 ? selector : selector.Class(state);
            }, new Setter(TemplatedControl.ForegroundProperty, Text), new Setter(TemplatedControl.BackgroundProperty, fill));
            Style(s => {
                var selector = s.OfType<ToggleButton>().Class("category-header");
                if (state.Length > 0) selector = selector.Class(state);
                return selector.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
            }, new Setter(ContentPresenter.BackgroundProperty, fill), new Setter(ContentPresenter.ForegroundProperty, Text));
        }
        Style(s => s.Class("toolstrip").Descendant().OfType<Button>(),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 2)),
            new Setter(Layoutable.MinHeightProperty, 30d),
            new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
            new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        Style(s => s.OfType<Button>().Class("primary-action"),
            new Setter(TemplatedControl.BackgroundProperty, Brush.Parse("#e0e8f0")),
            new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse("#a9b9c8")),
            new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
            new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold));
        Style(s => s.OfType<TreeViewItem>(),
            new Setter(TemplatedControl.FontSizeProperty, 13d),
            new Setter(TemplatedControl.MinHeightProperty, 28d));
        Style(s => s.OfType<TextBlock>().Class("dock-title"),
            new Setter(TextBlock.FontSizeProperty, 13d),
            new Setter(TextBlock.FontWeightProperty, FontWeight.SemiBold),
            new Setter(TextBlock.ForegroundProperty, Text),
            new Setter(TextBlock.PaddingProperty, new Thickness(7, 4)),
            new Setter(TextBlock.BackgroundProperty, Chrome),
            new Setter(Layoutable.MarginProperty, new Thickness(0)));

        // Compact property editors only; modal prompts retain their natural sizes.
        foreach (Type type in new[] { typeof(TextBox), typeof(ComboBox), typeof(NumericUpDown) }) {
            Style(s => s.Class("properties").Descendant().OfType(type),
                new Setter(TemplatedControl.FontSizeProperty, 13d),
                new Setter(Layoutable.MinHeightProperty, 28d),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(5, 2)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(TemplatedControl.BackgroundProperty, Surface),
                new Setter(TemplatedControl.ForegroundProperty, Text));
        }
        Style(s => s.Class("properties").Descendant().OfType<Expander>(),
            new Setter(Layoutable.MinWidthProperty, 0d),
            new Setter(Layoutable.MinHeightProperty, 28d),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
            new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
            new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)));
        Style(s => s.Class("properties").Descendant().OfType<CheckBox>(),
            new Setter(Layoutable.MinHeightProperty, 28d),
            new Setter(TemplatedControl.FontSizeProperty, 13d),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 1)));
    }
}
