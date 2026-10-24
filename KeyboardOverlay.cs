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
        private readonly Color surface = Color.FromArgb(20, 23, 29);
        private readonly Color textColor = Color.FromArgb(239, 242, 247);
        private readonly Color muted = Color.FromArgb(166, 175, 188);
        private Color outlineColor;
        private Color pressedColor;
        private readonly TrackBar opacityTrack = new TrackBar();
        private readonly TrackBar scaleTrack = new TrackBar();
        private readonly ComboBox layoutPicker = new ComboBox();
        private readonly CheckedListBox keyPicker = new CheckedListBox();
        private readonly CheckBox mouseToggle = new CheckBox();
        private readonly TabControl pages = new TabControl();
        private readonly TabPage layoutPage = new TabPage("Layout & keys");
        private readonly TabPage appearancePage = new TabPage("Appearance");
        private bool suppressLayoutChange;
        private readonly Label opacityValue = new Label();
        private readonly Label scaleValue = new Label();
        private readonly Button outlineButton = new Button();
        private readonly Button pressedButton = new Button();

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
            ClientSize = new Size(500, 548);
            BackColor = surface;
            ForeColor = textColor;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular);
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            Controls.Add(MakeLabel("Keyboard Overlay", 18, 14, 360, 28, 16F, true, textColor));
            Controls.Add(MakeLabel("Personalize what appears in your recording.", 18, 43, 420, 20, 9F, false, muted));
            pages.SetBounds(14, 72, 472, 406);
            pages.Controls.Add(layoutPage);
            pages.Controls.Add(appearancePage);
            layoutPage.UseVisualStyleBackColor = false;
            appearancePage.UseVisualStyleBackColor = false;
            Controls.Add(pages);

            layoutPage.BackColor = surface;
            layoutPage.Controls.Add(MakeLabel("Choose a starting layout", 14, 12, 240, 22, 10F, true, textColor));
            layoutPicker.DropDownStyle = ComboBoxStyle.DropDownList;
            layoutPicker.Items.AddRange(new object[] { "Classic", "WASD focus", "Arrow keys" });
            layoutPicker.SetBounds(14, 37, 420, 29);
            layoutPicker.SelectedIndex = current.Layout == "WASD" ? 1 : (current.Layout == "Arrows" ? 2 : 0);
            layoutPage.Controls.Add(layoutPicker);
            layoutPage.Controls.Add(MakeLabel("Keys to show", 14, 78, 200, 22, 10F, true, textColor));
            keyPicker.CheckOnClick = true;
            keyPicker.MultiColumn = true;
            keyPicker.ColumnWidth = 205;
            keyPicker.BorderStyle = BorderStyle.FixedSingle;
            keyPicker.BackColor = Color.FromArgb(27, 31, 39);
            keyPicker.ForeColor = textColor;
            keyPicker.SetBounds(14, 103, 420, 190);
            layoutPage.Controls.Add(keyPicker);
            Button selectAll = MakeButton("Select all", 14, 302, 100, false);
            selectAll.Click += delegate { for (int i = 0; i < keyPicker.Items.Count; i++) keyPicker.SetItemChecked(i, true); };
            layoutPage.Controls.Add(selectAll);
            Button clearKeys = MakeButton("Clear", 122, 302, 80, false);
            clearKeys.Click += delegate { for (int i = 0; i < keyPicker.Items.Count; i++) keyPicker.SetItemChecked(i, false); };
            layoutPage.Controls.Add(clearKeys);
            mouseToggle.Text = "Show mouse buttons";
            mouseToggle.Checked = current.ShowMouse;
            mouseToggle.SetBounds(14, 348, 260, 25);
            mouseToggle.ForeColor = textColor;
            layoutPage.Controls.Add(mouseToggle);
            layoutPicker.SelectedIndexChanged += delegate { if (!suppressLayoutChange) ApplyLayoutPreset(); };
            PopulateKeys(current.VisibleKeys);

            appearancePage.BackColor = surface;
            appearancePage.Controls.Add(MakeLabel("Choose a color theme", 14, 12, 220, 22, 10F, true, textColor));
            appearancePage.Controls.Add(MakeLabel("Or customize either color below.", 14, 34, 400, 20, 9F, false, muted));
            AddTheme("Ice", 14, 62, Color.FromArgb(247, 247, 247), Color.FromArgb(0, 210, 235));
            AddTheme("Red", 224, 62, Color.White, Color.FromArgb(255, 82, 105));
            AddTheme("Lime", 14, 105, Color.FromArgb(245, 255, 242), Color.FromArgb(124, 220, 90));
            AddTheme("Violet", 224, 105, Color.FromArgb(248, 241, 255), Color.FromArgb(177, 126, 255));
            appearancePage.Controls.Add(MakeLabel("Key outline", 14, 162, 140, 22, 9F, false, textColor));
            ConfigureColorButton(outlineButton, 280, 155, outlineColor);
            outlineButton.Text = "Choose color";
            outlineButton.Click += delegate { PickColor(true); };
            appearancePage.Controls.Add(outlineButton);
            appearancePage.Controls.Add(MakeLabel("Pressed key", 14, 201, 140, 22, 9F, false, textColor));
            ConfigureColorButton(pressedButton, 280, 194, pressedColor);
            pressedButton.Text = "Choose color";
            pressedButton.Click += delegate { PickColor(false); };
            appearancePage.Controls.Add(pressedButton);
            appearancePage.Controls.Add(MakeRule(235));
            appearancePage.Controls.Add(MakeLabel("Opacity", 14, 248, 180, 22, 10F, true, textColor));
            ConfigureValueLabel(opacityValue, 374, 248);
            opacityTrack.Minimum = 35;
            opacityTrack.Maximum = 100;
            opacityTrack.TickFrequency = 10;
            opacityTrack.SmallChange = 1;
            opacityTrack.LargeChange = 5;
            opacityTrack.Value = current.OpacityPercent;
            opacityTrack.SetBounds(8, 267, 426, 42);
            opacityTrack.ValueChanged += delegate { UpdateSliderLabels(); };
            appearancePage.Controls.Add(opacityTrack);
            appearancePage.Controls.Add(MakeLabel("Size", 14, 314, 180, 22, 10F, true, textColor));
            ConfigureValueLabel(scaleValue, 374, 314);
            scaleTrack.Minimum = 70;
            scaleTrack.Maximum = 150;
            scaleTrack.TickFrequency = 10;
            scaleTrack.SmallChange = 5;
            scaleTrack.LargeChange = 10;
            scaleTrack.Value = current.ScalePercent;
            scaleTrack.SetBounds(8, 333, 426, 38);
            scaleTrack.ValueChanged += delegate { UpdateSliderLabels(); };
            appearancePage.Controls.Add(scaleTrack);
            UpdateSliderLabels();

            Controls.Add(MakeRule(486));
            Button defaults = MakeButton("Reset", 18, 500, 90, false);
            defaults.Click += delegate { ResetDefaults(); };
            Controls.Add(defaults);
            Button cancel = MakeButton("Cancel", 314, 500, 76, false);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            Button save = MakeButton("Save settings", 398, 500, 86, true);
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

        private void AddTheme(string name, int x, int y, Color outline, Color pressed)
        {
            Button theme = MakeButton(name, x, y, 198, true);
            theme.Height = 36;
            theme.BackColor = pressed;
            theme.FlatAppearance.BorderColor = outline;
            theme.ForeColor = pressed.GetBrightness() > 0.56F ? Color.FromArgb(20, 23, 29) : Color.White;
            theme.Click += delegate
            {
                outlineColor = outline;
                pressedColor = pressed;
                SetColorButton(outlineButton, outlineColor);
                SetColorButton(pressedButton, pressedColor);
                outlineButton.Text = "Choose color";
                pressedButton.Text = "Choose color";
            };
            appearancePage.Controls.Add(theme);
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

        private Label MakeLabel(string caption, int x, int y, int width, int height, float size, bool bold, Color color)
        {
            Label label = new Label();
            label.Text = caption;
            label.SetBounds(x, y, width, height);
            label.Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            label.ForeColor = color;
            return label;
        }

        private Panel MakeRule(int y)
        {
            Panel rule = new Panel();
            rule.BackColor = Color.FromArgb(55, 61, 72);
            rule.SetBounds(24, y, 382, 1);
            return rule;
        }

        private void ConfigureValueLabel(Label label, int x, int y)
        {
            label.SetBounds(x, y, 60, 22);
            label.TextAlign = ContentAlignment.MiddleRight;
            label.ForeColor = Color.FromArgb(64, 220, 236);
        }

        private void ConfigureColorButton(Button button, int x, int y, Color color)
        {
            button.SetBounds(x, y, 88, 30);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(74, 83, 97);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(54, 61, 72);
            button.Cursor = Cursors.Hand;
            SetColorButton(button, color);
        }

        private void SetColorButton(Button button, Color color)
        {
            button.BackColor = color;
            button.ForeColor = color.GetBrightness() > 0.56F ? Color.FromArgb(20, 23, 29) : Color.White;
            button.Text = ColorTranslator.ToHtml(color).ToUpperInvariant();
        }

        private Button MakeButton(string caption, int x, int y, int width, bool primary)
        {
            Button button = new Button();
            button.Text = caption;
            button.SetBounds(x, y, width, 32);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(0, 170, 196) : Color.FromArgb(74, 83, 97);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0, 184, 209) : Color.FromArgb(42, 48, 58);
            button.BackColor = primary ? Color.FromArgb(0, 157, 183) : Color.FromArgb(31, 36, 44);
            button.ForeColor = Color.White;
            button.Cursor = Cursors.Hand;
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
                    SetColorButton(outlineButton, outlineColor);
                }
                else
                {
                    pressedColor = picker.Color;
                    SetColorButton(pressedButton, pressedColor);
                }
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
            SetColorButton(outlineButton, outlineColor);
            SetColorButton(pressedButton, pressedColor);
            opacityTrack.Value = 100;
            scaleTrack.Value = 100;
            opacityValue.Text = "100%";
            scaleValue.Text = "100%";
        }
    }
}
