using System.Reflection;

namespace Helper;

/// <summary>
/// Dark, Valheim-flavoured palette and control styling. The colours are sampled from the game's
/// logo — charred wood, bone-coloured lettering, and the ember glow running through the carving —
/// so the helper doesn't look like a stock Windows dialog sitting next to the game.
/// </summary>
internal static class Theme
{
    public static readonly Color Night     = Color.FromArgb(0x14, 0x10, 0x0D); // window background
    public static readonly Color Wood      = Color.FromArgb(0x24, 0x1C, 0x16); // raised panels
    public static readonly Color WoodLit   = Color.FromArgb(0x33, 0x27, 0x1E); // hover
    public static readonly Color WoodEdge  = Color.FromArgb(0x3E, 0x2E, 0x22); // borders
    public static readonly Color Sunken    = Color.FromArgb(0x0F, 0x0C, 0x0A); // inputs, activity log
    public static readonly Color Parchment = Color.FromArgb(0xE9, 0xDC, 0xC3); // primary text
    public static readonly Color Muted     = Color.FromArgb(0x9C, 0x8B, 0x73); // secondary text
    public static readonly Color Ember     = Color.FromArgb(0xE2, 0x66, 0x2B); // accent, headings
    public static readonly Color EmberDeep = Color.FromArgb(0x6E, 0x30, 0x14); // button borders
    public static readonly Color Gold      = Color.FromArgb(0xE3, 0xA8, 0x3C); // you are hosting
    public static readonly Color Moss      = Color.FromArgb(0x93, 0xB0, 0x67); // world is free
    public static readonly Color Blood     = Color.FromArgb(0xC4, 0x4A, 0x2C); // something is wrong

    // A serif face for headings and buttons reads closer to the game's carved lettering than the
    // system UI font. Georgia ships with Windows, so nothing has to be installed or embedded.
    private const string DisplayFamily = "Georgia";

    /// <summary>
    /// A display-face font, falling back to the system UI font if Georgia isn't present. Font
    /// substitutes silently when a family is missing, so the result is checked rather than assumed.
    /// </summary>
    public static Font Display(float size, FontStyle style = FontStyle.Regular)
    {
        try
        {
            var font = new Font(DisplayFamily, size, style);
            if (font.Name.Equals(DisplayFamily, StringComparison.OrdinalIgnoreCase)) return font;
            font.Dispose();
        }
        catch { /* fall through to the system face */ }
        return new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, size, style);
    }

    /// <summary>Recursively apply the palette to a control tree. Call once the tree is built.</summary>
    public static void Apply(Control control)
    {
        switch (control)
        {
            case TextBox textBox:
                textBox.BackColor = Sunken;
                textBox.ForeColor = Parchment;
                break;

            case NumericUpDown numeric:
                numeric.BackColor = Sunken;
                numeric.ForeColor = Parchment;
                // The spinner is an internal child control that keeps its own system-coloured
                // background, so it stays a bright block unless it's reached directly.
                foreach (Control part in numeric.Controls) part.BackColor = Wood;
                break;

            case Button button:
                StyleButton(button);
                break;

            case RunicCheckBox:
                break; // paints itself

            case CheckBox checkBox:
                checkBox.FlatStyle = FlatStyle.Flat;
                checkBox.ForeColor = Parchment;
                checkBox.BackColor = Wood;
                break;

            case GroupBox:
                break; // RunicGroupBox paints itself

            case Label label:
                // Leave a colour that was set deliberately (the status line, the muted detail line);
                // only controls still on the system default need claiming.
                if (label.ForeColor == SystemColors.ControlText) label.ForeColor = Parchment;
                break;
        }

        foreach (Control child in control.Controls) Apply(child);
    }

    public static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Wood;
        button.ForeColor = Parchment;
        button.Font = Display(9.5f, FontStyle.Bold);
        button.UseVisualStyleBackColor = false;
        button.Cursor = Cursors.Hand;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = EmberDeep;
        button.FlatAppearance.MouseOverBackColor = WoodLit;
        button.FlatAppearance.MouseDownBackColor = Sunken;
    }

    /// <summary>
    /// Wrap an input in a one-pixel frame we control the colour of. WinForms draws
    /// <see cref="BorderStyle.FixedSingle"/> in a fixed system colour that can't be themed, which
    /// reads as a bright outline around every field on a dark form. A padded panel behind a
    /// borderless control gives the same look in the palette's own colours.
    /// </summary>
    public static Panel Framed(Control inner, int? fixedWidth = null)
    {
        if (inner is TextBox box) box.BorderStyle = BorderStyle.None;
        if (inner is NumericUpDown numeric) numeric.BorderStyle = BorderStyle.None;

        // A borderless TextBox sits flush against the frame; a little breathing room inside the
        // frame keeps the text off the line without changing the control's own metrics.
        // Colours set here rather than left to Apply: Framed runs while the tree is being built, so
        // reading them off the control would capture the system defaults it still has.
        inner.BackColor = Sunken;
        inner.ForeColor = Parchment;
        var frame = new Panel { BackColor = WoodEdge, Padding = new Padding(1) };
        var pad = new Panel { BackColor = Sunken, Dock = DockStyle.Fill, Padding = new Padding(3) };
        inner.Dock = DockStyle.Fill;
        pad.Controls.Add(inner);
        frame.Controls.Add(pad);

        var height = inner.PreferredSize.Height + 8;
        if (fixedWidth is { } w) { frame.AutoSize = false; frame.Size = new Size(w + 8, height); }
        else { frame.Height = height; }
        return frame;
    }

    /// <summary>Mark the one button that's the main thing to do on this screen.</summary>
    public static void MakePrimary(Button button)
    {
        button.ForeColor = Ember;
        button.FlatAppearance.BorderColor = Ember;
    }

    /// <summary>The app logo, or null if the resource is missing (the UI degrades to no header).</summary>
    public static Image? LoadLogo() => LoadResource("logo.png", Image.FromStream);

    /// <summary>The app icon, used for the window, taskbar and Alt+Tab.</summary>
    public static Icon? LoadIcon() => LoadResource("app.ico", stream => new Icon(stream));

    // Matched on suffix rather than a hard-coded manifest name, which depends on the project's root
    // namespace and would break silently — as an empty window header — if that ever changed.
    private static T? LoadResource<T>(string suffix, Func<Stream, T> read) where T : class
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = Array.Find(
                assembly.GetManifestResourceNames(),
                n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var stream = assembly.GetManifestResourceStream(name);
            return stream is null ? null : read(stream);
        }
        catch { return null; }
    }
}

