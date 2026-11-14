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
    private static string PreferencePath => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "mgremote", "theme");
    internal static bool Dark = ReadPreference();
    private static bool ReadPreference()
    {
        string? forced = Environment.GetEnvironmentVariable("MGREMOTE_THEME");
        if (forced is "dark" or "light") return forced == "dark";
        try { return File.Exists(PreferencePath) && File.ReadAllText(PreferencePath).Trim() == "dark"; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    internal static void SavePreference(bool dark)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!); File.WriteAllText(PreferencePath, dark ? "dark" : "light");
    }
    // Light-theme hex values are authored once; dark mode flips lightness of neutral/pale tones and keeps saturated accents.
    internal static IBrush Tone(string hex) => new SolidColorBrush(ToneColor(hex));
    internal static Color ToneColor(string hex)
    {
        var c = Color.Parse(hex); if (!Dark) return c;
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        if (s > 0.9 && l > 0.25 && l < 0.6) return c; // saturated accents such as the dock caption blue
        double h = d == 0 ? 0 : max == r ? ((g - b) / d % 6) : max == g ? (b - r) / d + 2 : (r - g) / d + 4; h = (h * 60 + 360) % 360;
        double nl = 1 - l; nl = 0.11 + nl * 0.8; // keep dark surfaces from going pure black and light text from going pure white
        double ch = (1 - Math.Abs(2 * nl - 1)) * Math.Min(s, 0.45), x = ch * (1 - Math.Abs(h / 60 % 2 - 1)), m = nl - ch / 2;
        (double R, double G, double B) = h < 60 ? (ch, x, 0d) : h < 120 ? (x, ch, 0d) : h < 180 ? (0d, ch, x) : h < 240 ? (0d, x, ch) : h < 300 ? (x, 0d, ch) : (ch, 0d, x);
        return Color.FromArgb(c.A, (byte)Math.Round((R + m) * 255), (byte)Math.Round((G + m) * 255), (byte)Math.Round((B + m) * 255));
    }
    internal static readonly IBrush Surface = Tone("#ffffff");
    internal static readonly IBrush Chrome = Tone("#f4f5f7");
    internal static readonly IBrush Divider = Tone("#c9cdd2");
    internal static readonly IBrush Text = Tone("#28313a");
    internal static readonly IBrush MutedText = Tone("#65717c");
    internal static readonly IBrush Selection = Tone("#dfe6ed");

    internal static void Apply(Application app)
    {
        foreach (var theme in app.Styles.OfType<FluentTheme>()) {
            theme.DensityStyle = DensityStyle.Compact;
            theme.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = Color.Parse("#62788d") };
            theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = Color.Parse("#7fa6c9"), RegionColor = ToneColor("#ffffff"), ChromeLow = ToneColor("#f4f5f7") };
        }
        app.Resources["ControlCornerRadius"] = new CornerRadius(2);
        app.Resources["OverlayCornerRadius"] = new CornerRadius(2);
        app.Resources["ButtonPadding"] = new Thickness(8, 3);
        foreach (string name in new[] { "ButtonBackgroundDisabled", "TextControlBackgroundDisabled", "ComboBoxBackgroundDisabled", "NumericUpDownBackgroundDisabled" }) app.Resources[name] = Tone("#f3f4f6");
        foreach (string state in new[] { "", "PointerOver", "Pressed" }) {
            var fill = state == "Pressed" ? Tone("#d9dfe5") : state == "PointerOver" ? Tone("#e6eaee") : Chrome;
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
            var fill = Tone(state == ":pressed" ? "#d9dfe5" : "#e6eaee");
            Style(s => s.OfType<Button>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                new Setter(ContentPresenter.BackgroundProperty, fill),
                new Setter(ContentPresenter.ForegroundProperty, Text),
                new Setter(ContentPresenter.BorderBrushProperty, Divider));
        }
        Style(s => s.OfType<Menu>(),
            new Setter(TemplatedControl.BackgroundProperty, Tone("#eeeef2")),
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
            new Setter(TemplatedControl.BorderBrushProperty, Tone("#007acc")),
            new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0, 2, 0, 0)));
        foreach (string state in new[] { "", ":checked", ":pointerover", ":pressed" }) {
            var fill = Tone(state is ":pointerover" or ":pressed" ? "#dfe5eb" : "#e9edf1");
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
            new Setter(TemplatedControl.BackgroundProperty, Tone("#e0e8f0")),
            new Setter(TemplatedControl.BorderBrushProperty, Tone("#a9b9c8")),
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
