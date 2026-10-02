using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace InC1Scale;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

sealed class MainForm : Form
{
    // ---- Device filter ----
    const string DeviceName = "IN_C1";
    const ushort CompanyId = 0x0480;
    const ulong TargetMac = 0x64FB01109432;      // 64-FB-01-10-94-32
    const int NoSignalSeconds = 3;

    // ---- UI ----
    readonly Label lblWeight = new();
    readonly Label lblHint = new();
    readonly Label lblAddCaption = new();
    readonly TextBox txtAdd = new();
    readonly Label lblTotal = new();
    readonly Label lblInfo = new();
    readonly Label lblRaw = new();
    readonly System.Windows.Forms.Timer uiTimer = new();

    // ---- Shared state (written by BLE thread, read by UI) ----
    readonly object _lock = new();
    BluetoothLEAdvertisementWatcher? _watcher;
    int _weight = -1;
    bool _suspicious;
    short _rssi;
    long _packets;
    DateTime _lastRx = DateTime.MinValue;
    string _raw = "";
    string _error = "";

    // ---- Copy feedback ----
    string _toast = "";
    DateTime _toastUntil = DateTime.MinValue;

    // Result of the last total calculation (used for click-to-copy)
    string _totalText = "";

    public MainForm()
    {
        Text = "IN_C1 Scale";
        ClientSize = new Size(560, 660);
        MinimumSize = new Size(420, 320);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 24, 28);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        // Big weight (click to copy)
        lblWeight.Dock = DockStyle.Fill;
        lblWeight.AutoSize = true;
        lblWeight.TextAlign = ContentAlignment.MiddleCenter;
        lblWeight.Font = new Font("Segoe UI", 60f, FontStyle.Bold);
        lblWeight.Text = "--- g";
        lblWeight.Cursor = Cursors.Hand;
        lblWeight.Click += (_, _) => CopyWeight();

        // Hint / "Copied" message
        lblHint.Dock = DockStyle.Fill;
        lblHint.AutoSize = true;
        lblHint.TextAlign = ContentAlignment.MiddleCenter;
        lblHint.ForeColor = Color.Gray;
        lblHint.Font = new Font("Segoe UI", 9f);
        lblHint.Padding = new Padding(0, 0, 0, 8);

        // Add (g) row
        lblAddCaption.Text = "Add (g):";
        lblAddCaption.AutoSize = true;
        lblAddCaption.Font = new Font("Segoe UI", 14f);
        lblAddCaption.Margin = new Padding(0, 8, 8, 0);

        txtAdd.Width = 140;
        txtAdd.Font = new Font("Segoe UI", 16f);
        txtAdd.BackColor = Color.FromArgb(45, 45, 52);
        txtAdd.ForeColor = Color.White;
        txtAdd.BorderStyle = BorderStyle.FixedSingle;
        txtAdd.TextChanged += (_, _) => RefreshUi();
        txtAdd.Enter += (_, _) => txtAdd.SelectAll();

