using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KeyboardOverlay
{
    internal sealed class OverlaySettings
    {
        public static readonly string[] ClassicKeys = new string[] {
            "esc", "1", "2", "3", "4", "5", "tab", "q", "w", "e", "r", "t", "caps",
            "a", "s", "d", "f", "g", "shift", "z", "x", "c", "v", "b", "ctrl", "grave", "space"
        };
        public static readonly string[] WasdKeys = new string[] { "w", "a", "s", "d", "q", "e", "shift", "ctrl", "space" };
        public static readonly string[] ArrowKeys = new string[] { "up", "left", "down", "right", "shift", "ctrl", "space" };
        public Color OutlineColor = Color.FromArgb(247, 247, 247);
        public Color PressedColor = Color.FromArgb(0, 210, 235);
        public int OpacityPercent = 100;
        public int ScalePercent = 100;
        public string Layout = "Classic";
        public bool ShowMouse = true;
        public HashSet<string> VisibleKeys = new HashSet<string>(ClassicKeys, StringComparer.OrdinalIgnoreCase);

        public static OverlaySettings Load()
        {
            OverlaySettings value = new OverlaySettings();
            string path = SettingsPath();
            if (!File.Exists(path)) return value;
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] pair = line.Split(new char[] { '=' }, 2);
                    if (pair.Length != 2) continue;
                    int parsed;
                    if (pair[0] == "outline") value.OutlineColor = ColorTranslator.FromHtml(pair[1]);
                    else if (pair[0] == "pressed") value.PressedColor = ColorTranslator.FromHtml(pair[1]);
                    else if (pair[0] == "opacity" && int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) value.OpacityPercent = Clamp(parsed, 35, 100);
                    else if (pair[0] == "scale" && int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) value.ScalePercent = Clamp(parsed, 70, 150);
                    else if (pair[0] == "layout" && (pair[1] == "Classic" || pair[1] == "WASD" || pair[1] == "Arrows")) value.Layout = pair[1];
                    else if (pair[0] == "mouse") value.ShowMouse = pair[1] != "false";
                    else if (pair[0] == "keys")
                    {
                        value.VisibleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string key in pair[1].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) value.VisibleKeys.Add(key);
                    }
                }
            }
            catch { return new OverlaySettings(); }
            return value;
        }

        public void Save()
        {
            string path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new string[] {
                "outline=" + ColorTranslator.ToHtml(OutlineColor),
                "pressed=" + ColorTranslator.ToHtml(PressedColor),
                "opacity=" + OpacityPercent.ToString(CultureInfo.InvariantCulture),
                "scale=" + ScalePercent.ToString(CultureInfo.InvariantCulture),
                "layout=" + Layout,
                "mouse=" + (ShowMouse ? "true" : "false"),
                "keys=" + String.Join(",", VisibleKeys)
            });
        }

        private static string SettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyboardOverlay", "settings.ini");
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InputOverlay());
        }
    }

    internal sealed class InputOverlay : Form
    {
        private readonly Color ink = Color.FromArgb(0, 0, 0);
        private readonly Color transparent = Color.FromArgb(255, 0, 255);
        private readonly HashSet<int> held = new HashSet<int>();
        private readonly Timer wheelRelease = new Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private OverlaySettings preferences;
        private Color outlineColor;
        private Color pressedColor;
        private KeyboardHookProc keyboardProc;
        private MouseHookProc mouseProc;
        private IntPtr keyboardHook;
        private IntPtr mouseHook;
        private bool hookError;
        private int wheelDirection;

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;
        private const int VK_F11 = 0x7A;
        private const int VK_F12 = 0x7B;
        private const int VK_CONTROL = 0x11;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_SHIFT = 0x10;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public UIntPtr dwExtraInfo; }
        private delegate IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, KeyboardHookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, MouseHookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        public InputOverlay()
        {
            preferences = OverlaySettings.Load();
            outlineColor = preferences.OutlineColor;
            pressedColor = preferences.PressedColor;
            Text = "Keyboard Overlay";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            TopMost = true;
            ClientSize = OverlaySize();
            DoubleBuffered = true;
            Opacity = preferences.OpacityPercent / 100.0;
            BackColor = transparent;
            TransparencyKey = transparent;

            wheelRelease.Interval = 180;
            wheelRelease.Tick += delegate { wheelRelease.Stop(); wheelDirection = 0; Invalidate(); };

            ToolStripMenuItem showHide = new ToolStripMenuItem("Hide / show overlay");
            showHide.Click += delegate { Visible = !Visible; };
            ToolStripMenuItem settings = new ToolStripMenuItem("Settings...");
            settings.Click += delegate { OpenSettings(); };
            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { Close(); };
            trayMenu.Items.Add(showHide);
            trayMenu.Items.Add(settings);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(exit);
            this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray.Icon = this.Icon;
            tray.Text = "Keyboard Overlay";
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
            tray.DoubleClick += delegate { Visible = !Visible; };

            Shown += delegate
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(area.Left + 8, area.Bottom - Height - 8);
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                keyboardProc = KeyboardCallback;
                mouseProc = MouseCallback;
                IntPtr module = GetModuleHandle(null);
                keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
                mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
                hookError = keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero;
                tray.Text = hookError ? "Keyboard Overlay · input hook failed" : "Keyboard Overlay";
                Invalidate();
            };

            FormClosed += delegate
            {
                if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
                if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
                wheelRelease.Dispose();
                tray.Visible = false;
                tray.Dispose();
                trayMenu.Dispose();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00040000 | 0x00080000 | 0x00000020; // app window, layered, click-through
                return cp;
            }
        }

        private Size OverlaySize()
        {
            int width = preferences.ShowMouse ? 348 : 240;
            return new Size(width * preferences.ScalePercent / 100, 180 * preferences.ScalePercent / 100);
        }

        private void OpenSettings()
        {
            using (SettingsDialog dialog = new SettingsDialog(preferences))
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                preferences = dialog.Result;
            }

            outlineColor = preferences.OutlineColor;
            pressedColor = preferences.PressedColor;
            Opacity = preferences.OpacityPercent / 100.0;
            ClientSize = OverlaySize();
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Left + 8, area.Bottom - Height - 8);
            try { preferences.Save(); }
            catch (Exception error)
            {
                MessageBox.Show("The overlay changed, but its settings could not be saved.\n\n" + error.Message,
                    "Keyboard Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Invalidate();
        }

        private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int message = wParam.ToInt32();
                KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                int vk = (int)data.vkCode;
                if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
                {
                    bool freshPress = held.Add(vk);
                    if (freshPress)
                    {
                        if (vk == VK_F11 && CtrlDown() && ShiftDown()) Visible = !Visible;
                        else if (vk == VK_F12 && CtrlDown() && ShiftDown()) BeginInvoke(new MethodInvoker(delegate { Close(); }));
                        Invalidate();
                    }
                }
                else if ((message == WM_KEYUP || message == WM_SYSKEYUP) && held.Remove(vk)) Invalidate();
            }
            return CallNextHookEx(keyboardHook, code, wParam, lParam);
        }

        private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int message = wParam.ToInt32();
                if (message != WM_LBUTTONDOWN && message != WM_LBUTTONUP &&
                    message != WM_RBUTTONDOWN && message != WM_RBUTTONUP &&
                    message != WM_MBUTTONDOWN && message != WM_MBUTTONUP &&
                    message != WM_XBUTTONDOWN && message != WM_XBUTTONUP &&
                    message != WM_MOUSEWHEEL)
                    return CallNextHookEx(mouseHook, code, wParam, lParam);

                MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                int mouseCode = 0;
                bool pressed = false;
                if (message == WM_LBUTTONDOWN || message == WM_LBUTTONUP) { mouseCode = -1; pressed = message == WM_LBUTTONDOWN; }
                else if (message == WM_RBUTTONDOWN || message == WM_RBUTTONUP) { mouseCode = -2; pressed = message == WM_RBUTTONDOWN; }
                else if (message == WM_MBUTTONDOWN || message == WM_MBUTTONUP) { mouseCode = -3; pressed = message == WM_MBUTTONDOWN; }
                else if (message == WM_XBUTTONDOWN || message == WM_XBUTTONUP)
                {
                    mouseCode = (data.mouseData >> 16) == 1 ? -4 : -5;
                    pressed = message == WM_XBUTTONDOWN;
                }
                if (mouseCode != 0)
                {
                    bool changed = pressed ? held.Add(mouseCode) : held.Remove(mouseCode);
                    if (changed) Invalidate();
                }
                else if (message == WM_MOUSEWHEEL)
                {
                    wheelDirection = unchecked((short)(data.mouseData >> 16)) >= 0 ? 1 : -1;
                    wheelRelease.Stop();
                    wheelRelease.Start();
                    Invalidate();
                }
            }
            return CallNextHookEx(mouseHook, code, wParam, lParam);
        }

        private bool CtrlDown() { return held.Contains(VK_CONTROL) || held.Contains(VK_LCONTROL) || held.Contains(VK_RCONTROL); }
        private bool ShiftDown() { return held.Contains(VK_SHIFT) || held.Contains(VK_LSHIFT) || held.Contains(VK_RSHIFT); }
        private bool IsDown(Keys key) { return held.Contains((int)key); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.ScaleTransform(preferences.ScalePercent / 100F, preferences.ScalePercent / 100F);
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

            if (preferences.Layout == "WASD") DrawWasdLayout(g);
            else if (preferences.Layout == "Arrows") DrawArrowLayout(g);
            else DrawClassicLayout(g);

            if (preferences.ShowMouse) DrawMouse(g);
            if (hookError) DrawHookError(g);
        }

        private void DrawClassicLayout(Graphics g)
        {
            DrawOptionalKey(g, "esc", "Esc", 10, 10, 27, 27, IsDown(Keys.Escape), 8F);
            DrawOptionalKey(g, "1", "1", 41, 10, 27, 27, IsDown(Keys.D1), 9F);
            DrawOptionalKey(g, "2", "2", 72, 10, 27, 27, IsDown(Keys.D2), 9F);
            DrawOptionalKey(g, "3", "3", 103, 10, 27, 27, IsDown(Keys.D3), 9F);
            DrawOptionalKey(g, "4", "4", 134, 10, 27, 27, IsDown(Keys.D4), 9F);
            DrawOptionalKey(g, "5", "5", 165, 10, 27, 27, IsDown(Keys.D5), 9F);
            DrawOptionalKey(g, "tab", "Tab", 10, 42, 42, 27, IsDown(Keys.Tab), 8F);
            DrawOptionalKey(g, "q", "Q", 56, 42, 27, 27, IsDown(Keys.Q), 9F);
            DrawOptionalKey(g, "w", "W", 87, 42, 27, 27, IsDown(Keys.W), 9F);
            DrawOptionalKey(g, "e", "E", 118, 42, 27, 27, IsDown(Keys.E), 9F);
            DrawOptionalKey(g, "r", "R", 149, 42, 27, 27, IsDown(Keys.R), 9F);
            DrawOptionalKey(g, "t", "T", 180, 42, 27, 27, IsDown(Keys.T), 9F);
            DrawOptionalKey(g, "caps", "Caps", 10, 74, 50, 27, IsDown(Keys.CapsLock), 7F);
            DrawOptionalKey(g, "a", "A", 64, 74, 27, 27, IsDown(Keys.A), 9F);
            DrawOptionalKey(g, "s", "S", 95, 74, 27, 27, IsDown(Keys.S), 9F);
            DrawOptionalKey(g, "d", "D", 126, 74, 27, 27, IsDown(Keys.D), 9F);
            DrawOptionalKey(g, "f", "F", 157, 74, 27, 27, IsDown(Keys.F), 9F);
            DrawOptionalKey(g, "g", "G", 188, 74, 27, 27, IsDown(Keys.G), 9F);
            DrawOptionalKey(g, "shift", "Shift", 10, 106, 64, 27, ShiftDown(), 7F);
            DrawOptionalKey(g, "z", "Z", 78, 106, 27, 27, IsDown(Keys.Z), 9F);
            DrawOptionalKey(g, "x", "X", 109, 106, 27, 27, IsDown(Keys.X), 9F);
            DrawOptionalKey(g, "c", "C", 140, 106, 27, 27, IsDown(Keys.C), 9F);
            DrawOptionalKey(g, "v", "V", 171, 106, 27, 27, IsDown(Keys.V), 9F);
            DrawOptionalKey(g, "b", "B", 202, 106, 27, 27, IsDown(Keys.B), 9F);
            DrawOptionalKey(g, "ctrl", "Ctrl", 10, 138, 52, 27, CtrlDown(), 7F);
            DrawOptionalKey(g, "grave", "`", 66, 138, 34, 27, IsDown(Keys.Oemtilde), 9F);
            DrawOptionalKey(g, "space", "Space", 104, 138, 125, 27, IsDown(Keys.Space), 8F);
        }

        private void DrawWasdLayout(Graphics g)
        {
            DrawOptionalKey(g, "w", "W", 73, 10, 42, 34, IsDown(Keys.W), 12F);
            DrawOptionalKey(g, "q", "Q", 27, 48, 42, 34, IsDown(Keys.Q), 12F);
            DrawOptionalKey(g, "a", "A", 73, 48, 42, 34, IsDown(Keys.A), 12F);
            DrawOptionalKey(g, "s", "S", 73, 86, 42, 34, IsDown(Keys.S), 12F);
            DrawOptionalKey(g, "d", "D", 119, 48, 42, 34, IsDown(Keys.D), 12F);
            DrawOptionalKey(g, "e", "E", 165, 48, 42, 34, IsDown(Keys.E), 12F);
            DrawOptionalKey(g, "shift", "Shift", 27, 124, 58, 34, ShiftDown(), 8F);
            DrawOptionalKey(g, "ctrl", "Ctrl", 89, 124, 52, 34, CtrlDown(), 8F);
            DrawOptionalKey(g, "space", "Space", 145, 124, 92, 34, IsDown(Keys.Space), 8F);
        }

        private void DrawArrowLayout(Graphics g)
        {
            DrawOptionalKey(g, "up", "↑", 91, 24, 42, 34, IsDown(Keys.Up), 15F);
            DrawOptionalKey(g, "left", "←", 45, 62, 42, 34, IsDown(Keys.Left), 15F);
            DrawOptionalKey(g, "down", "↓", 91, 62, 42, 34, IsDown(Keys.Down), 15F);
            DrawOptionalKey(g, "right", "→", 137, 62, 42, 34, IsDown(Keys.Right), 15F);
            DrawOptionalKey(g, "shift", "Shift", 27, 112, 58, 34, ShiftDown(), 8F);
            DrawOptionalKey(g, "ctrl", "Ctrl", 89, 112, 52, 34, CtrlDown(), 8F);
            DrawOptionalKey(g, "space", "Space", 145, 112, 92, 34, IsDown(Keys.Space), 8F);
        }

        private void DrawOptionalKey(Graphics g, string id, string label, int x, int y, int width, int height, bool pressed, float fontSize)
        {
            if (preferences.VisibleKeys.Contains(id)) DrawKey(g, label, x, y, width, height, pressed, fontSize);
        }

        private void DrawKey(Graphics g, string label, int x, int y, int width, int height, bool pressed, float fontSize)
        {
            Rectangle rect = new Rectangle(x, y, width, height);
            using (Pen p = new Pen(outlineColor, 1.35F)) using (GraphicsPath path = RoundRect(rect, 4)) g.DrawPath(p, path);
            if (pressed)
            {
                using (SolidBrush b = new SolidBrush(pressedColor)) using (GraphicsPath path = RoundRect(new Rectangle(x + 1, y + 1, width - 2, height - 2), 3)) g.FillPath(b, path);
            }
            Color fg = pressed ? TextOn(pressedColor) : outlineColor;
            using (Font f = new Font("Segoe UI", fontSize, FontStyle.Regular)) using (SolidBrush b = new SolidBrush(fg))
            {
                SizeF size = g.MeasureString(label, f);
                g.DrawString(label, f, b, x + (width - size.Width) / 2F, y + (height - size.Height) / 2F - 1F);
            }
        }

        private void DrawMouse(Graphics g)
        {
            Rectangle body = new Rectangle(258, 22, 78, 126);
            using (GraphicsPath shell = RoundRect(body, 37))
            {
                GraphicsState state = g.Save();
                g.SetClip(shell);
                if (held.Contains(-1)) using (SolidBrush b = new SolidBrush(pressedColor)) g.FillRectangle(b, 259, 23, 38, 45);
                if (held.Contains(-2)) using (SolidBrush b = new SolidBrush(pressedColor)) g.FillRectangle(b, 297, 23, 38, 45);
                g.Restore(state);
                using (Pen p = new Pen(outlineColor, 1.6F)) g.DrawPath(p, shell);
            }
            using (Pen p = new Pen(outlineColor, 1.35F))
            {
                g.DrawLine(p, 297, 25, 297, 66);
                g.DrawLine(p, 260, 68, 334, 68);
            }
            if (held.Contains(-3) || wheelDirection != 0)
            {
                using (SolidBrush b = new SolidBrush(pressedColor)) g.FillEllipse(b, 293, 36, 8, 18);
            }
            else
            {
                using (Pen p = new Pen(outlineColor, 1.4F)) g.DrawEllipse(p, 293, 36, 8, 18);
            }
            DrawSideButton(g, 252, 86, held.Contains(-4));
            DrawSideButton(g, 252, 106, held.Contains(-5));
        }

        private void DrawSideButton(Graphics g, int x, int y, bool pressed)
        {
            Rectangle r = new Rectangle(x, y, 7, 13);
            using (Pen p = new Pen(outlineColor, 1.1F)) g.DrawRectangle(p, r);
            if (pressed) using (SolidBrush b = new SolidBrush(pressedColor)) g.FillRectangle(b, x + 1, y + 1, 5, 11);
        }

        private void DrawHookError(Graphics g)
        {
            using (Font f = new Font("Segoe UI", 7F)) using (SolidBrush b = new SolidBrush(outlineColor)) g.DrawString("INPUT HOOK FAILED", f, b, 10, 164);
        }

        private Color TextOn(Color background)
        {
            double brightness = (background.R * 299 + background.G * 587 + background.B * 114) / 1000.0;
            return brightness > 145 ? ink : Color.White;
        }

        private GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class KeyChoice
    {
        public string Id { get; private set; }
        private readonly string label;

        public KeyChoice(string id, string name)
        {
            Id = id;
            label = name;
        }

        public override string ToString() { return label; }
    }

    internal sealed class SettingsDialog : Form
    {
        private Color outlineColor;
        private Color pressedColor;
        private readonly TrackBar opacityTrack = new TrackBar();
        private readonly TrackBar scaleTrack = new TrackBar();
        private readonly ComboBox layoutPicker = new ComboBox();
        private readonly CheckedListBox keyPicker = new CheckedListBox();
        private readonly CheckBox mouseToggle = new CheckBox();
        private readonly ComboBox themePicker = new ComboBox();
        private bool suppressLayoutChange;
        private readonly Label opacityValue = new Label();
        private readonly Label scaleValue = new Label();
        private readonly Button outlineButton = new Button();
        private readonly Button pressedButton = new Button();
        private readonly Panel outlineSwatch = new Panel();
        private readonly Panel pressedSwatch = new Panel();
        private bool suppressThemeChange;

        public OverlaySettings Result { get; private set; }

        public SettingsDialog(OverlaySettings current)
        {
            outlineColor = current.OutlineColor;
            pressedColor = current.PressedColor;
            Text = "Keyboard Overlay Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(500, 646);
            BackColor = SystemColors.Control;
            ForeColor = SystemColors.ControlText;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular);
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            Controls.Add(MakeLabel("Keyboard Overlay Settings", 16, 14, 450, 26, 14F, true));
            Controls.Add(MakeLabel("Choose what viewers see. Changes apply when you click Save.", 17, 40, 460, 20, 9F, false));

            GroupBox layoutGroup = new GroupBox();
            layoutGroup.Text = "Layout and keys";
            layoutGroup.SetBounds(14, 68, 472, 300);
            Controls.Add(layoutGroup);
            layoutGroup.Controls.Add(MakeLabel("Layout", 14, 25, 70, 22, 9F, false));
            layoutPicker.DropDownStyle = ComboBoxStyle.DropDownList;
            layoutPicker.Items.AddRange(new object[] { "Classic", "WASD focus", "Arrow keys" });
            layoutPicker.SetBounds(90, 21, 360, 27);
            layoutPicker.SelectedIndex = current.Layout == "WASD" ? 1 : (current.Layout == "Arrows" ? 2 : 0);
            layoutGroup.Controls.Add(layoutPicker);
            layoutGroup.Controls.Add(MakeLabel("Check the keys to display", 14, 58, 300, 21, 9F, true));
            keyPicker.CheckOnClick = true;
            keyPicker.MultiColumn = true;
            keyPicker.ColumnWidth = 145;
            keyPicker.SetBounds(14, 81, 436, 158);
            layoutGroup.Controls.Add(keyPicker);
            Button selectAll = MakeButton("Select all", 14, 246, 95);
            selectAll.Click += delegate { for (int i = 0; i < keyPicker.Items.Count; i++) keyPicker.SetItemChecked(i, true); };
            layoutGroup.Controls.Add(selectAll);
            Button clearKeys = MakeButton("Clear selection", 117, 246, 112);
            clearKeys.Click += delegate { for (int i = 0; i < keyPicker.Items.Count; i++) keyPicker.SetItemChecked(i, false); };
            layoutGroup.Controls.Add(clearKeys);
            mouseToggle.Text = "Show mouse buttons and scroll wheel";
            mouseToggle.Checked = current.ShowMouse;
            mouseToggle.SetBounds(14, 282, 300, 24);
            layoutGroup.Controls.Add(mouseToggle);
            layoutPicker.SelectedIndexChanged += delegate { if (!suppressLayoutChange) ApplyLayoutPreset(); };
            PopulateKeys(current.VisibleKeys);

            GroupBox colorGroup = new GroupBox();
            colorGroup.Text = "Colors";
            colorGroup.SetBounds(14, 376, 472, 124);
            Controls.Add(colorGroup);
            colorGroup.Controls.Add(MakeLabel("Color theme", 14, 24, 100, 22, 9F, false));
            themePicker.DropDownStyle = ComboBoxStyle.DropDownList;
            themePicker.Items.AddRange(new object[] { "Ice (cyan)", "Red", "Lime", "Violet", "Custom colors" });
            themePicker.SetBounds(116, 20, 334, 27);
            colorGroup.Controls.Add(themePicker);
            colorGroup.Controls.Add(MakeLabel("Key outline", 14, 62, 100, 22, 9F, false));
            outlineSwatch.BorderStyle = BorderStyle.FixedSingle;
            outlineSwatch.SetBounds(118, 61, 30, 20);
            colorGroup.Controls.Add(outlineSwatch);
            outlineButton.Text = "Choose...";
            outlineButton.SetBounds(154, 58, 92, 27);
            outlineButton.Click += delegate { PickColor(true); };
            colorGroup.Controls.Add(outlineButton);
            colorGroup.Controls.Add(MakeLabel("Pressed key", 260, 62, 82, 22, 9F, false));
            pressedSwatch.BorderStyle = BorderStyle.FixedSingle;
            pressedSwatch.SetBounds(348, 61, 30, 20);
            colorGroup.Controls.Add(pressedSwatch);
            pressedButton.Text = "Choose...";
            pressedButton.SetBounds(384, 58, 68, 27);
            pressedButton.Click += delegate { PickColor(false); };
            colorGroup.Controls.Add(pressedButton);
            themePicker.SelectedIndex = FindTheme(outlineColor, pressedColor);
            themePicker.SelectedIndexChanged += delegate { if (!suppressThemeChange) ApplyTheme(); };
            UpdateColorSwatches();

            GroupBox overlayGroup = new GroupBox();
            overlayGroup.Text = "Overlay";
            overlayGroup.SetBounds(14, 508, 472, 82);
            Controls.Add(overlayGroup);
            overlayGroup.Controls.Add(MakeLabel("Opacity", 14, 21, 65, 22, 9F, false));
            ConfigureValueLabel(opacityValue, 390, 21);
            opacityTrack.Minimum = 35;
            opacityTrack.Maximum = 100;
            opacityTrack.TickFrequency = 13;
            opacityTrack.SmallChange = 1;
            opacityTrack.LargeChange = 5;
            opacityTrack.Value = current.OpacityPercent;
            opacityTrack.SetBounds(80, 15, 322, 36);
            opacityTrack.ValueChanged += delegate { UpdateSliderLabels(); };
            overlayGroup.Controls.Add(opacityTrack);
            overlayGroup.Controls.Add(MakeLabel("Size", 14, 52, 65, 22, 9F, false));
            ConfigureValueLabel(scaleValue, 390, 52);
            scaleTrack.Minimum = 70;
            scaleTrack.Maximum = 150;
            scaleTrack.TickFrequency = 10;
            scaleTrack.SmallChange = 5;
            scaleTrack.LargeChange = 10;
            scaleTrack.Value = current.ScalePercent;
            scaleTrack.SetBounds(80, 46, 322, 36);
            scaleTrack.ValueChanged += delegate { UpdateSliderLabels(); };
            overlayGroup.Controls.Add(scaleTrack);
            UpdateSliderLabels();

            Button defaults = MakeButton("Restore defaults", 14, 604, 130);
            defaults.Click += delegate { ResetDefaults(); };
            Controls.Add(defaults);
            Button cancel = MakeButton("Cancel", 324, 604, 74);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            Button save = MakeButton("Save", 406, 604, 80);
            save.Click += delegate
            {
                Result = new OverlaySettings();
                Result.OutlineColor = outlineColor;
                Result.PressedColor = pressedColor;
                Result.OpacityPercent = opacityTrack.Value;
                Result.ScalePercent = scaleTrack.Value;
                Result.Layout = layoutPicker.SelectedIndex == 1 ? "WASD" : (layoutPicker.SelectedIndex == 2 ? "Arrows" : "Classic");
                Result.ShowMouse = mouseToggle.Checked;
                Result.VisibleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object checkedKey in keyPicker.CheckedItems) Result.VisibleKeys.Add(((KeyChoice)checkedKey).Id);
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(save);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private int FindTheme(Color outline, Color pressed)
        {
            if (outline.ToArgb() == Color.FromArgb(247, 247, 247).ToArgb() && pressed.ToArgb() == Color.FromArgb(0, 210, 235).ToArgb()) return 0;
            if (outline.ToArgb() == Color.White.ToArgb() && pressed.ToArgb() == Color.FromArgb(255, 82, 105).ToArgb()) return 1;
            if (outline.ToArgb() == Color.FromArgb(245, 255, 242).ToArgb() && pressed.ToArgb() == Color.FromArgb(124, 220, 90).ToArgb()) return 2;
            if (outline.ToArgb() == Color.FromArgb(248, 241, 255).ToArgb() && pressed.ToArgb() == Color.FromArgb(177, 126, 255).ToArgb()) return 3;
            return 4;
        }

        private void ApplyTheme()
        {
            if (themePicker.SelectedIndex == 0) { outlineColor = Color.FromArgb(247, 247, 247); pressedColor = Color.FromArgb(0, 210, 235); }
            else if (themePicker.SelectedIndex == 1) { outlineColor = Color.White; pressedColor = Color.FromArgb(255, 82, 105); }
            else if (themePicker.SelectedIndex == 2) { outlineColor = Color.FromArgb(245, 255, 242); pressedColor = Color.FromArgb(124, 220, 90); }
            else if (themePicker.SelectedIndex == 3) { outlineColor = Color.FromArgb(248, 241, 255); pressedColor = Color.FromArgb(177, 126, 255); }
            else return;
            UpdateColorSwatches();
        }

        private void UpdateColorSwatches()
        {
            outlineSwatch.BackColor = outlineColor;
            pressedSwatch.BackColor = pressedColor;
        }

        private void UpdateSliderLabels()
        {
            opacityValue.Text = opacityTrack.Value.ToString() + "%";
            scaleValue.Text = scaleTrack.Value.ToString() + "%";
        }

        private string[] KeysForSelectedLayout()
        {
            if (layoutPicker.SelectedIndex == 1) return OverlaySettings.WasdKeys;
            if (layoutPicker.SelectedIndex == 2) return OverlaySettings.ArrowKeys;
            return OverlaySettings.ClassicKeys;
        }

        private void PopulateKeys(HashSet<string> selected)
        {
            keyPicker.Items.Clear();
            foreach (string id in KeysForSelectedLayout())
            {
                KeyChoice choice = new KeyChoice(id, id == "grave" ? "`" : id.ToUpperInvariant());
                int index = keyPicker.Items.Add(choice);
                keyPicker.SetItemChecked(index, selected.Contains(id));
            }
        }

        private void ApplyLayoutPreset()
        {
            string[] keys = KeysForSelectedLayout();
            PopulateKeys(new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase));
        }

        private Label MakeLabel(string caption, int x, int y, int width, int height, float size, bool bold)
        {
            Label label = new Label();
            label.Text = caption;
            label.SetBounds(x, y, width, height);
            label.Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            return label;
        }

        private void ConfigureValueLabel(Label label, int x, int y)
        {
            label.SetBounds(x, y, 60, 22);
            label.TextAlign = ContentAlignment.MiddleRight;
        }

        private Button MakeButton(string caption, int x, int y, int width)
        {
            Button button = new Button();
            button.Text = caption;
            button.SetBounds(x, y, width, 28);
            button.FlatStyle = FlatStyle.Standard;
            return button;
        }

        private void PickColor(bool outline)
        {
            using (ColorDialog picker = new ColorDialog())
            {
                picker.AnyColor = true;
                picker.FullOpen = true;
                picker.Color = outline ? outlineColor : pressedColor;
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                if (picker.Color.ToArgb() == Color.FromArgb(255, 0, 255).ToArgb())
                {
                    MessageBox.Show(this, "That color is reserved for the transparent background. Choose another color.",
                        "Choose a different color", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (outline)
                {
                    outlineColor = picker.Color;
                }
                else
                {
                    pressedColor = picker.Color;
                }
                suppressThemeChange = true;
                themePicker.SelectedIndex = 4;
                suppressThemeChange = false;
                UpdateColorSwatches();
            }
        }

        private void ResetDefaults()
        {
            suppressLayoutChange = true;
            layoutPicker.SelectedIndex = 0;
            suppressLayoutChange = false;
            PopulateKeys(new HashSet<string>(OverlaySettings.ClassicKeys, StringComparer.OrdinalIgnoreCase));
            mouseToggle.Checked = true;
            outlineColor = Color.FromArgb(247, 247, 247);
            pressedColor = Color.FromArgb(0, 210, 235);
            suppressThemeChange = true;
            themePicker.SelectedIndex = 0;
            suppressThemeChange = false;
            UpdateColorSwatches();
            opacityTrack.Value = 100;
            scaleTrack.Value = 100;
            UpdateSliderLabels();
        }
    }
}
