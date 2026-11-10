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
using Microsoft.Win32;

[assembly: AssemblyTitle("GameSir Battery Hub")]
[assembly: AssemblyDescription("Dual Wireless Controller Battery Monitor")]
[assembly: AssemblyCompany("GameSir Community")]
[assembly: AssemblyProduct("GameSir Battery Hub")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace GameSirBatteryMonitor
{
    public class ControllerStateData
    {
        public const double BATTERY_CAPACITY_MAH = 600.0;

        public int CardIndex;
        public int XInputSlot;
        public bool Connected;
        public bool WasConnected;
        public bool IsCharging;
        public byte BatteryType;
        public byte BatteryLevel;

        // Exact mAh remaining (persisted to disk continuously, never resets unless user clicks 100% or finishes charging)
        public double RemainingMah = 600.0;
        public double EstimatedPercent
        {
            get { return Math.Max(0.5, Math.Min(100.0, (RemainingMah / BATTERY_CAPACITY_MAH) * 100.0)); }
            set { RemainingMah = Math.Max(3.0, Math.Min(BATTERY_CAPACITY_MAH, (value / 100.0) * BATTERY_CAPACITY_MAH)); }
        }

        // Notification threshold flags so popups trigger once per threshold crossing
        public bool NotifiedLow15 = false;
        public bool NotifiedCharge80 = false;
        public bool NotifiedCharge100 = false;

        public uint PacketNumber;
        public ushort Buttons;
        public ushort PrevButtons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
        public short PrevThumbLX, PrevThumbLY, PrevThumbRX, PrevThumbRY;

        // Real activity vs idle tracking
        public bool InitializedPrev = false;
        public double ActivePlaySeconds = 0.0;
        public double IdleSeconds = 15.0; // Starts in Resting state until a real button/stick is touched
        public double RecentActionScore = 0.0; // Rolling intensity score (0..100)
        public int CurrentDrawMa = 11;
        public string ActivityState = "Em Repouso"; // "Carregando", "Carga Completa", "Jogo Intenso", "Em Uso", "Em Repouso", "Hibernando", "Desligado"
        public DateTime VibrateUntil = DateTime.MinValue;

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

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInput14_GetState(uint dwUserIndex, ref XINPUT_STATE pState);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        private static extern uint XInput14_SetState(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern uint XInput14_GetBattery(uint dwUserIndex, byte devType, ref XINPUT_BATTERY_INFORMATION pBatteryInformation);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInput91_GetState(uint dwUserIndex, ref XINPUT_STATE pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputSetState")]
        private static extern uint XInput91_SetState(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

        private static bool _use14 = true;

        static DynamicXInput()
        {
            try
            {
                XINPUT_STATE test = new XINPUT_STATE();
                XInput14_GetState(0, ref test);
                _use14 = true;
            }
            catch
            {
                _use14 = false;
            }
        }

        public static uint GetState(uint index, ref XINPUT_STATE state)
        {
            try
            {
                return _use14 ? XInput14_GetState(index, ref state) : XInput91_GetState(index, ref state);
            }
            catch { return 1167; }
        }

        public static uint SetState(uint index, ref XINPUT_VIBRATION vib)
        {
            try
            {
                return _use14 ? XInput14_SetState(index, ref vib) : XInput91_SetState(index, ref vib);
            }
            catch { return 1167; }
        }

        public static uint GetBatteryInformation(uint index, byte devType, ref XINPUT_BATTERY_INFORMATION bat)
        {
            try
            {
                if (_use14)
                {
                    return XInput14_GetBattery(index, devType, ref bat);
                }
            }
            catch { }

            bat.BatteryType = 1;
            bat.BatteryLevel = 3;
            return 0;
        }
    }

    public class Program : ApplicationContext
    {
        public const int PORT = 47832;
        private const string RUN_REG_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RUN_VALUE_NAME = "GameSirBatteryHub";

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

        [DllImport("hid.dll", SetLastError = true)]
        static extern void HidD_GetHidGuid(out Guid hidGuid);

        static readonly ControllerStateData[] _ctl = new ControllerStateData[2];
        static int _dongles = 0;
        static int _usbChargingCables = 0;
        static string _detectedModel = "GameSir Controller";
        static string _detectedTransport = "Receptor 2.4GHz";
        static readonly object _lock = new object();
        static string _baseDir, _stateFile;
        static TcpListener _listener;
        static volatile bool _running = true;
        static Program _instance;
        static List<string> _pendingAlerts = new List<string>();

        NotifyIcon _tray;
        MenuItem _autoStartMenuItem;
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

            string firstRunFlag = Path.Combine(appDataFolder, "autostart_initialized.flag");
            if (!File.Exists(firstRunFlag))
            {
                try
                {
                    SetAutoStart(true);
                    File.WriteAllText(firstRunFlag, "1");
                }
                catch { }
            }

            Application.ApplicationExit += (s, e) => SaveState();
            try
            {
                SystemEvents.SessionEnding += (s, e) => SaveState();
            }
            catch { }

            Application.EnableVisualStyles();
            Application.Run(new Program(!background));
        }

        public Program(bool openWindow)
        {
            _instance = this;
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("Abrir Painel GameSir Battery Hub", (s, e) => OpenAppWindow());
            menu.MenuItems.Add("Calibrar Controles no Windows (joy.cpl)", (s, e) => OpenWindowsCalibration());
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Vibrar Controle P1", (s, e) => Vibrate(0));
            menu.MenuItems.Add("Vibrar Controle P2", (s, e) => Vibrate(1));
            menu.MenuItems.Add("-");

            _autoStartMenuItem = new MenuItem("Iniciar junto com o Windows", (s, e) =>
            {
                bool next = !IsAutoStartEnabled();
                SetAutoStart(next);
                _autoStartMenuItem.Checked = IsAutoStartEnabled();
            });
            _autoStartMenuItem.Checked = IsAutoStartEnabled();
            menu.MenuItems.Add(_autoStartMenuItem);

            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Sair completamente (Parar 2º plano)", (s, e) =>
            {
                _running = false;
                SaveState();
                _tray.Visible = false;
                Application.Exit();
            });

            _tray = new NotifyIcon
            {
                Text = "GameSir Battery Hub (Rodando em 2º plano)",
                Icon = TrayIcon(100, 100, false, false),
                ContextMenu = menu,
                Visible = true
            };
            _tray.DoubleClick += (s, e) => OpenAppWindow();
            _tray.BalloonTipClicked += (s, e) => OpenAppWindow();
            _tray.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) OpenAppWindow();
            };

            new Thread(PollLoop) { IsBackground = true }.Start();
            new Thread(ServerLoop) { IsBackground = true }.Start();

            _trayTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _trayTimer.Tick += (s, e) => UpdateTray();
            _trayTimer.Start();
            UpdateTray();

            if (openWindow)
            {
                OpenAppWindow();
            }
        }

        public static void PushNotification(string title, string message, ToolTipIcon iconType)
        {
            lock (_lock)
            {
                string alertJson = "{\"title\":\"" + EscapeJson(title) + "\",\"message\":\"" + EscapeJson(message) + "\",\"level\":\"" + (iconType == ToolTipIcon.Warning ? "warn" : "info") + "\"}";
                _pendingAlerts.Add(alertJson);
                if (_pendingAlerts.Count > 10) _pendingAlerts.RemoveAt(0);
            }
            try
            {
                if (_instance != null && _instance._tray != null)
                {
                    _instance._tray.ShowBalloonTip(6000, title, message, iconType);
                }
            }
            catch { }
        }

        public static void OpenWindowsCalibration()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "control.exe",
                    Arguments = "joy.cpl",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_REG_KEY, false))
                {
                    if (key == null) return false;
                    object val = key.GetValue(RUN_VALUE_NAME);
                    return val != null && val.ToString().Length > 0;
                }
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_REG_KEY, true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        string exePath = Assembly.GetExecutingAssembly().Location;
                        key.SetValue(RUN_VALUE_NAME, "\"" + exePath + "\" --background");
                    }
                    else
                    {
                        key.DeleteValue(RUN_VALUE_NAME, false);
                    }
                }
            }
            catch { }
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
                        Arguments = "--app=" + url + " --user-data-dir=\"" + profile + "\" --window-size=1360,940 --disable-background-mode --disable-extensions --no-first-run --no-default-browser-check",
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
            int p0, p1; bool c0, c1; string s0, s1;
            lock (_lock)
            {
                p0 = (int)Math.Floor(_ctl[0].EstimatedPercent + 0.001);
                p1 = (int)Math.Floor(_ctl[1].EstimatedPercent + 0.001);
                c0 = _ctl[0].Connected || _ctl[0].IsCharging;
                c1 = _ctl[1].Connected || _ctl[1].IsCharging;
                s0 = p0 + "% (" + _ctl[0].ActivityState + ")";
                s1 = p1 + "% (" + _ctl[1].ActivityState + ")";
            }
            string txt = "P1: " + s0 + " | P2: " + s1;
            if (txt.Length > 63) txt = txt.Substring(0, 63);
            _tray.Text = txt;
            if (_autoStartMenuItem != null) _autoStartMenuItem.Checked = IsAutoStartEnabled();
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
            int h = Math.Max(1, (int)Math.Round(pct / 100.0 * 11));
            Color drawCol = !on ? Color.FromArgb(110, 120, 140) : (pct < 20 ? Color.FromArgb(248, 113, 113) : col);
            using (Brush b = new SolidBrush(drawCol)) g.FillRectangle(b, x + 1, 14 - h, 4, h);
        }

        // ---------------- Universal Hardware Discovery ----------------
        static void DiscoverControllersHardware()
        {
            int gamesirCount = 0;
            int wiredUsbCableCount = 0;
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
                            else if (p.Contains("vid_045e&pid_028e"))
                            {
                                // GameSir Nova Lite connected directly to the PC via USB-C cable enumerates as VID_045E&PID_028E (Wired XInput)
                                wiredUsbCableCount++;
                                if (model == null) model = "GameSir Nova Lite";
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

            if (gamesirCount > 2)
            {
                wiredUsbCableCount += (gamesirCount - 2);
            }

            lock (_lock)
            {
                _dongles = gamesirCount > 0 ? gamesirCount : totalXusb;
                _usbChargingCables = wiredUsbCableCount;
                _detectedModel = model ?? "GameSir Nova Lite";
                _detectedTransport = transport ?? "Receptor 2.4GHz";
            }
        }

        // Detects genuine human interaction while filtering out Hall Effect sensor ADC micro-jitter
        static bool HasGenuineHumanInput(ControllerStateData c, DynamicXInput.XINPUT_STATE st, out double intensityImpulse)
        {
            intensityImpulse = 0.0;

            bool buttonsPressed = st.wButtons != 0;
            bool buttonsChanged = st.wButtons != c.PrevButtons;
            bool triggersActive = st.bLeftTrigger > 12 || st.bRightTrigger > 12;

            const int REST_DEADZONE = 4500;
            bool stickDeflected =
                Math.Abs((int)st.sThumbLX) > REST_DEADZONE ||
                Math.Abs((int)st.sThumbLY) > REST_DEADZONE ||
                Math.Abs((int)st.sThumbRX) > REST_DEADZONE ||
                Math.Abs((int)st.sThumbRY) > REST_DEADZONE;

            const int DELTA_THRESHOLD = 1400;
            int dLX = Math.Abs((int)st.sThumbLX - (int)c.PrevThumbLX);
            int dLY = Math.Abs((int)st.sThumbLY - (int)c.PrevThumbLY);
            int dRX = Math.Abs((int)st.sThumbRX - (int)c.PrevThumbRX);
            int dRY = Math.Abs((int)st.sThumbRY - (int)c.PrevThumbRY);
            bool stickMoved = dLX > DELTA_THRESHOLD || dLY > DELTA_THRESHOLD || dRX > DELTA_THRESHOLD || dRY > DELTA_THRESHOLD;

            if (buttonsChanged) intensityImpulse += 18.0;
            if (buttonsPressed) intensityImpulse += 8.0;
            if (triggersActive) intensityImpulse += 10.0 * ((st.bLeftTrigger + st.bRightTrigger) / 510.0);
            if (stickMoved) intensityImpulse += 12.0;
            else if (stickDeflected) intensityImpulse += 6.0;

            return buttonsPressed || triggersActive || stickDeflected || stickMoved;
        }

        static void CheckBatteryPopups(ControllerStateData c, double prevPct, double newPct)
        {
            string pTag = "CONTROLE P" + (c.CardIndex + 1);
            if (c.IsCharging)
            {
                c.NotifiedLow15 = false;
                if (!c.NotifiedCharge80 && prevPct < 80.0 && newPct >= 80.0 && newPct < 99.5)
                {
                    c.NotifiedCharge80 = true;
                    PushNotification("⚡ " + pTag + " ATINGIU 80% DE CARGA",
                        "O " + pTag + " já está com 80% de bateria (carga rápida concluída, pronto para jogar)!",
                        ToolTipIcon.Info);
                }
                if (!c.NotifiedCharge100 && newPct >= 99.95)
                {
                    c.NotifiedCharge100 = true;
                    c.IsCharging = false;
                    c.RemainingMah = ControllerStateData.BATTERY_CAPACITY_MAH;
                    c.ActivePlaySeconds = 0;
                    PushNotification("✅ " + pTag + " 100% CARREGADO!",
                        "Bateria do " + pTag + " atingiu 100% (600 mAh). Você já pode desconectar o cabo carregador!",
                        ToolTipIcon.Info);
                }
            }
            else
            {
                if (newPct < 78.0) c.NotifiedCharge80 = false;
                if (newPct < 98.0) c.NotifiedCharge100 = false;

                if (!c.NotifiedLow15 && newPct <= 15.0)
                {
                    c.NotifiedLow15 = true;
                    int pctInt = (int)Math.Floor(newPct + 0.001);
                    PushNotification("⚠️ BATERIA FRACA — " + pTag + " (" + pctInt + "%)",
                        "A bateria do " + pTag + " está abaixo de 15%! Conecte o cabo USB-C para recarregar.",
                        ToolTipIcon.Warning);
                }
                else if (newPct > 18.0)
                {
                    c.NotifiedLow15 = false;
                }
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

                    bool stateTransitioned = false;

                    lock (_lock)
                    {
                        // Auto-detect USB charging cable connection on the PC ONLY for connected controllers:
                        // When a controller is turned OFF (!slotConnected), the PC cannot know if it's simply off
                        // or plugged into a wall outlet, so it must stay as "Desligado" (IsCharging = false).
                        bool c0Conn = slotConnected[targetSlots[0]];
                        bool c1Conn = slotConnected[targetSlots[1]];

                        if (_usbChargingCables >= 2)
                        {
                            _ctl[0].IsCharging = c0Conn;
                            _ctl[1].IsCharging = c1Conn;
                        }
                        else if (_usbChargingCables == 1)
                        {
                            if (c0Conn && !c1Conn)
                            {
                                _ctl[0].IsCharging = true;
                                _ctl[1].IsCharging = false;
                            }
                            else if (c1Conn && !c0Conn)
                            {
                                _ctl[0].IsCharging = false;
                                _ctl[1].IsCharging = true;
                            }
                            else if (c0Conn && c1Conn)
                            {
                                if (_ctl[0].RemainingMah <= _ctl[1].RemainingMah)
                                {
                                    _ctl[0].IsCharging = true;
                                    _ctl[1].IsCharging = false;
                                }
                                else
                                {
                                    _ctl[0].IsCharging = false;
                                    _ctl[1].IsCharging = true;
                                }
                            }
                            else
                            {
                                _ctl[0].IsCharging = false;
                                _ctl[1].IsCharging = false;
                            }
                        }
                        else
                        {
                            _ctl[0].IsCharging = false;
                            _ctl[1].IsCharging = false;
                        }

                        for (int i = 0; i < 2; i++)
                        {
                            ControllerStateData c = _ctl[i];
                            int hwSlot = targetSlots[i];
                            c.XInputSlot = hwSlot;

                            double dt = Math.Min(1.0, Math.Max(0.001, (now - c.LastTick).TotalSeconds));
                            c.LastTick = now;

                            bool nowConnected = slotConnected[hwSlot];
                            bool justReconnected = nowConnected && !c.WasConnected;
                            if (nowConnected != c.WasConnected)
                            {
                                stateTransitioned = true;
                                c.WasConnected = nowConnected;
                            }
                            c.Connected = nowConnected;
                            c.ModelName = _detectedModel;

                            double prevPct = c.EstimatedPercent;

                            if (!c.Connected)
                            {
                                c.IsCharging = false;
                                c.InitializedPrev = false;
                                c.Buttons = 0; c.LeftTrigger = c.RightTrigger = 0;
                                c.ThumbLX = c.ThumbLY = c.ThumbRX = c.ThumbRY = 0;
                                c.CurrentDrawMa = 0;
                                c.ActivityState = "Desligado";
                                c.ConnectionSubtitle = _detectedTransport + " · Slot XInput " + (hwSlot + 1);
                                continue;
                            }

                            DynamicXInput.XINPUT_STATE st = states[hwSlot];
                            if (!c.InitializedPrev)
                            {
                                c.PrevButtons = st.wButtons;
                                c.PrevThumbLX = st.sThumbLX;
                                c.PrevThumbLY = st.sThumbLY;
                                c.PrevThumbRX = st.sThumbRX;
                                c.PrevThumbRY = st.sThumbRY;
                                c.InitializedPrev = true;
                            }

                            DynamicXInput.XINPUT_BATTERY_INFORMATION bat = new DynamicXInput.XINPUT_BATTERY_INFORMATION();
                            if (DynamicXInput.GetBatteryInformation((uint)hwSlot, 0, ref bat) == 0)
                            {
                                byte prevHwLevel = c.BatteryLevel;
                                c.BatteryType = bat.BatteryType;
                                c.BatteryLevel = bat.BatteryLevel;

                                // 1. Upward Hardware Voltage Sync (Recharged in wall outlet or USB while off):
                                if (bat.BatteryLevel == 3 && c.EstimatedPercent <= 66.0 && (justReconnected || (prevHwLevel > 0 && prevHwLevel < 3)))
                                {
                                    c.RemainingMah = ControllerStateData.BATTERY_CAPACITY_MAH;
                                    c.ActivePlaySeconds = 0;
                                    c.NotifiedLow15 = false;
                                    PushNotification("✅ CONTROLE P" + (i + 1) + " RECARREGADO (100%)",
                                        "O Controle P" + (i + 1) + " foi reconhecido com carga completa!",
                                        ToolTipIcon.Info);
                                }
                                else if (bat.BatteryLevel == 2 && c.EstimatedPercent <= 30.5 && (justReconnected || (prevHwLevel > 0 && prevHwLevel < 2)))
                                {
                                    c.EstimatedPercent = 65.0;
                                    c.NotifiedLow15 = false;
                                }
                                // 2. Downward Hardware Voltage Sync (when physical voltage crosses into a lower band):
                                else if (!c.IsCharging)
                                {
                                    if (bat.BatteryLevel == 2 && c.EstimatedPercent > 65.0) c.EstimatedPercent = 65.0;
                                    else if (bat.BatteryLevel == 1 && c.EstimatedPercent > 30.0) c.EstimatedPercent = 30.0;
                                    else if (bat.BatteryLevel == 0 && c.EstimatedPercent > 8.0) c.EstimatedPercent = 8.0;
                                }
                            }

                            // Handle USB-C Charging on PC (CC/CV Li-Ion 600 mAh curve)
                            if (c.IsCharging)
                            {
                                double chargeMa = prevPct < 80.0 ? 300.0 : (prevPct < 92.0 ? 200.0 : 110.0);
                                c.CurrentDrawMa = (int)Math.Round(chargeMa);
                                c.ActivityState = "Carregando ⚡";
                                c.ConnectionSubtitle = "Cabo USB-C · Slot XInput " + (hwSlot + 1);
                                double addedMah = chargeMa * (dt / 3600.0);
                                c.RemainingMah = Math.Min(ControllerStateData.BATTERY_CAPACITY_MAH, c.RemainingMah + addedMah);

                                c.Buttons = st.wButtons;
                                c.LeftTrigger = st.bLeftTrigger;
                                c.RightTrigger = st.bRightTrigger;
                                c.ThumbLX = st.sThumbLX;
                                c.ThumbLY = st.sThumbLY;
                                c.ThumbRX = st.sThumbRX;
                                c.ThumbRY = st.sThumbRY;

                                CheckBatteryPopups(c, prevPct, c.EstimatedPercent);
                                continue;
                            }

                            string transportMode = (c.BatteryType == 2 || c.BatteryType == 3) ? "Sem Fio (XInput)" : _detectedTransport;
                            c.ConnectionSubtitle = transportMode + " · Slot XInput " + (hwSlot + 1);

                            double impulse;
                            bool humanActive = HasGenuineHumanInput(c, st, out impulse);
                            bool isVibrating = now < c.VibrateUntil;

                            c.PrevButtons = st.wButtons;
                            c.PrevThumbLX = st.sThumbLX;
                            c.PrevThumbLY = st.sThumbLY;
                            c.PrevThumbRX = st.sThumbRX;
                            c.PrevThumbRY = st.sThumbRY;

                            c.PacketNumber = st.dwPacketNumber;
                            c.Buttons = st.wButtons;
                            c.LeftTrigger = st.bLeftTrigger;
                            c.RightTrigger = st.bRightTrigger;
                            c.ThumbLX = st.sThumbLX;
                            c.ThumbLY = st.sThumbLY;
                            c.ThumbRX = st.sThumbRX;
                            c.ThumbRY = st.sThumbRY;

                            c.RecentActionScore = Math.Max(0.0, Math.Min(100.0, c.RecentActionScore * Math.Pow(0.82, dt) + impulse));

                            if (humanActive || isVibrating)
                            {
                                c.IdleSeconds = 0.0;
                            }
                            else
                            {
                                c.IdleSeconds += dt;
                            }

                            // Telemetry-weighted real-time current draw (mA)
                            double lMag = Math.Min(1.0, Math.Sqrt((double)st.sThumbLX * st.sThumbLX + (double)st.sThumbLY * st.sThumbLY) / 32767.0);
                            double rMag = Math.Min(1.0, Math.Sqrt((double)st.sThumbRX * st.sThumbRX + (double)st.sThumbRY * st.sThumbRY) / 32767.0);
                            double trigLoad = (st.bLeftTrigger + st.bRightTrigger) / 510.0;

                            string prevActivity = c.ActivityState;
                            double drawMa;
                            if (isVibrating)
                            {
                                drawMa = 195.0;
                                c.ActivityState = "Vibrando";
                                c.ActivePlaySeconds += dt;
                            }
                            else if (c.IdleSeconds < 12.0)
                            {
                                c.ActivePlaySeconds += dt;
                                drawMa = 68.0 + (lMag + rMag) * 5.5 + trigLoad * 6.0 + (c.RecentActionScore / 100.0) * 10.0;
                                c.ActivityState = (c.RecentActionScore > 38.0 || lMag > 0.6 || rMag > 0.6) ? "Jogo Intenso" : "Em Uso";
                            }
                            else if (c.IdleSeconds < 600.0)
                            {
                                drawMa = 11.0;
                                c.ActivityState = "Em Repouso";
                            }
                            else
                            {
                                drawMa = 0.0;
                                c.ActivityState = "Hibernando";
                            }

                            if (prevActivity != c.ActivityState) stateTransitioned = true;
                            c.CurrentDrawMa = (int)Math.Round(drawMa);

                            double consumedMah = drawMa * (dt / 3600.0);
                            double nextMah = Math.Max(3.0, c.RemainingMah - consumedMah);

                            // Clamp floor to current hardware voltage band so estimate never drifts below actual hardware voltage tier
                            double minPctForTier = (c.BatteryLevel == 3) ? 66.0 : (c.BatteryLevel == 2) ? 31.0 : (c.BatteryLevel == 1) ? 9.0 : 1.0;
                            double minMahForTier = (minPctForTier / 100.0) * ControllerStateData.BATTERY_CAPACITY_MAH;
                            c.RemainingMah = Math.Max(minMahForTier, nextMah);

                            CheckBatteryPopups(c, prevPct, c.EstimatedPercent);
                        }
                    }

                    if (stateTransitioned || (tick % 40 == 0))
                    {
                        SaveState();
                    }
                    tick++;
                }
                catch { }
                Thread.Sleep(50);
            }
        }

        static void ToggleCharging(int slot)
        {
            if (slot < 0 || slot > 1) return;
            bool nowCharging;
            double pct;
            lock (_lock)
            {
                ControllerStateData c = _ctl[slot];
                c.IsCharging = !c.IsCharging;
                nowCharging = c.IsCharging;
                if (nowCharging && c.EstimatedPercent >= 99.9)
                {
                    // If already at 100%, allow charging or keep 100%
                    c.NotifiedCharge100 = false;
                }
                pct = c.EstimatedPercent;
            }
            SaveState();
            if (nowCharging)
            {
                PushNotification("⚡ CONTROLE P" + (slot + 1) + " CARREGANDO (" + pct.ToString("F1", CultureInfo.InvariantCulture) + "%)",
                    "Modo de recarga ativado para o P" + (slot + 1) + ". Você receberá um alerta em 80% e em 100%!",
                    ToolTipIcon.Info);
            }
        }

        static void Vibrate(int cardIndex)
        {
            if (cardIndex < 0 || cardIndex > 1) return;
            int hwSlot;
            lock (_lock)
            {
                hwSlot = _ctl[cardIndex].XInputSlot;
                _ctl[cardIndex].VibrateUntil = DateTime.UtcNow.AddMilliseconds(600);
            }

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
            double prevPct;
            lock (_lock)
            {
                ControllerStateData c = _ctl[slot];
                prevPct = c.EstimatedPercent;
                c.EstimatedPercent = Math.Max(1, Math.Min(100, pct));
                if (pct >= 99)
                {
                    c.ActivePlaySeconds = 0;
                    c.IdleSeconds = 15.0;
                    c.IsCharging = false;
                    c.NotifiedLow15 = false;
                }
                else if (pct <= 15)
                {
                    c.NotifiedLow15 = false;
                }
                CheckBatteryPopups(c, prevPct, c.EstimatedPercent);
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
                    string[] parts = l[i].Split('|');
                    double v;
                    if (double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                        _ctl[i].EstimatedPercent = Math.Max(0.5, Math.Min(100.0, v));
                    if (parts.Length > 1)
                    {
                        double playSec;
                        if (double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out playSec))
                            _ctl[i].ActivePlaySeconds = Math.Max(0, playSec);
                    }
                    if (parts.Length > 2)
                    {
                        double mah;
                        if (double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out mah))
                            _ctl[i].RemainingMah = Math.Max(3.0, Math.Min(ControllerStateData.BATTERY_CAPACITY_MAH, mah));
                    }
                    if (parts.Length > 3)
                    {
                        byte bLvl;
                        if (byte.TryParse(parts[3], out bLvl))
                            _ctl[i].BatteryLevel = bLvl;
                    }
                    _ctl[i].IsCharging = false;
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
                    a = _ctl[0].EstimatedPercent.ToString("F4", CultureInfo.InvariantCulture) + "|" +
                        _ctl[0].ActivePlaySeconds.ToString("F1", CultureInfo.InvariantCulture) + "|" +
                        _ctl[0].RemainingMah.ToString("F4", CultureInfo.InvariantCulture) + "|" +
                        _ctl[0].BatteryLevel.ToString(CultureInfo.InvariantCulture);
                    b = _ctl[1].EstimatedPercent.ToString("F4", CultureInfo.InvariantCulture) + "|" +
                        _ctl[1].ActivePlaySeconds.ToString("F1", CultureInfo.InvariantCulture) + "|" +
                        _ctl[1].RemainingMah.ToString("F4", CultureInfo.InvariantCulture) + "|" +
                        _ctl[1].BatteryLevel.ToString(CultureInfo.InvariantCulture);
                }
                string tmp = _stateFile + ".tmp";
                File.WriteAllLines(tmp, new[] { a, b });
                if (File.Exists(_stateFile)) File.Delete(_stateFile);
                File.Move(tmp, _stateFile);
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
                    if (path == "/api/toggle-charge") { ToggleCharging(Q(url, "slot", 0)); Send(ns, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}")); return; }
                    if (path == "/api/open-joycpl") { OpenWindowsCalibration(); Send(ns, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}")); return; }
                    if (path == "/api/toggle-autostart")
                    {
                        bool next = !IsAutoStartEnabled();
                        SetAutoStart(next);
                        Send(ns, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"autoStart\":" + (IsAutoStartEnabled() ? "true" : "false") + "}"));
                        return;
                    }

                    if (path == "/") path = "/index.html";
                    string pub = Path.GetFullPath(Path.Combine(_baseDir, "public"));
                    string file = Path.GetFullPath(Path.Combine(pub, Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', '\\')));
                    if (file.StartsWith(pub, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                    {
                        Send(ns, "200 OK", Mime(file), File.ReadAllBytes(file));
                        return;
                    }

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
            bool autoStart = IsAutoStartEnabled();
            lock (_lock)
            {
                sb.Append("{\"dongleCount\":").Append(_dongles)
                  .Append(",\"autoStart\":").Append(autoStart ? "true" : "false")
                  .Append(",\"alerts\":[");
                for (int a = 0; a < _pendingAlerts.Count; a++)
                {
                    if (a > 0) sb.Append(',');
                    sb.Append(_pendingAlerts[a]);
                }
                _pendingAlerts.Clear();
                sb.Append("],\"controllers\":[");
                for (int i = 0; i < 2; i++)
                {
                    ControllerStateData c = _ctl[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"slot\":").Append(i)
                      .Append(",\"xinputSlot\":").Append(c.XInputSlot)
                      .Append(",\"connected\":").Append(c.Connected ? "true" : "false")
                      .Append(",\"isCharging\":").Append(c.IsCharging ? "true" : "false")
                      .Append(",\"modelName\":\"").Append(EscapeJson(c.ModelName)).Append("\"")
                      .Append(",\"connectionSubtitle\":\"").Append(EscapeJson(c.ConnectionSubtitle)).Append("\"")
                      .Append(",\"activityState\":\"").Append(EscapeJson(c.ActivityState)).Append("\"")
                      .Append(",\"currentDrawMa\":").Append(c.CurrentDrawMa)
                      .Append(",\"remainingMah\":").Append(c.RemainingMah.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(",\"batteryPercent\":").Append((int)Math.Floor(c.EstimatedPercent + 0.001))
                      .Append(",\"batteryPercentExact\":").Append(c.EstimatedPercent.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(",\"xinputLevel\":").Append(c.BatteryLevel)
                      .Append(",\"batteryType\":").Append(c.BatteryType)
                      .Append(",\"buttons\":").Append(c.Buttons)
                      .Append(",\"leftTrigger\":").Append(c.LeftTrigger)
                      .Append(",\"rightTrigger\":").Append(c.RightTrigger)
                      .Append(",\"thumbLX\":").Append(c.ThumbLX)
                      .Append(",\"thumbLY\":").Append(c.ThumbLY)
                      .Append(",\"thumbRX\":").Append(c.ThumbRX)
                      .Append(",\"thumbRY\":").Append(c.ThumbRY)
                      .Append(",\"sessionSeconds\":").Append(((int)c.ActivePlaySeconds).ToString(CultureInfo.InvariantCulture))
                      .Append(",\"idleSeconds\":").Append(((int)c.IdleSeconds).ToString(CultureInfo.InvariantCulture))
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
