using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace FontShelf
{
    /// <summary>
    /// Prikaz teksta isključivo znakovima zadanog fonta (bez zamjenskih fontova sustava):
    /// znak koji u fontu ne postoji crta se kao prazan okvir, pa se odmah vidi što nedostaje.
    /// </summary>
    public class GlyphPreview : FrameworkElement
    {
        private string _text = "";
        private double _size = 24;
        private GlyphTypeface _face;
        private string _brushKey = "TextBrush";
        private readonly List<(List<ushort> idx, List<double> adv, List<bool> missing)> _lines = new List<(List<ushort>, List<double>, List<bool>)>();

        public string Text { get => _text; set { _text = value ?? ""; InvalidateMeasure(); InvalidateVisual(); } }
        public double FontSize { get => _size; set { _size = value; InvalidateMeasure(); InvalidateVisual(); } }
        public string BrushKey { get => _brushKey; set { _brushKey = value; InvalidateVisual(); } }
        public GlyphTypeface Face { get => _face; set { _face = value; InvalidateMeasure(); InvalidateVisual(); } }

        private double LineHeight => _size * 1.5;

        private void Layout(double maxWidth)
        {
            _lines.Clear();
            if (_face == null || string.IsNullOrEmpty(_text)) return;
            var cur = (idx: new List<ushort>(), adv: new List<double>(), missing: new List<bool>());
            double x = 0;
            double space = Adv(' ', out _, out _);
            foreach (var word in _text.Split(' '))
            {
                var wi = new List<ushort>(); var wa = new List<double>(); var wm = new List<bool>();
                double ww = 0;
                foreach (var ch in word)
                {
                    double a = Adv(ch, out var gi, out var miss);
                    wi.Add(gi); wa.Add(a); wm.Add(miss); ww += a;
                }
                if (cur.idx.Count > 0 && x + space + ww > maxWidth)
                {
                    _lines.Add(cur);
                    cur = (new List<ushort>(), new List<double>(), new List<bool>());
                    x = 0;
                }
                if (cur.idx.Count > 0)
                {
                    Adv(' ', out var sg, out _);
                    cur.idx.Add(sg); cur.adv.Add(space); cur.missing.Add(false);
                    x += space;
                }
                cur.idx.AddRange(wi); cur.adv.AddRange(wa); cur.missing.AddRange(wm);
                x += ww;
            }
            if (cur.idx.Count > 0) _lines.Add(cur);
        }

        private double Adv(char ch, out ushort gi, out bool missing)
        {
            missing = !_face.CharacterToGlyphMap.TryGetValue(ch, out gi);
            if (missing)
            {
                gi = 0;
                return ch == ' ' ? _size * 0.28 : _size * 0.6;
            }
            return _face.AdvanceWidths.TryGetValue(gi, out var a) ? a * _size : _size * 0.5;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) ? 4000 : availableSize.Width;
            Layout(w);
            double max = 0;
            foreach (var l in _lines) { double s = 0; foreach (var a in l.adv) s += a; max = Math.Max(max, s); }
            return new Size(Math.Min(w, max), Math.Max(1, _lines.Count) * LineHeight);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_face == null) return;
            var brush = TryFindResource(_brushKey) as Brush ?? Brushes.White;
            var pen = new Pen(TryFindResource("WarnBrush") as Brush ?? Brushes.Orange, 1);
            float ppd = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int li = 0; li < _lines.Count; li++)
            {
                var l = _lines[li];
                double baseline = li * LineHeight + _size * 1.12;
                double x = 0;
                // crtaj u odsječcima: postojeće znakove kao GlyphRun, nepostojeće kao okvir
                int i = 0;
                while (i < l.idx.Count)
                {
                    if (l.missing[i])
                    {
                        if (l.adv[i] > _size * 0.3)
                            dc.DrawRectangle(null, pen, new Rect(x + _size * 0.08, baseline - _size * 0.62, l.adv[i] - _size * 0.16, _size * 0.62));
                        x += l.adv[i]; i++;
                        continue;
                    }
                    int j = i; double w = 0;
                    var idx = new List<ushort>(); var adv = new List<double>();
                    while (j < l.idx.Count && !l.missing[j]) { idx.Add(l.idx[j]); adv.Add(l.adv[j]); w += l.adv[j]; j++; }
                    try
                    {
                        var run = new GlyphRun(_face, 0, false, _size, ppd, idx, new Point(x, baseline), adv,
                            null, null, null, null, null, null);
                        dc.DrawGlyphRun(brush, run);
                    }
                    catch { }
                    x += w; i = j;
                }
            }
        }
    }
}