        var addRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 4, 0, 4)
        };
        addRow.Controls.Add(lblAddCaption);
        addRow.Controls.Add(txtAdd);

        // Total (click to copy)
        lblTotal.Dock = DockStyle.Fill;
        lblTotal.AutoSize = true;
        lblTotal.TextAlign = ContentAlignment.MiddleCenter;
        lblTotal.Font = new Font("Segoe UI", 26f, FontStyle.Bold);
        lblTotal.ForeColor = Color.FromArgb(120, 200, 255);
        lblTotal.Cursor = Cursors.Hand;
        lblTotal.Padding = new Padding(0, 8, 0, 8);
        lblTotal.Click += (_, _) => CopyTotal();

        // Info block
        lblInfo.Dock = DockStyle.Fill;
        lblInfo.AutoSize = true;
        lblInfo.Font = new Font("Consolas", 11f);
        lblInfo.Padding = new Padding(8, 8, 8, 0);

        // Raw data
        lblRaw.Dock = DockStyle.Fill;
        lblRaw.AutoSize = true;
        lblRaw.Font = new Font("Consolas", 9f);
        lblRaw.ForeColor = Color.Gray;
        lblRaw.Padding = new Padding(8, 4, 8, 8);

        // Layout: one column, every row sizes itself automatically
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(12)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.Controls.Add(lblWeight);
        table.Controls.Add(lblHint);
        table.Controls.Add(addRow);
        table.Controls.Add(lblTotal);
        table.Controls.Add(lblInfo);
        table.Controls.Add(lblRaw);

        // Scroll container: if the window is small, a scrollbar appears
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(table);
        Controls.Add(scroll);

        uiTimer.Interval = 150;
        uiTimer.Tick += (_, _) => RefreshUi();
        uiTimer.Start();

        Load += (_, _) => StartWatcher();
        FormClosing += (_, _) => { try { _watcher?.Stop(); } catch { } };
    }

    // ------------------------------------------------------------ BLE

    void StartWatcher()
    {
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            _watcher.Received += OnReceived;
            _watcher.Stopped += (_, e) =>
            {
                lock (_lock) _error = "Scanner stopped: " + e.Error;
            };
            _watcher.Start();
        }
        catch (Exception ex)
        {
            lock (_lock) _error = "Bluetooth error: " + ex.Message;
        }
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {

        var name = args.Advertisement.LocalName;
        if (!string.IsNullOrEmpty(name) && name != DeviceName) return;

        foreach (var md in args.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != CompanyId) continue;

            var payload = new byte[md.Data.Length];
            DataReader.FromBuffer(md.Data).ReadBytes(payload);

            var full = new byte[payload.Length + 2];
            full[0] = (byte)(CompanyId & 0xFF);
            full[1] = (byte)(CompanyId >> 8);
            Array.Copy(payload, 0, full, 2, payload.Length);

            if (full.Length < 16) continue;

            int w1 = (full[12] << 8) | full[13];
            int w2 = (full[14] << 8) | full[15];

            lock (_lock)
            {
                _packets++;
                _rssi = args.RawSignalStrengthInDBm;
                _lastRx = DateTime.Now;
                _raw = BitConverter.ToString(full).Replace('-', ' ');
                if (w1 == w2)
                {
                    _weight = w1;
                    _suspicious = false;
                }
                else
                {
                    _suspicious = true;
                }
            }
        }
    }

    // ------------------------------------------------------------ Copy

    void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            _toast = "Copied: " + text;
        }
        catch
        {
            _toast = "Copy failed, try again";
        }
        _toastUntil = DateTime.Now.AddSeconds(1.5);
        RefreshUi();
    }

    void CopyWeight()
    {
        int w;
        lock (_lock) w = _weight;
        if (w >= 0) CopyText(w.ToString(CultureInfo.InvariantCulture));
    }

    void CopyTotal()
    {
        CopyText(_totalText);
    }

    // ------------------------------------------------------------ Helpers

    // Accepts Persian/Arabic digits and "," or "٫" as decimal separator
    static string NormalizeNumber(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Trim())
        {
            if (ch >= '۰' && ch <= '۹') sb.Append((char)('0' + (ch - '۰')));
            else if (ch >= '٠' && ch <= '٩') sb.Append((char)('0' + (ch - '٠')));
            else if (ch == '٫' || ch == ',') sb.Append('.');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ UI refresh

    void RefreshUi()
    {
        int weight; bool suspicious; short rssi; long packets; DateTime last; string raw, error;
        lock (_lock)
        {
            weight = _weight; suspicious = _suspicious; rssi = _rssi;
            packets = _packets; last = _lastRx; raw = _raw; error = _error;
        }

        bool receiving = last != DateTime.MinValue &&
                         (DateTime.Now - last).TotalSeconds <= NoSignalSeconds;

        // Weight
        lblWeight.Text = weight >= 0 ? $"{weight} g" : "--- g";
        lblWeight.ForeColor = receiving ? Color.White : Color.DimGray;

        // Hint / copied message
        lblHint.Text = DateTime.Now < _toastUntil
            ? _toast
            : "Click the weight or the total to copy";

        // Total = weight + added value
        string addText = NormalizeNumber(txtAdd.Text);
        if (weight < 0)
        {
            lblTotal.Text = "Total: ---";
            _totalText = "";
        }
        else if (addText.Length == 0)
        {
            _totalText = weight.ToString(CultureInfo.InvariantCulture);
            lblTotal.Text = $"Total: {_totalText} g";
        }
        else if (decimal.TryParse(addText, NumberStyles.Float, CultureInfo.InvariantCulture, out var add))
        {
            decimal total = weight + add;
            _totalText = total.ToString("0.##", CultureInfo.InvariantCulture);
            lblTotal.Text = $"Total: {_totalText} g";
        }
        else
        {
            _totalText = "";
            lblTotal.Text = "Invalid number";
        }

        // Status
        string status = error.Length > 0 ? error
                      : receiving ? (suspicious ? "Receiving (suspicious packet)" : "Receiving")
                      : "No Signal";

        var sb = new StringBuilder();
        sb.AppendLine($"Status:       {status}");
        sb.AppendLine($"Device:       {DeviceName}");
        sb.AppendLine($"Manufacturer: 0x{CompanyId:X4}");
        sb.AppendLine($"RSSI:         {(packets > 0 ? rssi + " dBm" : "-")}");
        sb.AppendLine($"Packets:      {packets}");
        sb.Append($"Last Update:  {(last == DateTime.MinValue ? "-" : last.ToString("HH:mm:ss"))}");
        lblInfo.Text = sb.ToString();

        lblRaw.Text = raw.Length > 0 ? "Raw: " + raw : "";
    }
}
