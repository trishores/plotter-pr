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
    public readonly record struct PricePoint(DateTimeOffset Timestamp, double PlotTicks, double UtcTicks, decimal Price, bool IsSimulated = false);

    public enum SnapMode
    {
        None,
        Buy,
        Sell
    }

    public partial class MainWindow : Window
    {
        private const string LivePriceChannel = "liveprice";
        private static readonly string MasterLogDir = @"D:\LivePrice\MasterLogs";
        private static readonly string MasterBuyLogPath = Path.Combine(MasterLogDir, "master_buy_price_log.csv");
        private static readonly string MasterSellLogPath = Path.Combine(MasterLogDir, "master_sell_price_log.csv");
        private readonly List<PricePoint> _masterBuyPoints = new();
        private readonly List<PricePoint> _masterSellPoints = new();
        private readonly List<PricePoint> _simBuyPoints = new();
        private readonly List<PricePoint> _simSellPoints = new();
        private readonly HashSet<(long UtcTicks, decimal Price)> _knownBuyKeys = new();
        private readonly HashSet<(long UtcTicks, decimal Price)> _knownSellKeys = new();
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

            LoadMasterLogsOnStartup();
            UpdateMasterStatusDisplay();

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

                string packetTimestamp = root.TryGetProperty("Timestamp", out JsonElement tsEl) ? tsEl.GetString() ?? string.Empty : string.Empty;
                if (!DateTimeOffset.TryParse(packetTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp)) return;

                decimal lastPrice = dataEl.TryGetProperty("PriceValue", out JsonElement priceEl) && priceEl.ValueKind == JsonValueKind.Number ? priceEl.GetDecimal() : 0m;
                string transactionType = dataEl.TryGetProperty("TransactionType", out JsonElement transEl) ? transEl.GetString() ?? string.Empty : string.Empty;
                bool isSimulatedPrice = dataEl.TryGetProperty("IsSimulatedPrice", out JsonElement simEl) && simEl.ValueKind == JsonValueKind.True;

                string automationStatus = dataEl.TryGetProperty("AutomationStatus", out JsonElement automationStatusEl) ? automationStatusEl.GetString() ?? string.Empty : string.Empty;
                string statusMessage = dataEl.TryGetProperty("StatusMessage", out JsonElement statusMessageEl) ? statusMessageEl.GetString() ?? string.Empty : string.Empty;
                string errorMessage = dataEl.TryGetProperty("ErrorMessage", out JsonElement errorMessageEl) ? errorMessageEl.GetString() ?? string.Empty : string.Empty;

                bool isNewPrice = false;
                DateTimeOffset? parsedDto = timestamp.ToLocalTime();

                if (lastPrice > 0m && (transactionType.Equals("Buy", StringComparison.OrdinalIgnoreCase) || transactionType.Equals("Sell", StringComparison.OrdinalIgnoreCase)) && !string.Equals(packetTimestamp, _lastPriceTimestamp, StringComparison.Ordinal))
                {
                    _lastPriceTimestamp = packetTimestamp;
                    isNewPrice = true;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (isNewPrice && parsedDto.HasValue)
                    {
                        bool isBuy = transactionType.Equals("Buy", StringComparison.OrdinalIgnoreCase);
                        var pt = new PricePoint(parsedDto.Value, WeekdayTimeAxis.ToPlotTicks(parsedDto.Value, Chart.AxisMode), parsedDto.Value.UtcTicks, lastPrice, isSimulatedPrice);
                        if (isSimulatedPrice)
                        {
                            (isBuy ? _simBuyPoints : _simSellPoints).Add(pt);
                            Chart.AddLivePoint(pt, isBuy);
                        }
                        else if (TryAddPointToMaster(pt, isBuy))
                        {
                            Chart.AddLivePoint(pt, isBuy);
                            UpdateMasterStatusDisplay();
                        }
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
            if (Chart != null) Chart.AutoFitY = ChkAutoFitY.IsChecked == true;
        }

        private void ChkAutoFollowLive_Changed(object sender, RoutedEventArgs e)
        {
            bool follow = ChkAutoFollowLive.IsChecked == true;
            if (Chart != null) Chart.AutoFollowLive = follow;
            if (_statusWindow != null)
            {
                if (follow) _statusWindow.Show();
                else _statusWindow.Hide();
            }
        }

        private void CmbAxisMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Chart == null) return;
            Chart.AxisMode = CmbAxisMode.SelectedIndex == 1 ? TimeAxisMode.Expanded : TimeAxisMode.Compact;
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

            int newBuyAdded = 0;
            int newSellAdded = 0;
            bool buyUpdated = false;
            bool sellUpdated = false;

            foreach (string filePath in files)
            {
                string fileName = Path.GetFileName(filePath);
                if (string.Equals(fileName, "buy_price_log.csv", StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, "master_buy_price_log.csv", StringComparison.OrdinalIgnoreCase))
                {
                    List<PricePoint> droppedBuy = ParseLogFile(filePath, Chart.AxisMode);
                    newBuyAdded += MergeIntoMaster(droppedBuy, isBuy: true);
                    buyUpdated = true;
                }
                else if (string.Equals(fileName, "sell_price_log.csv", StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, "master_sell_price_log.csv", StringComparison.OrdinalIgnoreCase))
                {
                    List<PricePoint> droppedSell = ParseLogFile(filePath, Chart.AxisMode);
                    newSellAdded += MergeIntoMaster(droppedSell, isBuy: false);
                    sellUpdated = true;
                }
            }

            if (buyUpdated || sellUpdated)
            {
                Chart.SetData(buyUpdated ? BuildChartSeries(_masterBuyPoints, _simBuyPoints) : null, sellUpdated ? BuildChartSeries(_masterSellPoints, _simSellPoints) : null);

                string dropSummary = $"Dropped CSV added {newBuyAdded} new Buy and {newSellAdded} new Sell points. ";
                TxtStatus.Text = dropSummary + GetMasterStatusString();
            }
        }

        private void UpdateMasterStatusDisplay()
        {
            TxtStatus.Text = GetMasterStatusString();
        }

        private string GetMasterStatusString()
        {
            return $"Master Records -> Buy: {_masterBuyPoints.Count:N0} points | Sell: {_masterSellPoints.Count:N0} points";
        }

        private static List<PricePoint> BuildChartSeries(List<PricePoint> masterPoints, List<PricePoint> simPoints)
        {
            var combined = new List<PricePoint>(masterPoints.Count + simPoints.Count);
            combined.AddRange(masterPoints);
            if (simPoints.Count > 0)
            {
                combined.AddRange(simPoints);
                combined.Sort((a, b) => a.UtcTicks.CompareTo(b.UtcTicks));
            }
            return combined;
        }

        private void LoadMasterLogsOnStartup()
        {
            Directory.CreateDirectory(MasterLogDir);

            if (File.Exists(MasterBuyLogPath))
            {
                foreach (PricePoint pt in ParseLogFile(MasterBuyLogPath, Chart.AxisMode))
                {
                    if (_knownBuyKeys.Add(((long)pt.UtcTicks, pt.Price))) _masterBuyPoints.Add(pt);
                }
            }

            if (File.Exists(MasterSellLogPath))
            {
                foreach (PricePoint pt in ParseLogFile(MasterSellLogPath, Chart.AxisMode))
                {
                    if (_knownSellKeys.Add(((long)pt.UtcTicks, pt.Price))) _masterSellPoints.Add(pt);
                }
            }

            if (_masterBuyPoints.Count > 0 || _masterSellPoints.Count > 0)
            {
                Chart.SetData(new List<PricePoint>(_masterBuyPoints), new List<PricePoint>(_masterSellPoints));
            }
        }

        private bool TryAddPointToMaster(PricePoint pt, bool isBuy)
        {
            HashSet<(long UtcTicks, decimal Price)> knownKeys = isBuy ? _knownBuyKeys : _knownSellKeys;
            List<PricePoint> masterList = isBuy ? _masterBuyPoints : _masterSellPoints;
            string masterPath = isBuy ? MasterBuyLogPath : MasterSellLogPath;

            if (!knownKeys.Add(((long)pt.UtcTicks, pt.Price))) return false;

            if (masterList.Count == 0 || pt.UtcTicks >= masterList[^1].UtcTicks)
            {
                masterList.Add(pt);
                AppendPointToFile(masterPath, pt);
            }
            else
            {
                int idx = masterList.FindIndex(p => p.UtcTicks > pt.UtcTicks);
                if (idx >= 0) masterList.Insert(idx, pt);
                else masterList.Add(pt);
                RewriteMasterFile(masterPath, masterList);
            }
            return true;
        }

        private int MergeIntoMaster(List<PricePoint> incomingPoints, bool isBuy)
        {
            if (incomingPoints.Count == 0) return 0;

            HashSet<(long UtcTicks, decimal Price)> knownKeys = isBuy ? _knownBuyKeys : _knownSellKeys;
            List<PricePoint> masterList = isBuy ? _masterBuyPoints : _masterSellPoints;
            string masterPath = isBuy ? MasterBuyLogPath : MasterSellLogPath;

            int addedCount = 0;
            foreach (PricePoint pt in incomingPoints)
            {
                if (knownKeys.Add(((long)pt.UtcTicks, pt.Price)))
                {
                    masterList.Add(pt);
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                masterList.Sort((a, b) => a.UtcTicks.CompareTo(b.UtcTicks));
                RewriteMasterFile(masterPath, masterList);
            }
            return addedCount;
        }

        private static void AppendPointToFile(string path, PricePoint pt)
        {
            try
            {
                Directory.CreateDirectory(MasterLogDir);
                File.AppendAllText(path, $"{pt.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture)}, {pt.Price.ToString(CultureInfo.InvariantCulture)}\r\n");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Master Log Append Error: {ex.Message}");
            }
        }

        private static void RewriteMasterFile(string path, List<PricePoint> points)
        {
            try
            {
                Directory.CreateDirectory(MasterLogDir);
                string tempPath = path + ".tmp";
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs))
                {
                    for (int i = 0; i < points.Count; i++)
                    {
                        PricePoint pt = points[i];
                        sw.Write(pt.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture));
                        sw.Write(", ");
                        sw.WriteLine(pt.Price.ToString(CultureInfo.InvariantCulture));
                    }
                }
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Master Log Rewrite Error: {ex.Message}");
            }
        }

        private static List<PricePoint> ParseLogFile(string path, TimeAxisMode mode)
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
                    list.Add(new PricePoint(dto, WeekdayTimeAxis.ToPlotTicks(dto, mode), dto.UtcTicks, price));
                }
            }
            list.Sort((a, b) => a.UtcTicks.CompareTo(b.UtcTicks));
            return list;
        }
    }
}