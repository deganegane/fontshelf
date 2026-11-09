using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FontShelf.Controls
{
    /// <summary>Histogram s dva klizača za odabir raspona (kao u FontBaseu). Dvoklik vraća puni raspon.</summary>
    public class RangeHistogram : FrameworkElement
    {
        public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
            nameof(Values), typeof(double[]), typeof(RangeHistogram),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LowerProperty = DependencyProperty.Register(
            nameof(Lower), typeof(double), typeof(RangeHistogram),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty UpperProperty = DependencyProperty.Register(
            nameof(Upper), typeof(double), typeof(RangeHistogram),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
            nameof(Accent), typeof(Brush), typeof(RangeHistogram),
            new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MutedProperty = DependencyProperty.Register(
            nameof(Muted), typeof(Brush), typeof(RangeHistogram),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public double[] Values { get => (double[])GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
        public double Lower { get => (double)GetValue(LowerProperty); set => SetValue(LowerProperty, value); }
        public double Upper { get => (double)GetValue(UpperProperty); set => SetValue(UpperProperty, value); }
        public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
        public Brush Muted { get => (Brush)GetValue(MutedProperty); set => SetValue(MutedProperty, value); }

        private const double HandleZone = 12;
        private int _drag; // 0 = ništa, 1 = donji, 2 = gornji

        public RangeHistogram()
        {
            Height = 62;
            Cursor = Cursors.SizeWE;
            Focusable = false;
        }

        private static Brush WithOpacity(Brush b, double o)
        {
            var c = b.Clone();
            c.Opacity = o;
            c.Freeze();
            return c;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            double chartH = h - 12;
            var vals = Values;
            var on = WithOpacity(Accent, 0.55);
            var off = WithOpacity(Muted, 0.28);

            if (vals != null && vals.Length > 0)
            {
                double max = Math.Max(1, vals.Max());
                double bw = w / vals.Length;
                for (int i = 0; i < vals.Length; i++)
                {
                    if (vals[i] <= 0) continue;
                    double bh = Math.Max(1.5, vals[i] / max * (chartH - 2));
                    double center = (i + 0.5) / vals.Length;
                    var brush = center >= Lower && center <= Upper ? on : off;
                    dc.DrawRoundedRectangle(brush, null, new Rect(i * bw + 0.5, chartH - bh, Math.Max(1, bw - 1.5), bh), 1, 1);
                }
            }

            var basePen = new Pen(WithOpacity(Muted, 0.5), 1);
            dc.DrawLine(basePen, new Point(0, chartH + 0.5), new Point(w, chartH + 0.5));

            var sel = new Pen(Accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double xl = Lower * w, xu = Upper * w;
            dc.DrawLine(sel, new Point(xl, chartH + 0.5), new Point(xu, chartH + 0.5));

            var handlePen = new Pen(Accent, 1.5);
            foreach (var x in new[] { xl, xu })
            {
                dc.DrawLine(new Pen(WithOpacity(Accent, 0.6), 1), new Point(x, 2), new Point(x, chartH));
                dc.DrawEllipse(Brushes.White, handlePen, new Point(Math.Max(5, Math.Min(w - 5, x)), chartH + 0.5), 5, 5);
            }
        }

        private double ValueAt(Point p) => Math.Max(0, Math.Min(1, p.X / Math.Max(1, ActualWidth)));

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ClickCount == 2)
            {
                Lower = 0;
                Upper = 1;
                e.Handled = true;
                return;
            }
            var v = ValueAt(e.GetPosition(this));
            double dl = Math.Abs(v - Lower), du = Math.Abs(v - Upper);
            _drag = dl < du || (dl == du && v < Lower) ? 1 : 2;
            Apply(v);
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag != 0 && IsMouseCaptured) Apply(ValueAt(e.GetPosition(this)));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _drag = 0;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        private void Apply(double v)
        {
            v = Math.Round(v, 2);
            if (_drag == 1) Lower = Math.Min(v, Upper - 0.02);
            else if (_drag == 2) Upper = Math.Max(v, Lower + 0.02);
        }
    }
}
