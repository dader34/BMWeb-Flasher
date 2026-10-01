using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;

namespace BmwebFlasher
{
    /// <summary>
    /// A skin: the colour and font roles of BMWeb (the web app), carried over
    /// one to one from its css/themes.css so the two look alike. The roles keep
    /// BMWeb's names: "accent" is what the CSS calls amber, whatever colour a
    /// skin gives it.
    /// </summary>
    public sealed class Skin
    {
        public string Id, Name;
        public string Bg, Panel, Panel2, Raised, Line, LineBright;
        public string Ink, InkDim, InkFaint;
        public string Accent, AccentSoft, AccentGlow, AccentInk;
        public string Red, Green, Cyan;
        /// <summary>The top and bottom bars' gradient, and the F-key chip background.</summary>
        public string BarTop, BarBottom, KeyBg;
        public string Display, Mono;
        /// <summary>Corner radius of panels and controls: 0 for a skin drawn with square corners.</summary>
        public double Radius = 4;
        /// <summary>Whether the skin is a light one (used for the window's theme variant).</summary>
        public bool Light;

        public override string ToString() => Name;
    }

    /// <summary>
    /// Two of the skins BMWeb offers in its Settings (Brushed Metal and
    /// Brackets), applied here as application resources that
    /// Styles/Flasher.axaml reads through DynamicResource, so a change takes
    /// effect on the open window.
    /// </summary>
    public static class Skins
    {
        private const string Fonts = "avares://BmwebFlasher/Assets/Fonts";

        public static readonly Skin[] All =
        {
            new Skin
            {
                Id = "metal", Name = "Brushed Metal",
                Bg = "#B9BCC1", Panel = "#CDD0D4", Panel2 = "#C2C5CA", Raised = "#D8DADD",
                Line = "#9A9DA3", LineBright = "#7E8187",
                Ink = "#1E2227", InkDim = "#4A4F56", InkFaint = "#74797F",
                Accent = "#2F6FD6", AccentSoft = "#2257AC", AccentGlow = "#292F6FD6", AccentInk = "#FFFFFF",
                Red = "#C0392B", Green = "#2E8B57", Cyan = "#2F6FD6",
                BarTop = "#D3D6DA", BarBottom = "#B9BCC1", KeyBg = "#D8DADD",
                Display = "Lucida Grande, Helvetica Neue, Helvetica", Mono = "Monaco, Menlo, Consolas",
                Light = true,
            },
            new Skin
            {
                Id = "brackets", Name = "Brackets",
                Bg = "#1D1F21", Panel = "#2A2D2E", Panel2 = "#323537", Raised = "#3A3D3F",
                Line = "#404447", LineBright = "#5A5D5F",
                Ink = "#D4D7D9", InkDim = "#A0A3A5", InkFaint = "#6C6F71",
                Accent = "#F8DE7E", AccentSoft = "#E6CC6C", AccentGlow = "#26F8DE7E", AccentInk = "#1D1F21",
                Red = "#F06C75", Green = "#89CA78", Cyan = "#5DB0D7",
                BarTop = "#2A2D2E", BarBottom = "#1D1F21", KeyBg = "#232527",
                Display = "Helvetica Neue, Segoe UI, Helvetica", Mono = "Source Code Pro, " + Fonts + "#JetBrains Mono",
                Radius = 0,
            },
        };

        public const string DefaultId = "brackets";

        public static Skin Find(string id)
            => All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

        /// <summary>
        /// Puts a skin's roles into the application's resources. Every role is
        /// both a Color (Bw*) and a SolidColorBrush (Bw*Brush); the two
        /// gradients and the fonts are separate. A few of the Fluent theme's own
        /// keys are set as well, for the parts of controls the style sheet does
        /// not reach (drop-downs, tool tips, selection).
        /// </summary>
        public static void Apply(Application app, string id)
        {
            Skin s = Find(id);
            var r = app.Resources;

            void Role(string name, string hex)
            {
                Color c = Color.Parse(hex);
                r["Bw" + name] = c;
                r["Bw" + name + "Brush"] = new SolidColorBrush(c);
            }

            Role("Bg", s.Bg); Role("Panel", s.Panel); Role("Panel2", s.Panel2); Role("Raised", s.Raised);
            Role("Line", s.Line); Role("LineBright", s.LineBright);
            Role("Ink", s.Ink); Role("InkDim", s.InkDim); Role("InkFaint", s.InkFaint);
            Role("Accent", s.Accent); Role("AccentSoft", s.AccentSoft); Role("AccentGlow", s.AccentGlow);
            Role("AccentInk", s.AccentInk);
            Role("Red", s.Red); Role("Green", s.Green); Role("Cyan", s.Cyan);
            Role("KeyBg", s.KeyBg);

            r["BwBarBrush"] = Vertical(s.BarTop, s.BarBottom);
            r["BwSectionBrush"] = Vertical(s.Panel2, s.Panel);
            r["BwPrimaryBrush"] = Vertical(s.Accent, s.AccentSoft);

            r["BwRadius"] = new CornerRadius(s.Radius);
            r["ControlCornerRadius"] = new CornerRadius(s.Radius);      // Fluent's own, for what the sheet does not template
            r["OverlayCornerRadius"] = new CornerRadius(s.Radius);
            r["BwDisplayFont"] = new FontFamily(s.Display);
            r["BwMonoFont"] = new FontFamily(s.Mono);

            // Fluent's own keys, for what the style sheet does not template.
            Color accent = Color.Parse(s.Accent), panel = Color.Parse(s.Panel), raised = Color.Parse(s.Raised);
            Color ink = Color.Parse(s.Ink), line = Color.Parse(s.Line), glow = Color.Parse(s.AccentGlow);
            r["SystemAccentColor"] = accent;
            // tabs: no selection pipe, the pill's border shows the selection
            r["TabItemHeaderSelectedPipeFill"] = new SolidColorBrush(Colors.Transparent);
            r["TabItemPipeThickness"] = 0.0;
            r["TabItemVerticalPipeHeight"] = 0.0;
            r["ComboBoxDropDownBackground"] = new SolidColorBrush(panel);
            r["ComboBoxDropDownBorderBrush"] = new SolidColorBrush(line);
            r["ComboBoxItemForeground"] = new SolidColorBrush(ink);
            r["ComboBoxItemBackgroundPointerOver"] = new SolidColorBrush(raised);
            r["ComboBoxItemBackgroundSelected"] = new SolidColorBrush(glow);
            r["ComboBoxItemBackgroundSelectedPointerOver"] = new SolidColorBrush(glow);
            r["ToolTipBackground"] = new SolidColorBrush(panel);
            r["ToolTipForeground"] = new SolidColorBrush(ink);
            r["ToolTipBorderBrush"] = new SolidColorBrush(line);
            r["ScrollBarThumbFill"] = new SolidColorBrush(Color.Parse(s.LineBright));
            r["ScrollBarThumbFillPointerOver"] = new SolidColorBrush(accent);
            r["ScrollBarTrackFill"] = new SolidColorBrush(Colors.Transparent);
            r["DataGridRowHoveredBackgroundColor"] = raised;
            r["DataGridRowSelectedBackgroundColor"] = glow;
            r["DataGridRowSelectedHoveredBackgroundColor"] = glow;
            r["DataGridRowSelectedUnfocusedBackgroundColor"] = glow;

            app.RequestedThemeVariant = s.Light
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;
        }

        private static LinearGradientBrush Vertical(string top, string bottom)
            => new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse(top), 0),
                    new GradientStop(Color.Parse(bottom), 1),
                },
            };
    }
}
