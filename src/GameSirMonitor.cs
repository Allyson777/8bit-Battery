using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Drawing;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Forms;
using System.Globalization;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GameSirBatteryMonitor
{
    public class ControllerStateData
    {
        public int CardIndex;
        public int XInputSlot;
        public bool Connected;
        public byte BatteryType;
        public byte BatteryLevel;
        public double EstimatedPercent = 100.0;
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
        public double SessionSeconds;
        public string ModelName = "GameSir Nova Lite";
        public string ConnectionSubtitle = "Receptor 2.4GHz · Slot XInput 1";
        public DateTime LastTick = DateTime.UtcNow;
    }

    public static class DynamicXInput
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_BATTERY_INFORMATION
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_VIBRATION
        {
            public ushort wLeftMotorSpeed;
            public ushort wRightMotorSpeed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string libname);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint XInputGetStateDelegate(uint dwUserIndex, ref XINPUT_STATE pState);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint XInputSetStateDelegate(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint XInputGetBatteryInformationDelegate(uint dwUserIndex, byte devType, ref XINPUT_BATTERY_INFORMATION pBatteryInformation);

        private static XInputGetStateDelegate _getState;
        private static XInputSetStateDelegate _setState;
        private static XInputGetBatteryInformationDelegate _getBattery;

        static DynamicXInput()
        {
            string[] dlls = { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" };
            foreach (string dll in dlls)
            {
                IntPtr hMod = LoadLibrary(dll);
                if (hMod != IntPtr.Zero)
                {
                    IntPtr pGetState = GetProcAddress(hMod, "XInputGetState");
                    IntPtr pSetState = GetProcAddress(hMod, "XInputSetState");
                    IntPtr pGetBat = GetProcAddress(hMod, "XInputGetBatteryInformation");

                    if (pGetState != IntPtr.Zero)
                        _getState = (XInputGetStateDelegate)Marshal.GetDelegateForFunctionPointer(pGetState, typeof(XInputGetStateDelegate));
                    if (pSetState != IntPtr.Zero)
                        _setState = (XInputSetStateDelegate)Marshal.GetDelegateForFunctionPointer(pSetState, typeof(XInputSetStateDelegate));
                    if (pGetBat != IntPtr.Zero)
                        _getBattery = (XInputGetBatteryInformationDelegate)Marshal.GetDelegateForFunctionPointer(pGetBat, typeof(XInputGetBatteryInformationDelegate));

                    if (_getState != null) break;
                }
            }
        }

        public static uint GetState(uint index, ref XINPUT_STATE state)
        {
            if (_getState == null) return 1167;
            return _getState(index, ref state);
        }

        public static uint SetState(uint index, ref XINPUT_VIBRATION vib)
        {
            if (_setState == null) return 1167;
            return _setState(index, ref vib);
        }

        public static uint GetBatteryInformation(uint index, byte devType, ref XINPUT_BATTERY_INFORMATION bat)
        {
            if (_getBattery == null)
            {
                bat.BatteryType = 1;
                bat.BatteryLevel = 3;
                return 0;
            }
            return _getBattery(index, devType, ref bat);
        }
    }

    public class Program : ApplicationContext
    {
        public const int PORT = 47832;

        // ---------------- SetupAPI & HID for Universal Device Discovery ----------------
        const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public int cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
            public string DevicePath;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, uint f);

        [DllImport("setupapi.dll")]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA o);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, ref SP_DEVICE_INTERFACE_DETAIL_DATA o, int sz, out int req, IntPtr di);

        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

        static readonly ControllerStateData[] _ctl = new ControllerStateData[2];
        static int _dongles = 0;
        static string _detectedModel = "GameSir Controller";
        static string _detectedTransport = "Receptor 2.4GHz";
        static readonly object _lock = new object();
        static string _baseDir, _stateFile;
        static TcpListener _listener;
        static volatile bool _running = true;

        NotifyIcon _tray;
        System.Windows.Forms.Timer _trayTimer;

        [STAThread]
        public static void Main(string[] args)
        {
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSirBatteryHub");
            try { Directory.CreateDirectory(appDataFolder); } catch { }
            _stateFile = Path.Combine(appDataFolder, "battery_state.txt");

            bool background = Array.IndexOf(args, "--background") >= 0;

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, PORT);
                _listener.Start();
            }
            catch
            {
                if (!background) OpenAppWindow();
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                _ctl[i] = new ControllerStateData { CardIndex = i, XInputSlot = i };
            }
            LoadState();

            Application.EnableVisualStyles();
            Application.Run(new Program(!background));
        }

        public Program(bool openWindow)
        {
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("Abrir GameSir Battery Hub", (s, e) => OpenAppWindow());
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Vibrar Controle P1", (s, e) => Vibrate(0));
            menu.MenuItems.Add("Vibrar Controle P2", (s, e) => Vibrate(1));
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Marcar Ambos 100% Carregados", (s, e) => { SetBattery(0, 100); SetBattery(1, 100); });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Sair", (s, e) => { _running = false; _tray.Visible = false; SaveState(); Application.Exit(); });

            _tray = new NotifyIcon
            {
                Text = "GameSir Battery Hub",
                Icon = TrayIcon(100, 100, false, false),
                ContextMenu = menu,
                Visible = true
            };
            _tray.DoubleClick += (s, e) => OpenAppWindow();

            new Thread(PollLoop) { IsBackground = true }.Start();
            new Thread(ServerLoop) { IsBackground = true }.Start();

            _trayTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _trayTimer.Tick += (s, e) => UpdateTray();
            _trayTimer.Start();

            if (openWindow) OpenAppWindow();
        }

        public static void OpenAppWindow()
        {
            string url = "http://127.0.0.1:" + PORT + "/";
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSirBatteryHub", "webview");
            string[] browsers = {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe"
            };
            try
            {
                foreach (string b in browsers)
                {
                    if (!File.Exists(b)) continue;
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = b,
                        Arguments = "--app=" + url + " --user-data-dir=\"" + profile + "\" --window-size=1320,900 --no-first-run --no-default-browser-check",
                        UseShellExecute = false
                    });
                    return;
                }
                Process.Start(url);
            }
            catch { }
        }

        void UpdateTray()
        {
            int p0, p1; bool c0, c1;
            lock (_lock)
            {
                p0 = (int)Math.Round(_ctl[0].EstimatedPercent); p1 = (int)Math.Round(_ctl[1].EstimatedPercent);
                c0 = _ctl[0].Connected; c1 = _ctl[1].Connected;
            }
            _tray.Text = "P1: " + (c0 ? p0 + "%" : "off") + "  |  P2: " + (c1 ? p1 + "%" : "off");
            Icon old = _tray.Icon;
            _tray.Icon = TrayIcon(p0, p1, c0, c1);
            if (old != null) old.Dispose();
        }

        static Icon TrayIcon(int p0, int p1, bool c0, bool c1)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Bar(g, 1, p0, c0, Color.FromArgb(34, 211, 238));
                Bar(g, 9, p1, c1, Color.FromArgb(244, 114, 182));
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        static void Bar(Graphics g, int x, int pct, bool on, Color col)
        {
            using (Pen p = new Pen(Color.FromArgb(220, 225, 235))) g.DrawRectangle(p, x, 2, 5, 12);
            g.FillRectangle(Brushes.Gainsboro, x + 1, 0, 3, 2);
            if (!on) return;
            int h = Math.Max(1, (int)Math.Round(pct / 100.0 * 11));
            using (Brush b = new SolidBrush(pct < 20 ? Color.FromArgb(248, 113, 113) : col)) g.FillRectangle(b, x + 1, 14 - h, 4, h);
        }

        // ---------------- Universal Hardware Discovery ----------------
        static void DiscoverControllersHardware()
        {
            int gamesirCount = 0;
            int totalXusb = 0;
            string model = null;
            string transport = null;

            Guid xusb = new Guid("ec87f1e3-c13b-4100-b5f7-8b84d54260cb");
            IntPtr set = SetupDiGetClassDevs(ref xusb, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set != IntPtr.Zero && set != new IntPtr(-1))
            {
                try
                {
                    SP_DEVICE_INTERFACE_DATA d = new SP_DEVICE_INTERFACE_DATA();
                    d.cbSize = Marshal.SizeOf(d);
                    for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref xusb, i, ref d); i++)
                    {
                        SP_DEVICE_INTERFACE_DETAIL_DATA det = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                        det.cbSize = IntPtr.Size == 8 ? 8 : 6;
                        int req;
                        if (SetupDiGetDeviceInterfaceDetail(set, ref d, ref det, Marshal.SizeOf(det), out req, IntPtr.Zero)
                            && det.DevicePath != null)
                        {
                            totalXusb++;
                            string p = det.DevicePath.ToLowerInvariant();
                            if (p.Contains("vid_3537"))
                            {
                                gamesirCount++;
                                if (p.Contains("pid_1040") || p.Contains("pid_2106"))
                                {
                                    model = "GameSir Nova Lite";
                                    transport = "Receptor 2.4GHz";
                                }
                                else if (p.Contains("pid_1041"))
                                {
                                    model = "GameSir Cyclone 2";
                                    transport = "Receptor 2.4GHz";
                                }
                                else if (p.Contains("pid_1050"))
                                {
                                    model = "GameSir Super Nova";
                                    transport = "Receptor 2.4GHz";
                                }
                                else if (p.Contains("pid_1022") || p.Contains("pid_100b"))
                                {
                                    model = "GameSir G7 / T4";
                                    transport = "USB / 2.4GHz";
                                }
                                else
                                {
                                    model = "GameSir Controller";
                                    transport = "2.4GHz / USB";
                                }
                            }
                            else if (p.Contains("bth") || p.Contains("bluetooth"))
                            {
                                if (model == null) model = "GameSir Wireless";
                                transport = "Bluetooth Wireless";
                            }
                        }
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }

            lock (_lock)
            {
                _dongles = gamesirCount > 0 ? gamesirCount : totalXusb;
                _detectedModel = model ?? "GameSir Controller";
                _detectedTransport = transport ?? "XInput Wireless";
            }
        }

        static void PollLoop()
        {
            int tick = 0;
            while (_running)
            {
                try
                {
                    if (tick % 40 == 0) DiscoverControllersHardware();
                    DateTime now = DateTime.UtcNow;

                    // Scan all 4 XInput slots (0..3) so even if a PC has virtual gamepads on Slot 0,
                    // any active controllers on Slots 0..3 are automatically picked up!
                    DynamicXInput.XINPUT_STATE[] states = new DynamicXInput.XINPUT_STATE[4];
                    bool[] slotConnected = new bool[4];
                    List<int> activeSlots = new List<int>();

                    for (uint s = 0; s < 4; s++)
                    {
                        DynamicXInput.XINPUT_STATE st = new DynamicXInput.XINPUT_STATE();
                        if (DynamicXInput.GetState(s, ref st) == 0)
                        {
                            slotConnected[s] = true;
                            states[s] = st;
                            activeSlots.Add((int)s);
                        }
                    }

                    int[] targetSlots = new int[2] { 0, 1 };
                    if (activeSlots.Count >= 2)
                    {
                        targetSlots[0] = activeSlots[0];
                        targetSlots[1] = activeSlots[1];
                    }
                    else if (activeSlots.Count == 1)
                    {
                        if (activeSlots[0] == 1)
                        {
                            targetSlots[0] = 0;
                            targetSlots[1] = 1;
                        }
                        else
                        {
                            targetSlots[0] = activeSlots[0];
                            targetSlots[1] = activeSlots[0] == 0 ? 1 : 0;
                        }
                    }

                    lock (_lock)
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            ControllerStateData c = _ctl[i];
                            int hwSlot = targetSlots[i];
                            c.XInputSlot = hwSlot;

                            double dt = Math.Min(1.0, (now - c.LastTick).TotalSeconds);
                            c.LastTick = now;

                            c.Connected = slotConnected[hwSlot];
                            c.ModelName = _detectedModel;

                            if (!c.Connected)
                            {
                                c.Buttons = 0; c.LeftTrigger = c.RightTrigger = 0;
                                c.ThumbLX = c.ThumbLY = c.ThumbRX = c.ThumbRY = 0;
                                c.ConnectionSubtitle = _detectedTransport + " · Slot XInput " + (hwSlot + 1);
                                continue;
                            }

                            DynamicXInput.XINPUT_STATE st = states[hwSlot];
                            DynamicXInput.XINPUT_BATTERY_INFORMATION bat = new DynamicXInput.XINPUT_BATTERY_INFORMATION();
                            if (DynamicXInput.GetBatteryInformation((uint)hwSlot, 0, ref bat) == 0)
                            {
                                c.BatteryType = bat.BatteryType;
                                c.BatteryLevel = bat.BatteryLevel;

                                // When connected via Bluetooth/XInput wireless with Alkaline/NiMH reporting,
                                // or when the receiver reports a lower discrete band, sync immediately
                                if (bat.BatteryLevel == 2 && c.EstimatedPercent > 65) c.EstimatedPercent = 65;
                                else if (bat.BatteryLevel == 1 && c.EstimatedPercent > 30) c.EstimatedPercent = 30;
                                else if (bat.BatteryLevel == 0 && c.EstimatedPercent > 8) c.EstimatedPercent = 8;
                            }

                            string transportMode = (c.BatteryType == 2 || c.BatteryType == 3) ? "Sem Fio (XInput)" : _detectedTransport;
                            c.ConnectionSubtitle = transportMode + " · Slot XInput " + (hwSlot + 1);

                            bool active = st.dwPacketNumber != c.PacketNumber;
                            c.PacketNumber = st.dwPacketNumber;
                            c.Buttons = st.wButtons; c.LeftTrigger = st.bLeftTrigger; c.RightTrigger = st.bRightTrigger;
                            c.ThumbLX = st.sThumbLX; c.ThumbLY = st.sThumbLY; c.ThumbRX = st.sThumbRX; c.ThumbRY = st.sThumbRY;
                            c.SessionSeconds += dt;

                            double rate = active ? 100.0 / 25200.0 : 100.0 / 43200.0;
                            c.EstimatedPercent = Math.Max(1.0, c.EstimatedPercent - dt * rate);
                        }
                    }

                    if (tick % 300 == 0) SaveState();
                    tick++;
                }
                catch { }
                Thread.Sleep(50);
            }
        }

        static void Vibrate(int cardIndex)
        {
            if (cardIndex < 0 || cardIndex > 1) return;
            int hwSlot;
            lock (_lock) hwSlot = _ctl[cardIndex].XInputSlot;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                DynamicXInput.XINPUT_VIBRATION on = new DynamicXInput.XINPUT_VIBRATION { wLeftMotorSpeed = 50000, wRightMotorSpeed = 50000 };
                DynamicXInput.XINPUT_VIBRATION off = new DynamicXInput.XINPUT_VIBRATION();
                DynamicXInput.SetState((uint)hwSlot, ref on); Thread.Sleep(220);
                DynamicXInput.SetState((uint)hwSlot, ref off); Thread.Sleep(100);
                DynamicXInput.SetState((uint)hwSlot, ref on); Thread.Sleep(220);
                DynamicXInput.SetState((uint)hwSlot, ref off);
            });
        }

        static void SetBattery(int slot, double pct)
        {
            if (slot < 0 || slot > 1) return;
            lock (_lock)
            {
                _ctl[slot].EstimatedPercent = Math.Max(1, Math.Min(100, pct));
                if (pct >= 99) _ctl[slot].SessionSeconds = 0;
            }
            SaveState();
        }

        static void LoadState()
        {
            try
            {
                if (!File.Exists(_stateFile)) return;
                string[] l = File.ReadAllLines(_stateFile);
                for (int i = 0; i < 2 && i < l.Length; i++)
                {
                    double v;
                    if (double.TryParse(l[i], NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                        _ctl[i].EstimatedPercent = Math.Max(1, Math.Min(100, v));
                }
            }
            catch { }
        }

        static void SaveState()
        {
            try
            {
                string a, b;
                lock (_lock)
                {
                    a = _ctl[0].EstimatedPercent.ToString("F2", CultureInfo.InvariantCulture);
                    b = _ctl[1].EstimatedPercent.ToString("F2", CultureInfo.InvariantCulture);
                }
                File.WriteAllLines(_stateFile, new[] { a, b });
            }
            catch { }
        }

        // ---------------- Tiny HTTP server (Disk + Embedded Resource Fallback) ----------------
        static void ServerLoop()
        {
            try
            {
                while (_running)
                {
                    TcpClient c = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(Handle, c);
                }
            }
            catch { }
        }

        static void Handle(object o)
        {
            using (TcpClient client = (TcpClient)o)
            {
                try
                {
                    client.ReceiveTimeout = 3000;
                    NetworkStream ns = client.GetStream();
                    StreamReader rd = new StreamReader(ns, Encoding.ASCII);
                    string first = rd.ReadLine();
                    if (string.IsNullOrEmpty(first)) return;
                    string line; while (!string.IsNullOrEmpty(line = rd.ReadLine())) { }
                    string[] parts = first.Split(' ');
                    if (parts.Length < 2) return;
                    string url = parts[1];
                    string path = url.Split('?')[0];

                    if (path == "/api/status") { Send(ns, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(StatusJson())); return; }
                    if (path == "/api/vibrate") { Vibrate(Q(url, "slot", 0)); Send(ns, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}")); return; }
                    if (path == "/api/set-battery") { SetBattery(Q(url, "slot", 0), Q(url, "percent", 100)); Send(ns, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}")); return; }

                    if (path == "/") path = "/index.html";
                    string pub = Path.GetFullPath(Path.Combine(_baseDir, "public"));
                    string file = Path.GetFullPath(Path.Combine(pub, Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', '\\')));
                    if (file.StartsWith(pub, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                    {
                        Send(ns, "200 OK", Mime(file), File.ReadAllBytes(file));
                        return;
                    }

                    // Fallback to Embedded Resource inside the single-file .exe!
                    string resName = Path.GetFileName(path).ToLowerInvariant();
                    byte[] embedded = GetEmbeddedResourceBytes(resName);
                    if (embedded != null)
                    {
                        Send(ns, "200 OK", Mime(resName), embedded);
                        return;
                    }

                    Send(ns, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found"));
                }
                catch { }
            }
        }

        static byte[] GetEmbeddedResourceBytes(string name)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    byte[] buf = new byte[s.Length];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = s.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return buf;
                }
            }
            catch { return null; }
        }

        static string Mime(string f)
        {
            switch (Path.GetExtension(f).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".svg": return "image/svg+xml";
                case ".css": return "text/css";
                case ".js": return "application/javascript";
                default: return "application/octet-stream";
            }
        }

        static int Q(string url, string key, int def)
        {
            int qi = url.IndexOf('?');
            if (qi < 0) return def;
            foreach (string p in url.Substring(qi + 1).Split('&'))
            {
                string[] kv = p.Split('=');
                int v;
                if (kv.Length == 2 && kv[0] == key && int.TryParse(kv[1], out v)) return v;
            }
            return def;
        }

        static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static string StatusJson()
        {
            StringBuilder sb = new StringBuilder();
            lock (_lock)
            {
                sb.Append("{\"dongleCount\":").Append(_dongles).Append(",\"controllers\":[");
                for (int i = 0; i < 2; i++)
                {
                    ControllerStateData c = _ctl[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"slot\":").Append(i)
                      .Append(",\"xinputSlot\":").Append(c.XInputSlot)
                      .Append(",\"connected\":").Append(c.Connected ? "true" : "false")
                      .Append(",\"modelName\":\"").Append(EscapeJson(c.ModelName)).Append("\"")
                      .Append(",\"connectionSubtitle\":\"").Append(EscapeJson(c.ConnectionSubtitle)).Append("\"")
                      .Append(",\"batteryPercent\":").Append((int)Math.Round(c.EstimatedPercent))
                      .Append(",\"xinputLevel\":").Append(c.BatteryLevel)
                      .Append(",\"batteryType\":").Append(c.BatteryType)
                      .Append(",\"buttons\":").Append(c.Buttons)
                      .Append(",\"leftTrigger\":").Append(c.LeftTrigger)
                      .Append(",\"rightTrigger\":").Append(c.RightTrigger)
                      .Append(",\"thumbLX\":").Append(c.ThumbLX)
                      .Append(",\"thumbLY\":").Append(c.ThumbLY)
                      .Append(",\"thumbRX\":").Append(c.ThumbRX)
                      .Append(",\"thumbRY\":").Append(c.ThumbRY)
                      .Append(",\"sessionSeconds\":").Append(((int)c.SessionSeconds).ToString(CultureInfo.InvariantCulture))
                      .Append('}');
                }
                sb.Append("]}");
            }
            return sb.ToString();
        }

        static void Send(NetworkStream ns, string status, string type, byte[] body)
        {
            byte[] h = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            ns.Write(h, 0, h.Length);
            ns.Write(body, 0, body.Length);
            ns.Flush();
        }
    }
}
