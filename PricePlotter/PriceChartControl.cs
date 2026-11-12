using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace mine
{
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
        private const double BottomMargin = 58.0;

        private static readonly Brush BgBrush;
        private static readonly Brush MarketBandBrush;
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
        private static readonly Pen MajorGridPen;
        private bool _autoFollowLive = true;
        private static readonly Typeface UiTypeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        static PriceChartControl()
        {
            BgBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            BgBrush.Freeze();
            MarketBandBrush = new SolidColorBrush(Color.FromArgb(30, 100, 100, 255));
            MarketBandBrush.Freeze();
            GridPen = new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 1.0);
            GridPen.Freeze();
            MajorGridPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 2.0);
            MajorGridPen.Freeze();
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

        public bool AutoFollowLive
        {
            get => _autoFollowLive;
            set => _autoFollowLive = value;
        }

        public void AddLivePoint(PricePoint pt, bool isBuy)
        {
            List<PricePoint> target = isBuy ? _buyPoints : _sellPoints;
            if (target.Count == 0 || pt.PlotTicks >= target[^1].PlotTicks)
            {
                target.Add(pt);
            }
            else
            {
                int idx = target.FindIndex(p => p.PlotTicks > pt.PlotTicks);
                if (idx >= 0) target.Insert(idx, pt);
                else target.Add(pt);
            }

            if (!_hasData)
            {
                _hasData = true;
                double halfSpan = TimeSpan.FromMinutes(15).Ticks;
                _dataMinX = pt.PlotTicks - halfSpan;
                _dataMaxX = pt.PlotTicks + halfSpan;
                _minX = _dataMinX;
                _maxX = _dataMaxX;

                double pVal = (double)pt.Price;
                _dataMinY = pVal - 1.0;
                _dataMaxY = pVal + 1.0;
                _minY = _dataMinY;
                _maxY = _dataMaxY;

                if (_autoFitY) RecalculateAutoFitY();
                InvalidateVisual();
                return;
            }

            if (pt.PlotTicks < _dataMinX) _dataMinX = pt.PlotTicks;
            if (pt.PlotTicks > _dataMaxX) _dataMaxX = pt.PlotTicks;
            double priceD = (double)pt.Price;
            if (priceD - 0.08 < _dataMinY) _dataMinY = priceD - 0.08;
            if (priceD + 0.08 > _dataMaxY) _dataMaxY = priceD + 0.08;

            if (_autoFollowLive)
            {
                double currentRangeX = _maxX - _minX;
                double rightPad = currentRangeX * 0.05;
                if (pt.PlotTicks > _maxX - rightPad || pt.PlotTicks < _minX)
                {
                    _maxX = pt.PlotTicks + rightPad;
                    _minX = _maxX - currentRangeX;
                }
            }

            if (_autoFitY)
            {
                RecalculateAutoFitY();
            }
            else if (_autoFollowLive)
            {
                double currentRangeY = _maxY - _minY;
                if (priceD < _minY || priceD > _maxY)
                {
                    _minY = priceD - currentRangeY / 2.0;
                    _maxY = priceD + currentRangeY / 2.0;
                }
            }

            InvalidateVisual();
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
            double rawMinX = all.Min(p => p.PlotTicks);
            double rawMaxX = all.Max(p => p.PlotTicks);
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
                    if (pt.PlotTicks >= _minX && pt.PlotTicks <= _maxX)
                    {
                        if (pt.Price < minP) minP = pt.Price;
                        if (pt.Price > maxP) maxP = pt.Price;
                        found = true;
                    }
                    else if (i + 1 < series.Count && series[i].PlotTicks < _minX && series[i + 1].PlotTicks > _minX && (series[i + 1].UtcTicks - series[i].UtcTicks) <= MaxGapTicks)
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
            double rangeY = _maxY - _minY;
            if (rangeY > 0)
            {
                double yStep = 0.25;
                while (rangeY / yStep > Math.Max(4.0, plot.Height / 18.0))
                {
                    yStep *= 2.0;
                }

                long firstIndex = (long)Math.Ceiling(_minY / yStep - 1e-9);
                long lastIndex = (long)Math.Floor(_maxY / yStep + 1e-9);
                for (long idx = firstIndex; idx <= lastIndex; idx++)
                {
                    double val = idx * yStep;
                    double ratio = (val - _minY) / rangeY;
                    double y = plot.Bottom - ratio * plot.Height;
                    bool isWholePound = Math.Abs(val - Math.Round(val)) < 1e-6;
                    dc.DrawLine(isWholePound ? MajorGridPen : GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                    string priceLabel = isWholePound ? $"£{val.ToString("F0", CultureInfo.InvariantCulture)}" : $"£{val.ToString("F2", CultureInfo.InvariantCulture)}";
                    var ft = new FormattedText(priceLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 11, LabelBrush, dpi);
                    dc.DrawText(ft, new Point(plot.Right + 6, y - ft.Height / 2.0));
                }
            }

            double rangeX = _maxX - _minX;
            if (rangeX > 0)
            {
                DateTime startLocalDate = WeekdayTimeAxis.ToLocalDateTime(_minX).Date;
                DateTime endLocalDate = WeekdayTimeAxis.ToLocalDateTime(_maxX).Date.AddDays(1);

                double visibleWeekdays = rangeX / TimeSpan.FromDays(1).Ticks;
                int dayStep = Math.Max(1, (int)Math.Ceiling(visibleWeekdays / Math.Max(2.0, plot.Width / 110.0)));
                bool showMarketBands = visibleWeekdays <= Math.Max(20.0, plot.Width / 12.0);

                int weekdayIndex = 0;
                for (DateTime day = startLocalDate; day <= endLocalDate; day = day.AddDays(1))
                {
                    if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday) continue;

                    if (showMarketBands)
                    {
                        double openPlotTicks = WeekdayTimeAxis.ToPlotTicks(day.AddHours(8));
                        double closePlotTicks = WeekdayTimeAxis.ToPlotTicks(day.AddHours(16).AddMinutes(30));
                        double x1 = plot.Left + ((openPlotTicks - _minX) / rangeX) * plot.Width;
                        double x2 = plot.Left + ((closePlotTicks - _minX) / rangeX) * plot.Width;
                        double clampedX1 = Math.Clamp(x1, plot.Left, plot.Right);
                        double clampedX2 = Math.Clamp(x2, plot.Left, plot.Right);
                        if (clampedX2 > clampedX1)
                        {
                            dc.DrawRectangle(MarketBandBrush, null, new Rect(clampedX1, plot.Top, clampedX2 - clampedX1, plot.Height));
                        }
                    }

                    if (weekdayIndex % dayStep == 0)
                    {
                        DateTime middayLocal = day.AddHours(12);
                        double middayPlotTicks = WeekdayTimeAxis.ToPlotTicks(middayLocal);
                        if (middayPlotTicks >= _minX && middayPlotTicks <= _maxX)
                        {
                            double x = plot.Left + ((middayPlotTicks - _minX) / rangeX) * plot.Width;
                            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                            string label = $"{middayLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\n12:00\n{middayLocal.ToString("ddd", CultureInfo.InvariantCulture)}";
                            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 11, LabelBrush, dpi) { TextAlignment = TextAlignment.Center };
                            dc.DrawText(ft, new Point(x, plot.Bottom + 5));
                        }
                    }

                    weekdayIndex++;
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
                    double x = plot.Left + ((series[i].PlotTicks - _minX) / (_maxX - _minX)) * plot.Width;
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
                if (series[i].PlotTicks < _minX || series[i].PlotTicks > _maxX) continue;
                double x = plot.Left + ((series[i].PlotTicks - _minX) / (_maxX - _minX)) * plot.Width;
                double y = plot.Bottom - (((double)series[i].Price - _minY) / (_maxY - _minY)) * plot.Height;
                dc.DrawEllipse(pointBrush, null, new Point(x, y), 3.0, 3.0);
            }
        }

        private void DrawCrosshairAndTooltip(DrawingContext dc, Rect plot, Point hp, double dpi)
        {
            double ratioX = (hp.X - plot.Left) / plot.Width;
            double ratioY = (plot.Bottom - hp.Y) / plot.Height;
            double targetPlotTicks = _minX + ratioX * (_maxX - _minX);
            double cursorPrice = _minY + ratioY * (_maxY - _minY);
            double drawX = hp.X;
            double drawY = hp.Y;

            List<PricePoint>? snapSeries = _snapTo switch
            {
                SnapMode.Buy => _buyPoints,
                SnapMode.Sell => _sellPoints,
                _ => null
            };

            DateTimeOffset? snappedTimestamp = null;
            if (snapSeries is { Count: > 0 } && FindNearestPoint(snapSeries, targetPlotTicks) is PricePoint snappedPt)
            {
                targetPlotTicks = snappedPt.PlotTicks;
                cursorPrice = (double)snappedPt.Price;
                snappedTimestamp = snappedPt.Timestamp;
                drawX = plot.Left + ((targetPlotTicks - _minX) / (_maxX - _minX)) * plot.Width;
                drawY = plot.Bottom - ((cursorPrice - _minY) / (_maxY - _minY)) * plot.Height;
            }

            dc.PushClip(new RectangleGeometry(plot));
            dc.DrawLine(CrosshairPen, new Point(drawX, plot.Top), new Point(drawX, plot.Bottom));
            dc.DrawLine(CrosshairPen, new Point(plot.Left, drawY), new Point(plot.Right, drawY));
            dc.Pop();

            decimal? buyAtCursor = (_snapTo == SnapMode.Buy && snapSeries is { Count: > 0 } && FindNearestPoint(_buyPoints, targetPlotTicks) is PricePoint bp) ? bp.Price : InterpolatePriceAt(_buyPoints, targetPlotTicks);
            decimal? sellAtCursor = (_snapTo == SnapMode.Sell && snapSeries is { Count: > 0 } && FindNearestPoint(_sellPoints, targetPlotTicks) is PricePoint sp) ? sp.Price : InterpolatePriceAt(_sellPoints, targetPlotTicks);

            string timeStr;
            if (snappedTimestamp.HasValue)
            {
                timeStr = snappedTimestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss ddd", CultureInfo.InvariantCulture);
            }
            else
            {
                DateTime cursorLocal = WeekdayTimeAxis.ToLocalDateTime(targetPlotTicks);
                timeStr = cursorLocal.ToString("yyyy-MM-dd HH:mm:ss ddd", CultureInfo.InvariantCulture);
            }

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

        private static PricePoint? FindNearestPoint(List<PricePoint> series, double targetPlotTicks)
        {
            if (series.Count == 0) return null;
            if (targetPlotTicks <= series[0].PlotTicks) return series[0];
            if (targetPlotTicks >= series[^1].PlotTicks) return series[^1];

            int low = 0;
            int high = series.Count - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                double midTicks = series[mid].PlotTicks;
                if (Math.Abs(midTicks - targetPlotTicks) < 1.0) return series[mid];
                if (midTicks < targetPlotTicks) low = mid + 1;
                else high = mid - 1;
            }

            int idx1 = Math.Max(0, high);
            int idx2 = Math.Min(series.Count - 1, low);
            return Math.Abs(series[idx1].PlotTicks - targetPlotTicks) <= Math.Abs(series[idx2].PlotTicks - targetPlotTicks) ? series[idx1] : series[idx2];
        }

        private static decimal? InterpolatePriceAt(List<PricePoint> series, double targetPlotTicks)
        {
            if (series.Count == 0) return null;
            if (targetPlotTicks < series[0].PlotTicks || targetPlotTicks > series[^1].PlotTicks) return null;
            if (series.Count == 1) return Math.Abs(series[0].PlotTicks - targetPlotTicks) < 1.0 ? series[0].Price : null;

            int low = 0;
            int high = series.Count - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                double midTicks = series[mid].PlotTicks;
                if (Math.Abs(midTicks - targetPlotTicks) < 1.0) return series[mid].Price;
                if (midTicks < targetPlotTicks) low = mid + 1;
                else high = mid - 1;
            }

            int idx1 = Math.Max(0, high);
            int idx2 = Math.Min(series.Count - 1, low);
            if (idx1 == idx2) return series[idx1].Price;

            if ((series[idx2].UtcTicks - series[idx1].UtcTicks) > MaxGapTicks) return null;

            double t1 = series[idx1].PlotTicks;
            double t2 = series[idx2].PlotTicks;
            if (Math.Abs(t2 - t1) < 1.0) return series[idx1].Price;

            double fraction = (targetPlotTicks - t1) / (t2 - t1);
            double p1 = (double)series[idx1].Price;
            double p2 = (double)series[idx2].Price;
            return (decimal)(p1 + fraction * (p2 - p1));
        }
    }
}