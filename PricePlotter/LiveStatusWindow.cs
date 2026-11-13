using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace mine
{
    public sealed class LiveStatusWindow : Window
    {
        private readonly TextBlock _txtConnection = new() { Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82)), FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        private readonly TextBlock _txtLatestPrice = new() { Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        private readonly TextBlock _txtAutomationStatus = new() { Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 118)), FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        private readonly TextBlock _txtStatusMessage = new() { Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        private readonly TextBlock _txtErrorMessage = new() { Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82)), FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private bool _allowClose;

        public LiveStatusWindow()
        {
            Title = "Live Stream Status (liveprice)";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.ToolWindow;
            Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(37, 37, 38));
            Foreground = Brushes.White;
            ShowInTaskbar = false;

            _txtConnection.Text = "Connection:       Disconnected";
            _txtLatestPrice.Text = "Last Packet:      Waiting...";
            _txtAutomationStatus.Text = "AutomationStatus: N/A";
            _txtStatusMessage.Text = "StatusMessage:    N/A";
            _txtErrorMessage.Text = "ErrorMessage:     None";

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(_txtConnection);
            panel.Children.Add(_txtLatestPrice);
            panel.Children.Add(_txtAutomationStatus);
            panel.Children.Add(_txtStatusMessage);
            panel.Children.Add(_txtErrorMessage);
            Content = panel;
        }

        public void UpdateConnectionState(ConnectionStatus status)
        {
            _txtConnection.Text = $"Connection:       {status}";
            _txtConnection.Foreground = status == ConnectionStatus.Connected ? new SolidColorBrush(Color.FromRgb(0, 230, 118)) : new SolidColorBrush(Color.FromRgb(255, 82, 82));
        }

        public void UpdatePacketData(string automationStatus, string statusMessage, string errorMessage, string? transactionType, decimal? price, DateTimeOffset? timestamp)
        {
            if (timestamp.HasValue && price.HasValue && !string.IsNullOrEmpty(transactionType))
            {
                _txtLatestPrice.Text = $"Last Packet:      {transactionType} £{price.Value.ToString("F3", CultureInfo.InvariantCulture)} @ {timestamp.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
            }
            _txtAutomationStatus.Text = $"AutomationStatus: {(string.IsNullOrEmpty(automationStatus) ? "N/A" : automationStatus)}";
            _txtStatusMessage.Text = $"StatusMessage:    {(string.IsNullOrEmpty(statusMessage) ? "N/A" : statusMessage)}";
            _txtErrorMessage.Text = $"ErrorMessage:     {(string.IsNullOrEmpty(errorMessage) ? "None" : errorMessage)}";
        }

        public void ClosePermanently()
        {
            _allowClose = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnClosing(e);
        }
    }
}