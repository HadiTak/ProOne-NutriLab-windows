using System.Drawing;
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
    readonly Label lblInfo = new();
    readonly Label lblRaw = new();
    readonly System.Windows.Forms.Timer uiTimer = new();

    // ---- Shared state (written by BLE thread, read by UI timer) ----
    readonly object _lock = new();
    BluetoothLEAdvertisementWatcher? _watcher;
    int _weight = -1;
    bool _suspicious;
    short _rssi;
    long _packets;
    DateTime _lastRx = DateTime.MinValue;
    string _raw = "";
    string _error = "";

    public MainForm()
    {
        Text = "IN_C1 Scale";
        ClientSize = new Size(560, 380);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 24, 28);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        lblWeight.Dock = DockStyle.Top;
        lblWeight.Height = 140;
        lblWeight.TextAlign = ContentAlignment.MiddleCenter;
        lblWeight.Font = new Font("Segoe UI", 64f, FontStyle.Bold);
        lblWeight.Text = "--- g";

        lblInfo.Dock = DockStyle.Top;
        lblInfo.Height = 150;
        lblInfo.Padding = new Padding(24, 8, 24, 0);
        lblInfo.Font = new Font("Consolas", 11f);

        lblRaw.Dock = DockStyle.Fill;
        lblRaw.Padding = new Padding(24, 0, 24, 0);
        lblRaw.Font = new Font("Consolas", 9f);
        lblRaw.ForeColor = Color.Gray;

        Controls.Add(lblRaw);
        Controls.Add(lblInfo);
        Controls.Add(lblWeight);

        uiTimer.Interval = 150;
        uiTimer.Tick += (_, _) => RefreshUi();
        uiTimer.Start();

        Load += (_, _) => StartWatcher();
        FormClosing += (_, _) => { try { _watcher?.Stop(); } catch { } };
    }

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
        // MAC filter
        if (args.BluetoothAddress != TargetMac) return;

        // Name filter (only if the name is present in this packet)
        var name = args.Advertisement.LocalName;
        if (!string.IsNullOrEmpty(name) && name != DeviceName) return;

        foreach (var md in args.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != CompanyId) continue;

            // Windows gives the company ID separately; md.Data has only the bytes AFTER it.
            // Rebuild the full buffer so offsets match the raw Android dump: 80 04 01 29 ...
            var payload = new byte[md.Data.Length];
            DataReader.FromBuffer(md.Data).ReadBytes(payload);

            var full = new byte[payload.Length + 2];
            full[0] = (byte)(CompanyId & 0xFF);   // 0x80
            full[1] = (byte)(CompanyId >> 8);     // 0x04
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
                    _suspicious = true;   // keep last good weight
                }
            }
        }
    }

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

        lblWeight.Text = weight >= 0 ? $"{weight} g" : "--- g";
        lblWeight.ForeColor = receiving ? Color.White : Color.DimGray;

        string status = error.Length > 0 ? error
                      : receiving ? (suspicious ? "Receiving (suspicious packet)" : "Receiving")
                      : "No Signal";

        var sb = new StringBuilder();
        sb.AppendLine($"Status:       {status}");
        sb.AppendLine($"Device:       {DeviceName}");
        sb.AppendLine("MAC:          64-FB-01-10-94-32");
        sb.AppendLine($"Manufacturer: 0x{CompanyId:X4}");
        sb.AppendLine($"RSSI:         {(packets > 0 ? rssi + " dBm" : "-")}");
        sb.AppendLine($"Packets:      {packets}");
        sb.Append($"Last Update:  {(last == DateTime.MinValue ? "-" : last.ToString("HH:mm:ss"))}");
        lblInfo.Text = sb.ToString();

        lblRaw.Text = raw.Length > 0 ? "Raw: " + raw : "";
    }
}
