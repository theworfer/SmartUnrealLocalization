using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Tiny control that paints a single wavy line across its width — used by
    /// <see cref="TaggedTextBlock"/> to underline character ranges flagged by
    /// the validators (TagMismatch, Whitespace, etc.).
    ///
    /// <para>
    /// Avalonia 12 has <c>TextDecoration</c> with stroke style but no built-in
    /// "wavy" variant; rolling our own keeps the renderer drift-free and lets
    /// us scale the wave amplitude and frequency to match the prototype's
    /// <c>text-decoration: underline wavy</c>.
    /// </para>
    /// </summary>
    public class WavyUnderline : Control
    {
        public static readonly StyledProperty<IBrush?> StrokeProperty =
            AvaloniaProperty.Register<WavyUnderline, IBrush?>(nameof(Stroke));

        public IBrush? Stroke
        {
            get => GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public WavyUnderline()
        {
            Height = 3;
            IsHitTestVisible = false;
        }

        static WavyUnderline()
        {
            // Repaint when the colour changes.
            AffectsRender<WavyUnderline>(StrokeProperty);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (Stroke is null) return;

            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w < 4) return;

            var pen = new Pen(Stroke, 1.25);
            var geo = new StreamGeometry();
            using (var sg = geo.Open())
            {
                // Wave anatomy: peaks alternate up / down, midpoint sits at the
                // vertical centre of the control. cycleW = 4 px per half-wave
                // gives ~visually similar density to the prototype's CSS wavy
                // underline at the default 12 px font size.
                const double cycleW = 4.0;
                double midY = h * 0.5;
                sg.BeginFigure(new Point(0, midY), false);

                bool up = true;
                for (double x = 0; x < w; x += cycleW)
                {
                    double endX = Math.Min(x + cycleW, w);
                    double peakX = (x + endX) * 0.5;
                    double peakY = up ? 0 : h;
                    sg.QuadraticBezierTo(new Point(peakX, peakY), new Point(endX, midY));
                    up = !up;
                }
            }
            context.DrawGeometry(null, pen, geo);
        }
    }
}
