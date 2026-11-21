using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Lumen.UI
{
    /// <summary>
    /// 带描边的文本：先用黑色笔触描出字形轮廓，再在其上填充前景色。
    /// 用于桌面歌词「白字黑描边」——描边真正居中、均匀地包裹每个字，
    /// 而不是 DropShadowEffect 那种单一方向的偏移阴影。
    /// </summary>
    public sealed class OutlinedTextBlock : FrameworkElement
    {
        private string _text = string.Empty;
        private FontFamily _fontFamily = new FontFamily("Microsoft YaHei");
        private double _fontSize = 30;
        private FontWeight _fontWeight = FontWeights.Bold;
        private Brush _foreground = Brushes.White;
        private Brush _outline = Brushes.Black;
        private double _outlineThickness = 2;

        private Typeface _typeface;
        private Geometry _cachedGeometry;
        private double _cachedGeometryPpd;
        private Pen _cachedPen;

        public string Text
        {
            get { return _text; }
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(_text, normalized, StringComparison.Ordinal)) return;
                _text = normalized;
                _cachedGeometry = null;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        public FontFamily FontFamily
        {
            get { return _fontFamily; }
            set
            {
                var next = value ?? new FontFamily("Microsoft YaHei");
                if (Equals(_fontFamily, next)) return;
                _fontFamily = next;
                _typeface = null;
                _cachedGeometry = null;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        public double FontSize
        {
            get { return _fontSize; }
            set
            {
                if (_fontSize == value) return;
                _fontSize = value;
                _cachedGeometry = null;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        public FontWeight FontWeight
        {
            get { return _fontWeight; }
            set
            {
                if (_fontWeight == value) return;
                _fontWeight = value;
                _typeface = null;
                _cachedGeometry = null;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        public Brush Foreground
        {
            get { return _foreground; }
            set
            {
                if (Equals(_foreground, value)) return;
                _foreground = value;
                InvalidateVisual();
            }
        }

        public Brush Outline
        {
            get { return _outline; }
            set
            {
                if (Equals(_outline, value)) return;
                _outline = value;
                _cachedPen = null;
                InvalidateVisual();
            }
        }

        public double OutlineThickness
        {
            get { return _outlineThickness; }
            set
            {
                double next = value < 0 ? 0 : value;
                if (_outlineThickness == next) return;
                _outlineThickness = next;
                _cachedPen = null;
                _cachedGeometry = null;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        private Typeface CurrentTypeface
        {
            get
            {
                if (_typeface == null)
                    _typeface = new Typeface(_fontFamily, FontStyles.Normal, _fontWeight, FontStretches.Normal);
                return _typeface;
            }
        }

        private double GetPixelsPerDip()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.PixelsPerDip > 0) return dpi.PixelsPerDip;

            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
                return source.CompositionTarget.TransformToDevice.M11;

            return 1.0;
        }

        private FormattedText BuildFormattedText(double ppd)
        {
            return new FormattedText(
                _text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                CurrentTypeface,
                _fontSize,
                _foreground ?? Brushes.White,
                ppd);
        }

        private Geometry EnsureGeometry()
        {
            double ppd = GetPixelsPerDip();
            if (_cachedGeometry != null && Math.Abs(_cachedGeometryPpd - ppd) < 1e-6)
                return _cachedGeometry;

            var ft = BuildFormattedText(ppd);
            double half = _outlineThickness / 2.0;
            var geometry = ft.BuildGeometry(new Point(half, half));
            if (geometry != null && geometry.CanFreeze)
                geometry.Freeze();

            _cachedGeometry = geometry;
            _cachedGeometryPpd = ppd;
            return geometry;
        }

        private Pen EnsurePen()
        {
            if (_cachedPen != null) return _cachedPen;
            if (_outline == null || _outlineThickness <= 0) return null;

            var pen = new Pen(_outline, _outlineThickness)
            {
                LineJoin = PenLineJoin.Round
            };
            if (pen.CanFreeze) pen.Freeze();

            _cachedPen = pen;
            return pen;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _cachedGeometry = null;
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var ft = BuildFormattedText(GetPixelsPerDip());
            return new Size(
                ft.WidthIncludingTrailingWhitespace + _outlineThickness,
                ft.Height + _outlineThickness);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (string.IsNullOrEmpty(_text)) return;

            var geometry = EnsureGeometry();
            if (geometry == null) return;

            // 先描边（轮廓），再填充（字形），描边会被字形盖住内侧一半，
            // 只剩外侧一半成为均匀的「描边」。
            var pen = EnsurePen();
            if (pen != null) dc.DrawGeometry(null, pen, geometry);
            dc.DrawGeometry(_foreground, null, geometry);
        }
    }
}
