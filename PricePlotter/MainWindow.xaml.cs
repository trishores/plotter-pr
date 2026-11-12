using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
        private const string LivePriceChannel = "liveprice";
        private readonly KestrelClient _kestrelClient = new();
        private readonly CancellationTokenSource _cts = new();
        private LiveStatusWindow? _statusWindow;
        private string _lastPriceTimestamp = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _statusWindow = new LiveStatusWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual, Left = Left + Width - 400, Top = Top + 60 };
            if (ChkAutoFollowLive.IsChecked == true)
            {
                _statusWindow.Show();
            }

            _kestrelClient.OnConnectionStateChanged += (channel, status) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _statusWindow?.UpdateConnectionState(status);
                });
            };

            _ = StartLiveMonitoringAsync(_cts.Token);
        }

        private async Task StartLiveMonitoringAsync(CancellationToken cancellationToken)
        {
            _kestrelClient.Start(LivePriceChannel);
            try
            {
                await foreach (RelayMessage msg in _kestrelClient.ReadAllAsync(cancellationToken))
                {
                    ProcessIncomingPacket(msg.Payload);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void ProcessIncomingPacket(ReadOnlyMemory<byte> utf8Json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(utf8Json);
                JsonElement root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    root = root[0];
                }

                if (!root.TryGetProperty("Type", out JsonElement typeEl) || typeEl.ValueKind != JsonValueKind.String || !root.TryGetProperty("Data", out JsonElement dataEl))
                {
                    return;
                }

                string messageType = typeEl.GetString() ?? string.Empty;
                if (!messageType.Equals("LivePricePacket", StringComparison.OrdinalIgnoreCase)) return;

                string priceTimestamp = dataEl.TryGetProperty("PriceTimestamp", out JsonElement tsEl) ? tsEl.GetString() ?? string.Empty : string.Empty;
                decimal lastPrice = dataEl.TryGetProperty("PriceValue", out JsonElement priceEl) && priceEl.ValueKind == JsonValueKind.Number ? priceEl.GetDecimal() : 0m;
                string transactionType = dataEl.TryGetProperty("TransactionType", out JsonElement transEl) ? transEl.GetString() ?? string.Empty : string.Empty;

                string automationStatus = dataEl.TryGetProperty("AutomationStatus", out JsonElement automationStatusEl) ? automationStatusEl.GetString() ?? string.Empty : string.Empty;
                string statusMessage = dataEl.TryGetProperty("StatusMessage", out JsonElement statusMessageEl) ? statusMessageEl.GetString() ?? string.Empty : string.Empty;
                string errorMessage = dataEl.TryGetProperty("ErrorMessage", out JsonElement errorMessageEl) ? errorMessageEl.GetString() ?? string.Empty : string.Empty;

                bool isNewPrice = false;
                DateTimeOffset? parsedDto = null;

                if (!string.IsNullOrEmpty(priceTimestamp) && !string.Equals(priceTimestamp, _lastPriceTimestamp, StringComparison.Ordinal) && long.TryParse(priceTimestamp, CultureInfo.InvariantCulture, out long rawUtcTicks))
                {
                    _lastPriceTimestamp = priceTimestamp;
                    parsedDto = new DateTimeOffset(rawUtcTicks, TimeSpan.Zero).ToLocalTime();
                    isNewPrice = lastPrice > 0m && (transactionType.Equals("Buy", StringComparison.OrdinalIgnoreCase) || transactionType.Equals("Sell", StringComparison.OrdinalIgnoreCase));
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (isNewPrice && parsedDto.HasValue && !WeekdayTimeAxis.IsWeekend(parsedDto.Value))
                    {
                        bool isBuy = transactionType.Equals("Buy", StringComparison.OrdinalIgnoreCase);
                        var pt = new PricePoint(parsedDto.Value, WeekdayTimeAxis.ToPlotTicks(parsedDto.Value), parsedDto.Value.UtcTicks, lastPrice);
                        Chart.AddLivePoint(pt, isBuy);
                    }
                    _statusWindow?.UpdatePacketData(automationStatus, statusMessage, errorMessage, isNewPrice ? transactionType : null, isNewPrice ? lastPrice : null, parsedDto);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Payload Parse Error: {ex.Message}");
            }
        }

        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            _cts.Cancel();
            _statusWindow?.ClosePermanently();
            await _kestrelClient.DisposeAsync();
            _cts.Dispose();
        }

        private void BtnResetView_Click(object sender, RoutedEventArgs e)
        {
            Chart?.ResetView();
        }

        private void ChkAutoFitY_Changed(object sender, RoutedEventArgs e)
        {
            Chart?.AutoFitY = ChkAutoFitY.IsChecked == true;
        }

        private void ChkAutoFollowLive_Changed(object sender, RoutedEventArgs e)
        {
            bool follow = ChkAutoFollowLive.IsChecked == true;
            Chart?.AutoFollowLive = follow;
            if (_statusWindow != null)
            {
                if (follow) _statusWindow.Show();
                else _statusWindow.Hide();
            }
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