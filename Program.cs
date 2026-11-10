using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace InC1Scale
{
    static class Program
    {
        const int MinFrameworkRelease = 461808; // .NET Framework 4.7.2
        const string DotNetUrl = "https://dotnet.microsoft.com/download/dotnet-framework/net48";

        [STAThread]
        static void Main()
        {
            if (!CheckEnvironment()) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            RunApp();
        }

        static void RunApp()
        {
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The program could not start.\n\n" + ex.GetType().Name + ": " + ex.Message +
                    "\n\nProgram requirements: Windows 10 or 11 and .NET Framework 4.7.2 or newer." +
                    "\nبرنامه اجرا نشد. به ویندوز ۱۰ یا ۱۱ و .NET Framework نسخه 4.7.2 یا بالاتر نیاز دارد.",
                    "IN_C1 Scale", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Returns false (after showing a clear message) if the PC can't run the app
        static bool CheckEnvironment()
        {
            // 1) Windows 10 or newer
            bool isWin10 = true;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null) isWin10 = k.GetValue("CurrentMajorVersionNumber") != null;
                }
            }
            catch { }

            if (!isWin10)
            {
                MessageBox.Show(
                    "This program needs Windows 10 or Windows 11 (Bluetooth LE support).\n" +
                    "این برنامه به ویندوز ۱۰ یا ۱۱ نیاز دارد.",
                    "IN_C1 Scale", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // 2) .NET Framework 4.7.2 or newer
            int release = 0;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("Release");
                        if (v != null) release = Convert.ToInt32(v);
                    }
                }
            }
            catch { }

            if (release != 0 && release < MinFrameworkRelease)
            {
                var r = MessageBox.Show(
                    "This program needs .NET Framework 4.7.2 or newer (recommended: 4.8).\n" +
                    "نیاز به .NET Framework نسخه 4.7.2 یا بالاتر دارد (پیشنهادی: 4.8).\n\n" +
                    "Open the download page now?\n" +
                    "صفحه‌ی دانلود باز شود؟",
                    "IN_C1 Scale", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes)
                {
                    try { Process.Start(DotNetUrl); } catch { }
                }
                return false;
            }

            return true;
        }
    }

    sealed class MainForm : Form
    {
        // ---- Device filter ----
        const string DeviceName = "IN_C1";
        const ushort CompanyId = 0x0480;
        const int NoSignalSeconds = 3;
        const int RestartAfterSeconds = 8;
        const string BtcAddress = "bc1q7nzwldzyccuelyxsw00fnhj3g09jkj3mx7ugfp";

        // ---- UI ----
        readonly Label lblWeight = new Label();
        readonly Label lblHint = new Label();
        readonly Label lblAddCaption = new Label();
        readonly TextBox txtAdd = new TextBox();
        readonly Label lblTotal = new Label();
        readonly Label lblInfo = new Label();
        readonly Label lblRaw = new Label();
        readonly Label lblDonate = new Label();
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();

        // ---- Shared state (written by BLE thread, read by UI) ----
        readonly object _lock = new object();
        BluetoothLEAdvertisementWatcher _watcher;
        int _weight = -1;
        bool _suspicious;
        short _rssi;
        long _packets;
        DateTime _lastRx = DateTime.MinValue;
        string _raw = "";
        string _error = "Starting Bluetooth...";

        // ---- Watchdog ----
        DateTime _lastRestart = DateTime.Now;
        volatile bool _restartRequested;

        // ---- Copy feedback ----
        string _toast = "";
        DateTime _toastUntil = DateTime.MinValue;
        string _totalText = "";

        public MainForm()
        {
            Text = "IN_C1 Scale";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            ClientSize = new Size(560, 700);
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
            lblWeight.Click += (s, e) => CopyWeight();

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
            txtAdd.TextChanged += (s, e) => RefreshUi();
            txtAdd.Enter += (s, e) => txtAdd.SelectAll();

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
            lblTotal.Click += (s, e) => CopyText(_totalText);

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

            // Donation address (click to copy)
            lblDonate.Dock = DockStyle.Fill;
            lblDonate.AutoSize = true;
            lblDonate.Font = new Font("Consolas", 9f);
            lblDonate.ForeColor = Color.FromArgb(247, 147, 26);
            lblDonate.Cursor = Cursors.Hand;
            lblDonate.Padding = new Padding(8, 12, 8, 8);
            lblDonate.Text = "My Bitcoin address to receive (click to copy):\n" + BtcAddress;
            lblDonate.Click += (s, e) => CopyText(BtcAddress);

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
            table.Controls.Add(lblDonate);

            // Scroll container: if the window is small, a scrollbar appears
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            scroll.Controls.Add(table);
            Controls.Add(scroll);

            uiTimer.Interval = 150;
            uiTimer.Tick += (s, e) => { Watchdog(); RefreshUi(); };
            uiTimer.Start();

            // Show the window first, then start Bluetooth in the background
            Shown += (s, e) =>
            {
                _lastRestart = DateTime.Now;
                Task.Run(() => SafeStart());
            };
            FormClosing += (s, e) => { try { StopWatcher(); } catch { } };
        }

        // ------------------------------------------------------------ BLE

        void SetError(string message)
        {
            lock (_lock) _error = message;
        }

        // Wrapper so that ANY failure (even a missing Windows component) becomes a readable message
        void SafeStart()
        {
            try
            {
                StartWatcher();
            }
            catch (Exception ex)
            {
                SetError("Bluetooth not available: " + ex.GetType().Name + " - " + ex.Message);
            }
        }

        void StopWatcher()
        {
            var w = _watcher;
            if (w == null) return;
            w.Received -= OnReceived;
            w.Stopped -= OnStopped;
            try { w.Stop(); } catch { }
            _watcher = null;
        }

        void StartWatcher()
        {
            StopWatcher();

            var w = new BluetoothLEAdvertisementWatcher();
            w.ScanningMode = BluetoothLEScanningMode.Active;
            w.Received += OnReceived;
            w.Stopped += OnStopped;
            _watcher = w;
            w.Start();

            _lastRestart = DateTime.Now;
            SetError("");
        }

        void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs e)
        {
            string msg;
            switch (e.Error)
            {
                case BluetoothError.Success:
                    return;
                case BluetoothError.RadioNotAvailable:
                    msg = "Bluetooth is off or missing - turn on Bluetooth in Windows";
                    break;
                case BluetoothError.DisabledByUser:
                    msg = "Bluetooth is disabled in Windows settings";
                    break;
                case BluetoothError.DisabledByPolicy:
                    msg = "Bluetooth is disabled by system policy";
                    break;
                case BluetoothError.NotSupported:
                    msg = "This PC does not support Bluetooth LE";
                    break;
                default:
                    msg = "Bluetooth scanner stopped: " + e.Error;
                    break;
            }
            SetError(msg);
            _restartRequested = true;
        }

        // If no packet arrives for a while (or the scanner stopped), restart the scanner
        void Watchdog()
        {
            DateTime last;
            lock (_lock) last = _lastRx;

            var now = DateTime.Now;
            var since = last > _lastRestart ? last : _lastRestart;
            bool silent = (now - since).TotalSeconds > RestartAfterSeconds;

            if ((_restartRequested || silent) && (now - _lastRestart).TotalSeconds >= 3)
            {
                _restartRequested = false;
                _lastRestart = now;
                Task.Run(() => SafeStart());
            }
        }

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var name = args.Advertisement.LocalName;
            if (!string.IsNullOrEmpty(name) && name != DeviceName) return;

            foreach (var md in args.Advertisement.ManufacturerData)
            {
                if (md.CompanyId != CompanyId) continue;

                // Windows gives the company ID separately; rebuild the full buffer
                // so offsets match the raw dump: 80 04 01 29 ...
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
                    _error = "";
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
            lblWeight.Text = weight >= 0 ? weight + " g" : "--- g";
            lblWeight.ForeColor = receiving ? Color.White : Color.DimGray;

            // Hint / copied message
            lblHint.Text = DateTime.Now < _toastUntil
                ? _toast
                : "Click the weight or the total to copy";

            // Total = weight + added value
            string addText = NormalizeNumber(txtAdd.Text);
            decimal add;
            if (weight < 0)
            {
                lblTotal.Text = "Total: ---";
                _totalText = "";
            }
            else if (addText.Length == 0)
            {
                _totalText = weight.ToString(CultureInfo.InvariantCulture);
                lblTotal.Text = "Total: " + _totalText + " g";
            }
            else if (decimal.TryParse(addText, NumberStyles.Float, CultureInfo.InvariantCulture, out add))
            {
                decimal total = weight + add;
                _totalText = total.ToString("0.##", CultureInfo.InvariantCulture);
                lblTotal.Text = "Total: " + _totalText + " g";
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
            sb.AppendLine("Status:       " + status);
            sb.AppendLine("Device:       " + DeviceName);
            sb.AppendLine("Manufacturer: 0x" + CompanyId.ToString("X4"));
            sb.AppendLine("RSSI:         " + (packets > 0 ? rssi + " dBm" : "-"));
            sb.AppendLine("Packets:      " + packets);
            sb.Append("Last Update:  " + (last == DateTime.MinValue ? "-" : last.ToString("HH:mm:ss")));
            lblInfo.Text = sb.ToString();

            lblRaw.Text = raw.Length > 0 ? "Raw: " + raw : "";
        }
    }
}
