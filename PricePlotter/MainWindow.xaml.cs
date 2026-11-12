using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace mine
{
    public readonly record struct PricePoint(DateTimeOffset Timestamp, double UtcTicks, decimal Price);

    public enum SnapMode
    {
        None,
        Buy,
        Sell
    }

    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void BtnResetView_Click(object sender, RoutedEventArgs e)
        {
            Chart?.ResetView();
        }

        private void ChkAutoFitY_Changed(object sender, RoutedEventArgs e)
        {
            if (Chart != null) Chart.AutoFitY = ChkAutoFitY.IsChecked == true;
        }

        private void CmbSnapTo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Chart == null) return;
            Chart.SnapTo = CmbSnapTo.SelectedIndex switch
            {
                1 => SnapMode.Buy,
                2 => SnapMode.Sell,
                _ => SnapMode.None
            };
        }

        private void Chart_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Chart_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

            List<PricePoint>? buyPoints = null;
            List<PricePoint>? sellPoints = null;

            foreach (string filePath in files)
            {
                string fileName = Path.GetFileName(filePath);
                if (string.Equals(fileName, "buy_price_log.csv", StringComparison.OrdinalIgnoreCase))
                {
                    buyPoints = ParseLogFile(filePath);
                }
                else if (string.Equals(fileName, "sell_price_log.csv", StringComparison.OrdinalIgnoreCase))
                {
                    sellPoints = ParseLogFile(filePath);
                }
            }

            if (buyPoints != null || sellPoints != null)
            {
                Chart.SetData(buyPoints, sellPoints);
            }
        }

        private static List<PricePoint> ParseLogFile(string path)
        {
            var list = new List<PricePoint>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int commaIdx = line.IndexOf(',');
                if (commaIdx <= 0) continue;
                ReadOnlySpan<char> tsSpan = line.AsSpan(0, commaIdx).Trim();
                ReadOnlySpan<char> priceSpan = line.AsSpan(commaIdx + 1).Trim();
                if (DateTimeOffset.TryParse(tsSpan, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dto) && decimal.TryParse(priceSpan, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price))
                {
                    list.Add(new PricePoint(dto, dto.UtcTicks, price));
                }
            }
            list.Sort((a, b) => a.UtcTicks.CompareTo(b.UtcTicks));
            return list;
        }
    }

    public sealed class PriceChartControl : FrameworkElement
    {
        private List<PricePoint> _buyPoints = new();
        private List<PricePoint> _sellPoints = new();
        private double _minX;
        private double _maxX;
        private double _minY;
        private double _maxY;
        private double _dataMinX;
        private double _dataMaxX;
        private double _dataMinY;
        private double _dataMaxY;
        private bool _hasData;
        private bool _autoFitY = true;
        private SnapMode _snapTo = SnapMode.None;
        private bool _isDragging;
        private Point _lastMouseDownPos;
        private Point? _hoverPos;

        private static readonly double MaxGapTicks = TimeSpan.FromMinutes(15).Ticks;
        private const double LeftMargin = 16.0;
        private const double RightMargin = 85.0;
        private const double TopMargin = 16.0;
        private const double BottomMargin = 45.0;

        private static readonly Brush BgBrush;
        private static readonly Pen GridPen;
        private static readonly Pen AxisPen;
        private static readonly Pen BuyPen;
        private static readonly Pen SellPen;
        private static readonly Brush BuyBrush;
        private static readonly Brush SellBrush;
        private static readonly Pen CrosshairPen;
        private static readonly Brush LabelBrush;
        private static readonly Brush TooltipBgBrush;
        private static readonly Pen TooltipBorderPen;
        private static readonly Typeface UiTypeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        static PriceChartControl()
        {
            BgBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            BgBrush.Freeze();
            GridPen = new Pen(new SolidColorBrush(Color.FromRgb(45, 45, 48)), 1.0);
            GridPen.Freeze();
            AxisPen = new Pen(new SolidColorBrush(Color.FromRgb(80, 80, 85)), 1.0);
            AxisPen.Freeze();
            BuyBrush = new SolidColorBrush(Color.FromRgb(0, 230, 118));
            BuyBrush.Freeze();
            SellBrush = new SolidColorBrush(Color.FromRgb(255, 82, 82));
            SellBrush.Freeze();
            BuyPen = new Pen(BuyBrush, 1.8);
            BuyPen.Freeze();
            SellPen = new Pen(SellBrush, 1.8);
            SellPen.Freeze();
            CrosshairPen = new Pen(new SolidColorBrush(Color.FromRgb(140, 140, 150)), 1.0) { DashStyle = DashStyles.Dash };
            CrosshairPen.Freeze();
            LabelBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200));
            LabelBrush.Freeze();
            TooltipBgBrush = new SolidColorBrush(Color.FromArgb(230, 37, 37, 38));
            TooltipBgBrush.Freeze();
            TooltipBorderPen = new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 110)), 1.0);
            TooltipBorderPen.Freeze();
        }

        public PriceChartControl()
        {
            ClipToBounds = true;
            Focusable = true;
        }

        public bool AutoFitY
        {
            get => _autoFitY;
            set
            {
                _autoFitY = value;
                if (_autoFitY && _hasData) RecalculateAutoFitY();
                InvalidateVisual();
            }
        }

        public SnapMode SnapTo
        {
            get => _snapTo;
            set
            {
                _snapTo = value;
                InvalidateVisual();
            }
        }

        public void SetData(List<PricePoint>? buyPoints, List<PricePoint>? sellPoints)
        {
            if (buyPoints != null) _buyPoints = buyPoints;
            if (sellPoints != null) _sellPoints = sellPoints;

            var all = _buyPoints.Concat(_sellPoints).ToList();
            if (all.Count == 0)
            {
                _hasData = false;
                InvalidateVisual();
                return;
            }

            _hasData = true;
            double rawMinX = all.Min(p => p.UtcTicks);
            double rawMaxX = all.Max(p => p.UtcTicks);
            if (rawMaxX <= rawMinX)
            {
                _dataMinX = rawMinX - TimeSpan.FromMinutes(5).Ticks;
                _dataMaxX = rawMaxX + TimeSpan.FromMinutes(5).Ticks;
            }
            else
            {
                double padX = (rawMaxX - rawMinX) * 0.03;
                _dataMinX = rawMinX - padX;
                _dataMaxX = rawMaxX + padX;
            }

            double rawMinY = (double)all.Min(p => p.Price);
            double rawMaxY = (double)all.Max(p => p.Price);
            if (Math.Abs(rawMaxY - rawMinY) < 1e-6)
            {
                _dataMinY = rawMinY - 1.0;
                _dataMaxY = rawMaxY + 1.0;
            }
            else
            {
                double padY = (rawMaxY - rawMinY) * 0.08;
                _dataMinY = rawMinY - padY;
                _dataMaxY = rawMaxY + padY;
            }

            ResetView();
        }

        public void ResetView()
        {
            if (!_hasData) return;
            _minX = _dataMinX;
            _maxX = _dataMaxX;
            if (_autoFitY)
            {
                RecalculateAutoFitY();
            }
            else
            {
                _minY = _dataMinY;
                _maxY = _dataMaxY;
            }
            InvalidateVisual();
        }

        private void ClampXBounds()
        {
            double maxRange = _dataMaxX - _dataMinX;
            double curRange = _maxX - _minX;
            if (curRange >= maxRange)
            {
                _minX = _dataMinX;
                _maxX = _dataMaxX;
                return;
            }
            if (_minX < _dataMinX)
            {
                _minX = _dataMinX;
                _maxX = _minX + curRange;
            }
            if (_maxX > _dataMaxX)
            {
                _maxX = _dataMaxX;
                _minX = _maxX - curRange;
            }
        }

        private void ClampYBounds()
        {
            double maxRange = _dataMaxY - _dataMinY;
            double curRange = _maxY - _minY;
            if (curRange >= maxRange)
            {
                _minY = _dataMinY;
                _maxY = _dataMaxY;
                return;
            }
            if (_minY < _dataMinY)
            {
                _minY = _dataMinY;
                _maxY = _minY + curRange;
            }
            if (_maxY > _dataMaxY)
            {
                _maxY = _dataMaxY;
                _minY = _maxY - curRange;
            }
        }

        private void RecalculateAutoFitY()
        {
            if (!_hasData) return;

            decimal minP = decimal.MaxValue;
            decimal maxP = decimal.MinValue;
            bool found = false;

            void ScanSeries(List<PricePoint> series)
            {
                for (int i = 0; i < series.Count; i++)
                {
                    var pt = series[i];
                    if (pt.UtcTicks >= _minX && pt.UtcTicks <= _maxX)
                    {
                        if (pt.Price < minP) minP = pt.Price;
                        if (pt.Price > maxP) maxP = pt.Price;
                        found = true;
                    }
                    else if (i + 1 < series.Count && series[i].UtcTicks < _minX && series[i + 1].UtcTicks > _minX && (series[i + 1].UtcTicks - series[i].UtcTicks) <= MaxGapTicks)
                    {
                        if (pt.Price < minP) minP = pt.Price;
                        if (pt.Price > maxP) maxP = pt.Price;
                        if (series[i + 1].Price < minP) minP = series[i + 1].Price;
                        if (series[i + 1].Price > maxP) maxP = series[i + 1].Price;
                        found = true;
                    }
                }
            }

            ScanSeries(_buyPoints);
            ScanSeries(_sellPoints);

            if (!found) return;

            double minD = (double)minP;
            double maxD = (double)maxP;
            if (Math.Abs(maxD - minD) < 1e-6)
            {
                _minY = minD - 1.0;
                _maxY = maxD + 1.0;
            }
            else
            {
                double padY = (maxD - minD) * 0.08;
                _minY = minD - padY;
                _maxY = maxD + padY;
            }
        }

        private Rect GetPlotRect()
        {
            double w = Math.Max(1.0, ActualWidth - LeftMargin - RightMargin);
            double h = Math.Max(1.0, ActualHeight - TopMargin - BottomMargin);
            return new Rect(LeftMargin, TopMargin, w, h);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            _isDragging = true;
            _lastMouseDownPos = e.GetPosition(this);
            CaptureMouse();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_isDragging)
            {
                _isDragging = false;
                ReleaseMouseCapture();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverPos = null;
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point pos = e.GetPosition(this);
            _hoverPos = pos;

            if (_isDragging && _hasData)
            {
                Rect plot = GetPlotRect();
                double dx = pos.X - _lastMouseDownPos.X;
                double dy = pos.Y - _lastMouseDownPos.Y;
                _lastMouseDownPos = pos;

                double rangeX = _maxX - _minX;
                double deltaTicks = (dx / plot.Width) * rangeX;
                _minX -= deltaTicks;
                _maxX -= deltaTicks;

                if (_autoFitY)
                {
                    RecalculateAutoFitY();
                }
                else
                {
                    double rangeY = _maxY - _minY;
                    double deltaPrice = (dy / plot.Height) * rangeY;
                    _minY += deltaPrice;
                    _maxY += deltaPrice;
                }
            }
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_hasData) return;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (!ctrl && !shift) return;

            Rect plot = GetPlotRect();
            Point pos = e.GetPosition(this);
            double zoomFactor = e.Delta > 0 ? 0.82 : 1.22;

            if (ctrl)
            {
                double ratioX = Math.Clamp((pos.X - plot.Left) / plot.Width, 0.0, 1.0);
                double anchorX = _minX + ratioX * (_maxX - _minX);
                double newRangeX = Math.Max((_maxX - _minX) * zoomFactor, TimeSpan.FromSeconds(2).Ticks);
                _minX = anchorX - ratioX * newRangeX;
                _maxX = _minX + newRangeX;

                if (_autoFitY) RecalculateAutoFitY();
            }

            if (shift && !_autoFitY)
            {
                double ratioY = Math.Clamp((plot.Bottom - pos.Y) / plot.Height, 0.0, 1.0);
                double anchorY = _minY + ratioY * (_maxY - _minY);
                double newRangeY = Math.Max((_maxY - _minY) * zoomFactor, 0.0001);
                _minY = anchorY - ratioY * newRangeY;
                _maxY = _minY + newRangeY;
            }

            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(BgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

            Rect plot = GetPlotRect();
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            if (!_hasData)
            {
                var emptyText = new FormattedText("Drag and drop buy_price_log.csv and/or sell_price_log.csv onto this area", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 14, LabelBrush, dpi);
                dc.DrawText(emptyText, new Point(plot.Left + (plot.Width - emptyText.Width) / 2.0, plot.Top + (plot.Height - emptyText.Height) / 2.0));
                return;
            }

            DrawGridAndAxes(dc, plot, dpi);

            dc.PushClip(new RectangleGeometry(plot));
            DrawSeries(dc, plot, _buyPoints, BuyPen, BuyBrush);
            DrawSeries(dc, plot, _sellPoints, SellPen, SellBrush);
            dc.Pop();

            if (_hoverPos is Point hp && plot.Contains(hp))
            {
                DrawCrosshairAndTooltip(dc, plot, hp, dpi);
            }
        }

        private void DrawGridAndAxes(DrawingContext dc, Rect plot, double dpi)
        {
            int ySteps = 6;
            for (int i = 0; i <= ySteps; i++)
            {
                double ratio = (double)i / ySteps;
                double y = plot.Bottom - ratio * plot.Height;
                double val = _minY + ratio * (_maxY - _minY);
                dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                var ft = new FormattedText(val.ToString("F3", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 11, LabelBrush, dpi);
                dc.DrawText(ft, new Point(plot.Right + 6, y - ft.Height / 2.0));
            }

            int xSteps = Math.Max(2, (int)(plot.Width / 160));
            for (int i = 0; i <= xSteps; i++)
            {
                double ratio = (double)i / xSteps;
                double x = plot.Left + ratio * plot.Width;
                long ticks = (long)(_minX + ratio * (_maxX - _minX));
                dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                if (ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks)
                {
                    var dt = new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime();
                    string label = $"{dt:yyyy-MM-dd}\n{dt:HH:mm:ss}";
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 11, LabelBrush, dpi);
                    dc.DrawText(ft, new Point(x - ft.Width / 2.0, plot.Bottom + 6));
                }
            }

            dc.DrawRectangle(null, AxisPen, plot);
        }

        private void DrawSeries(DrawingContext dc, Rect plot, List<PricePoint> series, Pen linePen, Brush pointBrush)
        {
            if (series.Count == 0) return;

            var geom = new StreamGeometry();
            using (StreamGeometryContext ctx = geom.Open())
            {
                for (int i = 0; i < series.Count; i++)
                {
                    double x = plot.Left + ((series[i].UtcTicks - _minX) / (_maxX - _minX)) * plot.Width;
                    double y = plot.Bottom - (((double)series[i].Price - _minY) / (_maxY - _minY)) * plot.Height;
                    var pt = new Point(x, y);
                    if (i == 0 || (series[i].UtcTicks - series[i - 1].UtcTicks) > MaxGapTicks)
                    {
                        ctx.BeginFigure(pt, false, false);
                    }
                    else
                    {
                        ctx.LineTo(pt, true, false);
                    }
                }
            }
            geom.Freeze();
            dc.DrawGeometry(null, linePen, geom);

            for (int i = 0; i < series.Count; i++)
            {
                if (series[i].UtcTicks < _minX || series[i].UtcTicks > _maxX) continue;
                double x = plot.Left + ((series[i].UtcTicks - _minX) / (_maxX - _minX)) * plot.Width;
                double y = plot.Bottom - (((double)series[i].Price - _minY) / (_maxY - _minY)) * plot.Height;
                dc.DrawEllipse(pointBrush, null, new Point(x, y), 3.0, 3.0);
            }
        }

        private void DrawCrosshairAndTooltip(DrawingContext dc, Rect plot, Point hp, double dpi)
        {
            double ratioX = (hp.X - plot.Left) / plot.Width;
            double ratioY = (plot.Bottom - hp.Y) / plot.Height;
            double targetTicks = _minX + ratioX * (_maxX - _minX);
            double cursorPrice = _minY + ratioY * (_maxY - _minY);
            double drawX = hp.X;
            double drawY = hp.Y;

            List<PricePoint>? snapSeries = _snapTo switch
            {
                SnapMode.Buy => _buyPoints,
                SnapMode.Sell => _sellPoints,
                _ => null
            };

            if (snapSeries is { Count: > 0 } && FindNearestPoint(snapSeries, targetTicks) is PricePoint snappedPt)
            {
                targetTicks = snappedPt.UtcTicks;
                cursorPrice = (double)snappedPt.Price;
                drawX = plot.Left + ((targetTicks - _minX) / (_maxX - _minX)) * plot.Width;
                drawY = plot.Bottom - ((cursorPrice - _minY) / (_maxY - _minY)) * plot.Height;
            }

            dc.PushClip(new RectangleGeometry(plot));
            dc.DrawLine(CrosshairPen, new Point(drawX, plot.Top), new Point(drawX, plot.Bottom));
            dc.DrawLine(CrosshairPen, new Point(plot.Left, drawY), new Point(plot.Right, drawY));
            dc.Pop();

            long cursorTicks = (long)targetTicks;
            decimal? buyAtCursor = (_snapTo == SnapMode.Buy && snapSeries is { Count: > 0 } && FindNearestPoint(_buyPoints, targetTicks) is PricePoint bp) ? bp.Price : InterpolatePriceAt(_buyPoints, cursorTicks);
            decimal? sellAtCursor = (_snapTo == SnapMode.Sell && snapSeries is { Count: > 0 } && FindNearestPoint(_sellPoints, targetTicks) is PricePoint sp) ? sp.Price : InterpolatePriceAt(_sellPoints, cursorTicks);

            string timeStr = (cursorTicks >= DateTimeOffset.MinValue.UtcTicks && cursorTicks <= DateTimeOffset.MaxValue.UtcTicks) ? new DateTimeOffset(cursorTicks, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) : "N/A";
            string buyStr = buyAtCursor.HasValue ? buyAtCursor.Value.ToString("F3", CultureInfo.InvariantCulture) : "N/A";
            string sellStr = sellAtCursor.HasValue ? sellAtCursor.Value.ToString("F3", CultureInfo.InvariantCulture) : "N/A";
            string spreadStr = (buyAtCursor.HasValue && sellAtCursor.HasValue) ? (sellAtCursor.Value - buyAtCursor.Value).ToString("F3", CultureInfo.InvariantCulture) : "N/A";

            string tooltipText = $"Time:   {timeStr}\nCursor: {cursorPrice.ToString("F3", CultureInfo.InvariantCulture)}\nBuy:    {buyStr}\nSell:   {sellStr}\nSpread: {spreadStr}";
            var ft = new FormattedText(tooltipText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 12, LabelBrush, dpi);

            double boxW = ft.Width + 16;
            double boxH = ft.Height + 12;
            double anchorX = Math.Clamp(drawX, plot.Left, plot.Right);
            double anchorY = Math.Clamp(drawY, plot.Top, plot.Bottom);
            double boxX = anchorX + 14;
            double boxY = anchorY + 14;
            if (boxX + boxW > plot.Right) boxX = anchorX - boxW - 14;
            if (boxY + boxH > plot.Bottom) boxY = anchorY - boxH - 14;

            dc.DrawRoundedRectangle(TooltipBgBrush, TooltipBorderPen, new Rect(boxX, boxY, boxW, boxH), 4, 4);
            dc.DrawText(ft, new Point(boxX + 8, boxY + 6));
        }

        private static PricePoint? FindNearestPoint(List<PricePoint> series, double targetTicks)
        {
            if (series.Count == 0) return null;
            if (targetTicks <= series[0].UtcTicks) return series[0];
            if (targetTicks >= series[^1].UtcTicks) return series[^1];

            int low = 0;
            int high = series.Count - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                double midTicks = series[mid].UtcTicks;
                if (Math.Abs(midTicks - targetTicks) < 1.0) return series[mid];
                if (midTicks < targetTicks) low = mid + 1;
                else high = mid - 1;
            }

            int idx1 = Math.Max(0, high);
            int idx2 = Math.Min(series.Count - 1, low);
            return Math.Abs(series[idx1].UtcTicks - targetTicks) <= Math.Abs(series[idx2].UtcTicks - targetTicks) ? series[idx1] : series[idx2];
        }

        private static decimal? InterpolatePriceAt(List<PricePoint> series, double targetTicks)
        {
            if (series.Count == 0) return null;
            if (targetTicks < series[0].UtcTicks || targetTicks > series[^1].UtcTicks) return null;
            if (series.Count == 1) return Math.Abs(series[0].UtcTicks - targetTicks) < 1.0 ? series[0].Price : null;

            int low = 0;
            int high = series.Count - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                double midTicks = series[mid].UtcTicks;
                if (Math.Abs(midTicks - targetTicks) < 1.0) return series[mid].Price;
                if (midTicks < targetTicks) low = mid + 1;
                else high = mid - 1;
            }

            int idx1 = Math.Max(0, high);
            int idx2 = Math.Min(series.Count - 1, low);
            if (idx1 == idx2) return series[idx1].Price;

            double t1 = series[idx1].UtcTicks;
            double t2 = series[idx2].UtcTicks;
            if ((t2 - t1) > MaxGapTicks) return null;
            if (Math.Abs(t2 - t1) < 1.0) return series[idx1].Price;

            double fraction = (targetTicks - t1) / (t2 - t1);
            double p1 = (double)series[idx1].Price;
            double p2 = (double)series[idx2].Price;
            return (decimal)(p1 + fraction * (p2 - p1));
        }
    }
}