/// <summary>
/// A <see cref="CheckBox"/> drawn in the app's palette. The stock flat check box fills its glyph
/// with a *lightened* BackColor, which on a dark form comes out as a pale tan tile and can't be
/// steered by any property — so the box, the tick and the label are all drawn here instead.
/// </summary>
internal sealed class RunicCheckBox : CheckBox
{
    private const int BoxSize = 14;
    private const int TextGap = 7;

    public RunicCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Wood;
        ForeColor = Theme.Parchment;
        AutoSize = true;
        Cursor = Cursors.Hand;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        return new Size(
            LogicalToDeviceUnits(BoxSize + TextGap) + text.Width + 2,
            Math.Max(text.Height, LogicalToDeviceUnits(BoxSize + 2)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        var size = LogicalToDeviceUnits(BoxSize);
        var box = new Rectangle(0, (Height - size) / 2, size, size);

        using (var fill = new SolidBrush(Theme.Sunken)) g.FillRectangle(fill, box);
        using (var pen = new Pen(Checked ? Theme.Ember : Theme.WoodEdge)) g.DrawRectangle(pen, box);

        if (Checked)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var tick = new Pen(Theme.Ember, Math.Max(2f, size / 7f));
            g.DrawLines(tick,
            [
                new PointF(box.Left + size * 0.22f, box.Top + size * 0.52f),
                new PointF(box.Left + size * 0.42f, box.Top + size * 0.74f),
                new PointF(box.Left + size * 0.80f, box.Top + size * 0.26f),
            ]);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
        }

        var textLeft = box.Right + LogicalToDeviceUnits(TextGap);
        TextRenderer.DrawText(
            g, Text, Font, new Rectangle(textLeft, 0, Width - textLeft, Height),
            Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// A <see cref="GroupBox"/> drawn in the app's palette. The stock control paints a system-coloured
/// frame that ignores ForeColor/BackColor, so it has to be painted by hand to fit a dark theme.
/// </summary>
internal sealed class RunicGroupBox : GroupBox
{
    public RunicGroupBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Wood;
        ForeColor = Theme.Ember;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        using var font = Theme.Display(10f, FontStyle.Bold);
        var caption = TextRenderer.MeasureText(g, Text, font);
        var captionLeft = LogicalToDeviceUnits(10);
        var frameTop = caption.Height / 2;

        using (var pen = new Pen(Theme.WoodEdge))
        {
            g.DrawRectangle(pen, new Rectangle(0, frameTop, Width - 1, Height - frameTop - 1));
        }

        // Break the frame behind the caption so the text doesn't sit on top of the line.
        using (var brush = new SolidBrush(BackColor))
        {
            g.FillRectangle(brush, captionLeft - 4, frameTop - caption.Height / 2, caption.Width + 8, caption.Height);
        }
        TextRenderer.DrawText(g, Text, font, new Point(captionLeft, frameTop - caption.Height / 2), ForeColor);
    }
}
