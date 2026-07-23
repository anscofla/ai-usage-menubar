using System.Drawing;
using System.Drawing.Text;
using AIUsage.Core;

namespace AIUsage.Tray;

public sealed class RenderedIcon : IDisposable
{
    public Icon Icon { get; }

    internal RenderedIcon(Icon icon) { Icon = icon; }

    public void Dispose() => Icon.Dispose();
}

public static class IconRenderer
{
    public static RenderedIcon Render(TrayState state, int sizePx)
    {
        var text = Display.IconText(state);
        var back = state switch
        {
            TrayState.Ok ok => Display.Severity(ok.Snapshot.MaxPercent) switch
            {
                Severity.Red => Color.FromArgb(200, 40, 40),
                Severity.Orange => Color.FromArgb(220, 130, 20),
                _ => Color.FromArgb(30, 140, 60),
            },
            TrayState.Degraded => Color.FromArgb(110, 110, 110),
            _ => Color.FromArgb(70, 70, 70),
        };

        using var bmp = new Bitmap(sizePx, sizePx);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(back);
            g.FillEllipse(brush, 0, 0, sizePx - 1, sizePx - 1);

            var fontSize = text.Length switch { <= 1 => sizePx * 0.62f, 2 => sizePx * 0.52f, _ => sizePx * 0.40f };
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0.5f, sizePx, sizePx), fmt);
        }

        // Icon.FromHandle doesn't own the HICON; clone an owned copy, then destroy the original.
        var hIcon = bmp.GetHicon();
        try { return new RenderedIcon((Icon)Icon.FromHandle(hIcon).Clone()); }
        finally { NativeMethods.DestroyIcon(hIcon); }
    }
}
