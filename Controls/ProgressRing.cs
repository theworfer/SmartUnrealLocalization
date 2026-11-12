using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Circular progress indicator — a track ring with a percentage-filled arc on top.
    /// Used in the activity-bar Status icon (small, 22 px) and the Status panel hero
    /// card (large, 120 px).
    ///
    /// Render-only Control (no XAML) — simpler than UserControl + bindings for a pure
    /// drawing primitive. All four properties are StyledProperty so they participate
    /// in normal Avalonia bindings and invalidate on change.
    /// </summary>
    public class ProgressRing : Control
    {
        public static readonly StyledProperty<double> PercentageProperty =
            AvaloniaProperty.Register<ProgressRing, double>(nameof(Percentage), 0.0);

        public static readonly StyledProperty<double> StrokeThicknessProperty =
            AvaloniaProperty.Register<ProgressRing, double>(nameof(StrokeThickness), 3.0);

        public static readonly StyledProperty<IBrush?> StrokeBrushProperty =
            AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(StrokeBrush));

        public static readonly StyledProperty<IBrush?> TrackBrushProperty =
            AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(TrackBrush));

        /// <summary>Progress in [0, 100]. Values outside that range are clamped.</summary>
        public double Percentage
        {
            get => GetValue(PercentageProperty);
            set => SetValue(PercentageProperty, value);
        }

        public double StrokeThickness
        {
            get => GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        public IBrush? StrokeBrush
        {
            get => GetValue(StrokeBrushProperty);
            set => SetValue(StrokeBrushProperty, value);
        }

        public IBrush? TrackBrush
        {
            get => GetValue(TrackBrushProperty);
            set => SetValue(TrackBrushProperty, value);
        }

        static ProgressRing()
        {
            // Any visual property change → invalidate so Render() runs again.
            AffectsRender<ProgressRing>(
                PercentageProperty, StrokeThicknessProperty,
                StrokeBrushProperty, TrackBrushProperty);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            double size = Math.Min(Bounds.Width, Bounds.Height);
            if (size <= 0) return;

            double stroke = Math.Max(0.5, StrokeThickness);
            double radius = (size - stroke) / 2;
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            double pct = Math.Clamp(Percentage, 0, 100);

            // 1. Track (background ring)
            var trackBrush = TrackBrush;
            if (trackBrush != null)
            {
                var trackPen = new Pen(trackBrush, stroke);
                context.DrawEllipse(null, trackPen, center, radius, radius);
            }

            // 2. Progress arc on top — drawn via StreamGeometry with an ArcSegment.
            //    Starts at 12 o'clock and sweeps clockwise.
            var strokeBrush = StrokeBrush;
            if (strokeBrush == null || pct <= 0) return;

            // Visual floor: arcs shorter than half the stroke width look like vertical
            // tick marks (especially the awkward "blob" at 1-3%). Bump tiny percentages
            // up to a minimum sweep so they read as a recognisable rounded pill —
            // matches the prototype's strokeLinecap=round + min-visual-mark behaviour.
            double minSweepRadians = stroke / radius;             // ≈ stroke-width-worth of arc
            double sweepRadians = Math.Max(minSweepRadians, pct / 100.0 * 2 * Math.PI);
            // 100% becomes a closed circle — drop a tiny epsilon to avoid an empty arc bug.
            if (pct >= 100) sweepRadians = 2 * Math.PI - 0.0001;

            var startPoint = new Point(center.X, center.Y - radius);
            var endPoint = new Point(
                center.X + radius * Math.Sin(sweepRadians),
                center.Y - radius * Math.Cos(sweepRadians));

            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(startPoint, false);
                ctx.ArcTo(
                    endPoint,
                    new Size(radius, radius),
                    rotationAngle: 0,
                    isLargeArc: sweepRadians > Math.PI,
                    sweepDirection: SweepDirection.Clockwise);
                ctx.EndFigure(false);
            }
            // Round caps now look clean because the floor guarantees the arc length is
            // at least 1× stroke (no more two-cap-merging blob).
            var pen = new Pen(strokeBrush, stroke, lineCap: PenLineCap.Round);
            context.DrawGeometry(null, pen, sg);
        }
    }
}
