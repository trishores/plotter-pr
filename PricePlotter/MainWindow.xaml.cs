using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace mine
{
    public readonly record struct PricePoint(DateTimeOffset Timestamp, double PlotTicks, double UtcTicks, decimal Price);

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
                    if (WeekdayTimeAxis.IsWeekend(dto)) continue;
                    list.Add(new PricePoint(dto, WeekdayTimeAxis.ToPlotTicks(dto), dto.UtcTicks, price));
                }
            }
            list.Sort((a, b) => a.PlotTicks.CompareTo(b.PlotTicks));
            return list;
        }
    }
}