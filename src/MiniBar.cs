// MiniBar.cs - the floating, always-on-top recorder bar, tray icon, menus and global hotkeys.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MiniRecorder
{
    internal sealed class MiniBar : Form
    {
        // ---- look
        private const int W = 520, H = 64, Radius = 14;
        private static readonly Color Bg = Color.FromArgb(0x1C, 0x1D, 0x21);
        private static readonly Color BtnBg = Color.FromArgb(0x2A, 0x2C, 0x31);
        private static readonly Color BtnHover = Color.FromArgb(0x34, 0x36, 0x3C);
        private static readonly Color Ring = Color.FromArgb(0x3A, 0x3C, 0x42);
        private static readonly Color TextMain = Color.FromArgb(0xF1, 0xF3, 0xF5);
        private static readonly Color TextDim = Color.FromArgb(0xCE, 0xD4, 0xDA);
        private static readonly Color TextMuted = Color.FromArgb(0xAD, 0xB5, 0xBD);
        private static readonly Color Red = Color.FromArgb(0xE0, 0x31, 0x31);
        private static readonly Color RedHover = Color.FromArgb(0xF0, 0x3E, 0x3E);
        private static readonly Color RecText = Color.FromArgb(0xFF, 0x87, 0x87);
        private static readonly Color Amber = Color.FromArgb(0xFF, 0xD4, 0x3B);
        private static readonly Color Green = Color.FromArgb(0x51, 0xCF, 0x66);
        private static readonly Color OkText = Color.FromArgb(0x8C, 0xE9, 0x9A);

        // ---- hotkeys (Ctrl+Alt+...)
        private const int HK_RECORD = 1, HK_PAUSE = 2, HK_FOLDER = 3, HK_SHOW = 4;

        private readonly Settings _s;
        private readonly AudioEngine _engine = new AudioEngine();
        private readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ToolTip _tip = new ToolTip();
        private readonly ContextMenuStrip _gearMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _trayMenu = new ContextMenuStrip();
        private readonly List<Thread> _converters = new List<Thread>();
        private Icon _iconIdle, _iconRec;
        private readonly Font _fontTimer, _fontSmall;
        private readonly float _scale;

        private readonly float[] _bars = new float[40];
        private string _deviceName = "System audio";
        private string _status;          // transient message under the timer
        private Color _statusColor;
        private DateTime _statusUntil;
        private string _currentWav;
        private string _lastFile;
        private bool _stopping, _exiting, _settingsOpen;
        private int _converting;
        private string _hover, _pressed, _tipShown;

        // demo / screenshot support (used to preview the UI without recording)
        private readonly string _demo;
        private string _shotPath;
        private readonly Random _rnd = new Random(7);

        private RectangleF _rRec, _rPause, _rFolder, _rGear, _rClose, _rMeter, _rHint;

        public MiniBar(string demo)
        {
            _demo = demo;
            foreach (string a in Environment.GetCommandLineArgs())
                if (a.StartsWith("--shot=")) _shotPath = a.Substring(7);

            _s = Settings.Load();
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) _scale = Math.Max(1f, g.DpiX / 96f);

            _fontTimer = new Font("Segoe UI Semibold", 18f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontSmall = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);

            Text = "Mini Recorder";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Bg;
            ClientSize = new Size((int)(W * _scale), (int)(H * _scale));
            TopMost = _s.AlwaysOnTop;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            PlaceWindow();
            Layout_();
            BuildIcons();
            BuildMenus();

            _tray.Icon = _iconIdle;
            _tray.Text = "Mini Recorder";
            _tray.ContextMenuStrip = _trayMenu;
            _tray.Visible = _demo == null;
            _tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleVisible(); };

            _tip.InitialDelay = 500;
            _engine.Finished += OnEngineFinished;

            _uiTimer.Interval = 50;
            _uiTimer.Tick += OnTick;
            _uiTimer.Start();

            if (_demo == null)
            {
                RefreshDeviceName();
            }
            else
            {
                _deviceName = "Speakers (Realtek(R) Audio)";
                for (int i = 0; i < _bars.Length; i++) _bars[i] = (float)(0.35 + 0.55 * Math.Abs(Math.Sin(i * 0.9) * 0.7 + Math.Sin(i * 2.3) * 0.3));
            }
            if (_shotPath != null)
            {
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 1500;
                t.Tick += delegate { t.Stop(); SaveShot(); };
                t.Start();
            }
        }

        // ------------------------------------------------------------------ window setup
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= Win32.CS_DROPSHADOW;
                cp.ExStyle |= Win32.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRoundedCorners();
            if (_demo == null) RegisterHotkeys();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try
            {
                Win32.UnregisterHotKey(Handle, HK_RECORD);
                Win32.UnregisterHotKey(Handle, HK_PAUSE);
                Win32.UnregisterHotKey(Handle, HK_FOLDER);
                Win32.UnregisterHotKey(Handle, HK_SHOW);
            }
            catch { }
            base.OnHandleDestroyed(e);
        }

        private void ApplyRoundedCorners()
        {
            bool dwmOk = false;
            try
            {
                int pref = Win32.DWMWCP_ROUND; // Windows 11 smooth rounded corners
                dwmOk = Win32.DwmSetWindowAttribute(Handle, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4) == 0;
            }
            catch { }
            if (!dwmOk)
            {
                try
                {
                    int r = (int)(Radius * 2 * _scale);
                    Region = Region.FromHrgn(Win32.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r));
                }
                catch
                {
                    using (GraphicsPath p = RoundRect(new RectangleF(0, 0, Width, Height), Radius * _scale))
                        Region = new Region(p);
                }
            }
        }

        private void RegisterHotkeys()
        {
            List<string> failed = new List<string>();
            int mods = Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT;
            try
            {
                if (!Win32.RegisterHotKey(Handle, HK_RECORD, mods, (int)Keys.R)) failed.Add("Ctrl+Alt+R");
                if (!Win32.RegisterHotKey(Handle, HK_PAUSE, mods, (int)Keys.P)) failed.Add("Ctrl+Alt+P");
                if (!Win32.RegisterHotKey(Handle, HK_FOLDER, mods, (int)Keys.O)) failed.Add("Ctrl+Alt+O");
                if (!Win32.RegisterHotKey(Handle, HK_SHOW, mods, (int)Keys.H)) failed.Add("Ctrl+Alt+H");
            }
            catch { return; }
            if (failed.Count > 0)
                ShowStatus("Shortcut taken by another app: " + string.Join(", ", failed.ToArray()), Amber, 8);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32.WM_HOTKEY)
            {
                switch (m.WParam.ToInt32())
                {
                    case HK_RECORD: ToggleRecord(); break;
                    case HK_PAUSE: TogglePause(); break;
                    case HK_FOLDER: OpenFolder(); break;
                    case HK_SHOW: ToggleVisible(); break;
                }
                return;
            }
            base.WndProc(ref m);
        }

        private void PlaceWindow()
        {
            Point p = new Point(_s.X, _s.Y);
            bool onScreen = false;
            foreach (Screen sc in Screen.AllScreens)
                if (sc.WorkingArea.Contains(new Point(p.X + 20, p.Y + 20))) onScreen = true;
            if (!onScreen)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                p = new Point(wa.Right - Width - 24, wa.Bottom - Height - 24);
            }
            Location = p;
            Move += delegate { if (Visible) { _s.X = Left; _s.Y = Top; } };
        }

        private void Layout_()
        {
            _rRec = new RectangleF(12, 12, 40, 40);
            _rClose = new RectangleF(W - 8 - 36, 14, 36, 36);
            _rGear = new RectangleF(_rClose.X - 38, 14, 36, 36);
            _rFolder = new RectangleF(_rGear.X - 38, 14, 36, 36);
            _rPause = new RectangleF(_rFolder.X - 42, 14, 36, 36);
            _rMeter = new RectangleF(176, 18, _rPause.X - 10 - 176, 28);
            _rHint = new RectangleF(200, 0, _rFolder.X - 10 - 200, H);
        }

        // ------------------------------------------------------------------ state
        private bool Recording { get { return _demo != null ? _demo != "--demo-idle" : _engine.IsRecording; } }
        private bool Paused { get { return _demo != null ? _demo == "--demo-paused" : _engine.IsPaused; } }

        private void ToggleRecord()
        {
            if (_demo != null) return;
            if (_stopping) return;
            if (_engine.IsRecording) StopRecording(); else StartRecording();
        }

        private void StartRecording()
        {
            try
            {
                Directory.CreateDirectory(_s.SaveFolder);
            }
            catch (Exception ex)
            {
                ShowStatus("Can't use save folder: " + ex.Message, RecText, 8);
                return;
            }
            if (!RefreshDeviceName())
            {
                ShowStatus("Recording device not connected · pick one in Settings", RecText, 8);
                return;
            }
            string name = "Recording " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            _currentWav = Path.Combine(_s.SaveFolder, name + ".wav");
            Array.Clear(_bars, 0, _bars.Length);
            _status = null;
            try
            {
                _engine.Start(_currentWav, _s.RecordDevice);
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, RecText, 8);
                return;
            }
            _tray.Icon = _iconRec;
            if (!Visible) _tray.ShowBalloonTip(1500, "Recording started", "Ctrl+Alt+R to stop.", ToolTipIcon.None);
            Invalidate();
        }

        /// <summary>
        /// Updates the device name shown under the timer from the chosen recording device.
        /// Returns false if a specific device is chosen but isn't connected.
        /// </summary>
        private bool RefreshDeviceName()
        {
            if (string.IsNullOrEmpty(_s.RecordDevice))
            {
                string def = CoreAudio.GetDefaultOutputName();
                _deviceName = string.IsNullOrEmpty(def) ? "System audio" : def;
                return true;
            }
            AudioDeviceInfo d = CoreAudio.FindDevice(_s.RecordDevice);
            if (d == null)
            {
                _deviceName = "Recording device not connected";
                return false;
            }
            _deviceName = d.IsInput ? "Mic · " + d.Name : d.Name;
            return true;
        }

        private void StopRecording()
        {
            _stopping = true;
            _engine.Stop();
            Invalidate();
        }

        private void TogglePause()
        {
            if (_demo != null || !_engine.IsRecording) return;
            _engine.IsPaused = !_engine.IsPaused;
            Invalidate();
        }

        // Called on the capture thread.
        private void OnEngineFinished(string wavPath, long frames, int sampleRate, string error)
        {
            if (_exiting || IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate { AfterRecording(wavPath, frames, error); });
            }
            catch { }
        }

        private void AfterRecording(string wavPath, long frames, string error)
        {
            _stopping = false;
            _tray.Icon = _iconIdle;
            _tray.Text = "Mini Recorder";

            if (wavPath == null || !File.Exists(wavPath))
            {
                ShowStatus(error ?? "Nothing was recorded.", RecText, 8);
                return;
            }
            if (frames == 0)
            {
                try { File.Delete(wavPath); } catch { }
                ShowStatus(error ?? "Nothing was recorded.", error != null ? RecText : Amber, 6);
                return;
            }
            if (error != null)
            {
                _tray.ShowBalloonTip(4000, "Recording stopped", error, ToolTipIcon.Warning);
            }

            if (_s.Format == "mp3")
            {
                StartMp3Conversion(wavPath);
            }
            else
            {
                _lastFile = wavPath;
                ShowStatus("Saved · " + Path.GetFileName(wavPath), OkText, 6);
            }
        }

        private void StartMp3Conversion(string wavPath)
        {
            string mp3Path = Path.ChangeExtension(wavPath, ".mp3");
            int kbps = _s.Mp3Kbps;
            _converting++;
            ShowStatus("Saving MP3…", TextMuted, 3600);
            Thread t = null;
            t = new Thread(delegate()
            {
                string err = null;
                try
                {
                    Mp3Converter.Convert(wavPath, mp3Path, kbps);
                    File.Delete(wavPath);
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                    try { if (File.Exists(mp3Path)) File.Delete(mp3Path); } catch { }
                }
                if (_exiting || IsDisposed) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _converting--;
                        lock (_converters) _converters.Remove(t);
                        if (err == null)
                        {
                            _lastFile = mp3Path;
                            ShowStatus("Saved · " + Path.GetFileName(mp3Path), OkText, 6);
                        }
                        else
                        {
                            _lastFile = wavPath;
                            ShowStatus("Saved as WAV (MP3 failed)", Amber, 8);
                            _tray.ShowBalloonTip(5000, "Saved as WAV instead of MP3", err, ToolTipIcon.Warning);
                        }
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            lock (_converters) _converters.Add(t);
            t.Start();
        }

        private void ShowStatus(string text, Color color, int seconds)
        {
            _status = text;
            _statusColor = color;
            _statusUntil = DateTime.Now.AddSeconds(seconds);
            Invalidate();
        }

        private void OpenFolder()
        {
            try
            {
                if (_lastFile != null && File.Exists(_lastFile))
                {
                    Process.Start("explorer.exe", "/select,\"" + _lastFile + "\"");
                }
                else
                {
                    Directory.CreateDirectory(_s.SaveFolder);
                    Process.Start("explorer.exe", "\"" + _s.SaveFolder + "\"");
                }
            }
            catch (Exception ex)
            {
                ShowStatus("Can't open folder: " + ex.Message, RecText, 6);
            }
        }

        private void ToggleVisible()
        {
            if (Visible)
            {
                Hide();
                _s.Save();
            }
            else
            {
                Show();
                try { Win32.SetForegroundWindow(Handle); } catch { }
            }
        }

        private void HideToTray()
        {
            Hide();
            if (!_s.TrayHintShown && _demo == null)
            {
                _tray.ShowBalloonTip(4000, "Mini Recorder is still running",
                    "Press Ctrl+Alt+H or click the tray icon to bring the bar back.", ToolTipIcon.Info);
                _s.TrayHintShown = true;
            }
            _s.Save();
        }

        private void ExitApp()
        {
            if (_engine.IsRecording)
            {
                if (Visible == false) Show();
                DialogResult r = MessageBox.Show(this, "A recording is in progress.\n\nStop it, save it and exit?",
                    "Mini Recorder", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;
            }
            Cursor = Cursors.WaitCursor;
            _exiting = true;
            string wav = _engine.IsRecording ? _currentWav : null;
            _engine.StopAndWait(5000);

            // Finish any MP3 conversions that are still running.
            Thread[] pending;
            lock (_converters) pending = _converters.ToArray();
            foreach (Thread t in pending) t.Join(60000);

            if (wav != null && File.Exists(wav) && _s.Format == "mp3")
            {
                string mp3 = Path.ChangeExtension(wav, ".mp3");
                Thread t = new Thread(delegate()
                {
                    try { Mp3Converter.Convert(wav, mp3, _s.Mp3Kbps); File.Delete(wav); }
                    catch { try { if (File.Exists(mp3)) File.Delete(mp3); } catch { } }
                });
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
                t.Join(120000);
            }
            _s.Save();
            _tray.Visible = false;
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
            {
                _exiting = true;
                _engine.StopAndWait(3000); // keeps the WAV; no time for MP3 during shutdown
                _s.Save();
                _tray.Visible = false;
            }
            else if (!_exiting && e.CloseReason == CloseReason.UserClosing && _demo == null)
            {
                e.Cancel = true; // Alt+F4 hides to tray instead of quitting
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _uiTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ menus
        private void BuildMenus()
        {
            // Gear menu
            ToolStripMenuItem fmt = new ToolStripMenuItem("Save as");
            ToolStripMenuItem fmtMp3 = new ToolStripMenuItem("MP3 (smaller files)");
            ToolStripMenuItem fmtWav = new ToolStripMenuItem("WAV (lossless, large)");
            fmtMp3.Click += delegate { _s.Format = "mp3"; _s.Save(); };
            fmtWav.Click += delegate { _s.Format = "wav"; _s.Save(); };
            fmt.DropDownItems.Add(fmtMp3);
            fmt.DropDownItems.Add(fmtWav);

            ToolStripMenuItem quality = new ToolStripMenuItem("MP3 quality");
            int[] rates = { 128, 192, 320 };
            foreach (int k in rates)
            {
                int kk = k;
                ToolStripMenuItem qi = new ToolStripMenuItem(k + " kbps" + (k == 192 ? " (recommended)" : ""));
                qi.Tag = kk;
                qi.Click += delegate { _s.Mp3Kbps = kk; _s.Save(); };
                quality.DropDownItems.Add(qi);
            }

            ToolStripMenuItem settings = new ToolStripMenuItem("Settings…");
            settings.Click += delegate { ShowSettings(); };
            ToolStripMenuItem onTop = new ToolStripMenuItem("Always on top");
            onTop.Click += delegate { _s.AlwaysOnTop = !_s.AlwaysOnTop; TopMost = _s.AlwaysOnTop; _s.Save(); };
            ToolStripMenuItem keys = new ToolStripMenuItem("Keyboard shortcuts…");
            keys.Click += delegate { ShowShortcuts(); };
            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitApp(); };

            _gearMenu.Items.AddRange(new ToolStripItem[] { settings, new ToolStripSeparator(), fmt, quality, onTop, new ToolStripSeparator(), keys, new ToolStripSeparator(), exit });
            _gearMenu.Opening += delegate
            {
                fmtMp3.Checked = _s.Format == "mp3";
                fmtWav.Checked = _s.Format == "wav";
                quality.Enabled = _s.Format == "mp3";
                foreach (ToolStripMenuItem qi in quality.DropDownItems) qi.Checked = (int)qi.Tag == _s.Mp3Kbps;
                onTop.Checked = _s.AlwaysOnTop;
            };

            // Tray menu
            ToolStripMenuItem tShow = new ToolStripMenuItem("Show bar");
            tShow.ShortcutKeyDisplayString = "Ctrl+Alt+H";
            tShow.Click += delegate { ToggleVisible(); };
            ToolStripMenuItem tRec = new ToolStripMenuItem("Start recording");
            tRec.ShortcutKeyDisplayString = "Ctrl+Alt+R";
            tRec.Click += delegate { ToggleRecord(); };
            ToolStripMenuItem tPause = new ToolStripMenuItem("Pause");
            tPause.ShortcutKeyDisplayString = "Ctrl+Alt+P";
            tPause.Click += delegate { TogglePause(); };
            ToolStripMenuItem tFolder = new ToolStripMenuItem("Open recordings folder");
            tFolder.ShortcutKeyDisplayString = "Ctrl+Alt+O";
            tFolder.Click += delegate { OpenFolder(); };
            ToolStripMenuItem tSettings = new ToolStripMenuItem("Settings…");
            tSettings.Click += delegate { ShowSettings(); };
            ToolStripMenuItem tExit = new ToolStripMenuItem("Exit");
            tExit.Click += delegate { ExitApp(); };
            _trayMenu.Items.AddRange(new ToolStripItem[] { tShow, tRec, tPause, tFolder, tSettings, new ToolStripSeparator(), tExit });
            _trayMenu.Opening += delegate
            {
                tShow.Text = Visible ? "Hide bar" : "Show bar";
                tRec.Text = _engine.IsRecording ? "Stop recording" : "Start recording";
                tPause.Enabled = _engine.IsRecording;
                tPause.Text = _engine.IsPaused ? "Resume" : "Pause";
            };
        }

        private void ShowSettings()
        {
            if (_settingsOpen) return;
            _settingsOpen = true;
            try
            {
                string oldFolder = _s.SaveFolder;
                using (SettingsDialog d = new SettingsDialog(_s, _engine.IsRecording))
                {
                    d.TopMost = _s.AlwaysOnTop;
                    DialogResult r = Visible ? d.ShowDialog(this) : d.ShowDialog();
                    if (r != DialogResult.OK) return;
                }
                if (_demo == null && !_engine.IsRecording) RefreshDeviceName();
                if (!string.Equals(oldFolder, _s.SaveFolder, StringComparison.OrdinalIgnoreCase))
                {
                    _lastFile = null; // "open folder" should go to the new folder
                    ShowStatus("Saving to " + _s.SaveFolder, TextMuted, 5);
                }
                Invalidate();
            }
            finally
            {
                _settingsOpen = false;
            }
        }

        private void ShowShortcuts()
        {
            MessageBox.Show(this,
                "These work anywhere in Windows, even when the bar is hidden:\n\n" +
                "Ctrl+Alt+R\tStart / stop recording\n" +
                "Ctrl+Alt+P\tPause / resume\n" +
                "Ctrl+Alt+O\tOpen recordings folder\n" +
                "Ctrl+Alt+H\tShow / hide the bar\n\n" +
                "When the bar is focused:\n\n" +
                "Space\t\tStart / stop recording\n" +
                "Esc\t\tHide to tray",
                "Keyboard shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space) { ToggleRecord(); return true; }
            if (keyData == Keys.Escape) { HideToTray(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------------ timer / meter
        private void OnTick(object sender, EventArgs e)
        {
            bool rec = Recording;
            if (rec)
            {
                float peak = _demo != null ? (float)(0.15 + _rnd.NextDouble() * 0.8) : _engine.TakePeak();
                float level = peak <= 0.0001f ? 0f : (float)Math.Max(0, 1 + 20 * Math.Log10(peak) / 48.0);
                if (_demo == null || _shotPath == null)
                {
                    Array.Copy(_bars, 1, _bars, 0, _bars.Length - 1);
                    _bars[_bars.Length - 1] = level;
                }
                if (_demo == null)
                {
                    string t = "Mini Recorder — " + (Paused ? "paused " : "recording ") + Elapsed();
                    if (_tray.Text != t) _tray.Text = t;
                }
            }
            if (_status != null && DateTime.Now > _statusUntil && _converting == 0) { _status = null; }
            Invalidate();
        }

        private string Elapsed()
        {
            if (_demo != null) return _demo == "--demo-idle" ? "00:00:00" : "00:03:27";
            if (!_engine.IsRecording) return "00:00:00";
            TimeSpan ts = TimeSpan.FromSeconds((double)_engine.FramesWritten / Math.Max(1, _engine.SampleRate));
            return string.Format("{0:00}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
        }

        // ------------------------------------------------------------------ painting
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Bg);
            g.ScaleTransform(_scale, _scale);

            bool rec = Recording, paused = Paused;

            // Record / stop button
            if (rec || _stopping)
            {
                using (SolidBrush b = new SolidBrush(_hover == "rec" ? RedHover : Red)) g.FillEllipse(b, _rRec);
                using (GraphicsPath sq = RoundRect(new RectangleF(_rRec.X + 15, _rRec.Y + 15, 10, 10), 2))
                    g.FillPath(Brushes.White, sq);
            }
            else
            {
                using (SolidBrush b = new SolidBrush(_hover == "rec" ? BtnHover : BtnBg)) g.FillEllipse(b, _rRec);
                using (Pen p = new Pen(Ring, 2)) g.DrawEllipse(p, _rRec.X + 1, _rRec.Y + 1, _rRec.Width - 2, _rRec.Height - 2);
                using (SolidBrush b = new SolidBrush(Red)) g.FillEllipse(b, _rRec.X + 13, _rRec.Y + 13, 14, 14);
            }

            // Timer
            using (SolidBrush b = new SolidBrush(rec ? TextMain : TextDim))
                g.DrawString(Elapsed(), _fontTimer, b, 60, 8);

            // Subtitle line
            float subW = rec ? _rMeter.X - 62 - 6 : _rHint.Right - 62 - 130;
            RectangleF sub = new RectangleF(62, 36, subW, 16);
            if (_status != null && !rec)
            {
                DrawText(g, _status, _statusColor, new RectangleF(62, 36, _rFolder.X - 10 - 62, 16));
            }
            else if (_stopping)
            {
                DrawText(g, "Finishing…", TextMuted, sub);
            }
            else if (rec && paused)
            {
                using (SolidBrush b = new SolidBrush(Amber))
                {
                    g.FillRectangle(b, 63, 40, 2.5f, 8);
                    g.FillRectangle(b, 67, 40, 2.5f, 8);
                }
                DrawText(g, "Paused", Amber, new RectangleF(74, 36, subW - 12, 16));
            }
            else if (rec)
            {
                using (SolidBrush b = new SolidBrush(RecText)) g.FillEllipse(b, 63, 40.5f, 7, 7);
                DrawText(g, "Recording", RecText, new RectangleF(74, 36, subW - 12, 16));
            }
            else
            {
                DrawText(g, _deviceName, TextMuted, sub);
            }

            if (rec)
            {
                // Level meter (newest on the right)
                const float bw = 4, gap = 3;
                int n = (int)((_rMeter.Width + gap) / (bw + gap));
                float x = _rMeter.Right - n * (bw + gap) + gap;
                Color c = paused ? Color.FromArgb(0x5C, 0x5F, 0x66) : Green;
                using (SolidBrush b = new SolidBrush(c))
                {
                    for (int i = 0; i < n; i++)
                    {
                        float v = _bars[_bars.Length - n + i];
                        float h = 4 + v * (_rMeter.Height - 4);
                        using (GraphicsPath bar = RoundRect(new RectangleF(x, _rMeter.Y + (_rMeter.Height - h) / 2, bw, h), 2))
                            g.FillPath(b, bar);
                        x += bw + gap;
                    }
                }
                // Pause / resume button
                using (SolidBrush b = new SolidBrush(_hover == "pause" ? BtnHover : BtnBg))
                using (GraphicsPath p = RoundRect(_rPause, 8)) g.FillPath(b, p);
                using (SolidBrush b = new SolidBrush(TextMain))
                {
                    float cx = _rPause.X + 18, cy = _rPause.Y + 18;
                    if (paused)
                        g.FillPolygon(b, new[] { new PointF(cx - 4, cy - 6), new PointF(cx + 6, cy), new PointF(cx - 4, cy + 6) });
                    else
                    {
                        g.FillRectangle(b, cx - 5, cy - 6, 3.5f, 12);
                        g.FillRectangle(b, cx + 1.5f, cy - 6, 3.5f, 12);
                    }
                }
            }
            else if (_status == null)
            {
                using (StringFormat sf = new StringFormat())
                using (SolidBrush b = new SolidBrush(TextMuted))
                {
                    sf.Alignment = StringAlignment.Far;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("Ctrl+Alt+R to record", _fontSmall, b, _rHint, sf);
                }
            }

            DrawIconButton(g, "folder", _rFolder);
            DrawIconButton(g, "gear", _rGear);
            DrawIconButton(g, "close", _rClose);
        }

        private void DrawIconButton(Graphics g, string id, RectangleF r)
        {
            if (_hover == id)
                using (SolidBrush b = new SolidBrush(BtnHover))
                using (GraphicsPath p = RoundRect(r, 8)) g.FillPath(b, p);

            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (Pen pen = new Pen(TextDim, 1.4f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                if (id == "close")
                {
                    g.DrawLine(pen, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f);
                    g.DrawLine(pen, cx + 4.5f, cy - 4.5f, cx - 4.5f, cy + 4.5f);
                }
                else if (id == "folder")
                {
                    using (GraphicsPath p = new GraphicsPath())
                    {
                        p.AddLines(new[]
                        {
                            new PointF(cx - 7, cy - 4.5f), new PointF(cx - 2.5f, cy - 4.5f), new PointF(cx - 1, cy - 3),
                            new PointF(cx + 7, cy - 3), new PointF(cx + 7, cy + 5.5f), new PointF(cx - 7, cy + 5.5f)
                        });
                        p.CloseFigure();
                        g.DrawPath(pen, p);
                    }
                }
                else if (id == "gear")
                {
                    g.DrawEllipse(pen, cx - 2.6f, cy - 2.6f, 5.2f, 5.2f);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                        g.DrawLine(pen, cx + c * 4.8f, cy + s * 4.8f, cx + c * 7f, cy + s * 7f);
                    }
                }
            }
        }

        private void DrawText(Graphics g, string text, Color color, RectangleF r)
        {
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
            using (SolidBrush b = new SolidBrush(color))
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(text, _fontSmall, b, r, sf);
            }
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ------------------------------------------------------------------ mouse
        private string HitTest(Point client)
        {
            PointF p = new PointF(client.X / _scale, client.Y / _scale);
            if (Contains(_rRec, p)) return "rec";
            if (Recording && Contains(_rPause, p)) return "pause";
            if (Contains(_rFolder, p)) return "folder";
            if (Contains(_rGear, p)) return "gear";
            if (Contains(_rClose, p)) return "close";
            return null;
        }

        private static bool Contains(RectangleF r, PointF p) { return r.Contains(p); }

        private static string TipFor(string id, bool rec, bool paused)
        {
            switch (id)
            {
                case "rec": return rec ? "Stop and save (Ctrl+Alt+R)" : "Start recording (Ctrl+Alt+R)";
                case "pause": return paused ? "Resume (Ctrl+Alt+P)" : "Pause (Ctrl+Alt+P)";
                case "folder": return "Open recordings folder (Ctrl+Alt+O)";
                case "gear": return "Settings";
                case "close": return "Hide to tray (Ctrl+Alt+H brings it back)";
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string h = HitTest(e.Location);
            if (h != _hover)
            {
                _hover = h;
                Cursor = h != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            string tip = h == null ? null : TipFor(h, Recording, Paused);
            if (tip != _tipShown)
            {
                _tipShown = tip;
                _tip.SetToolTip(this, tip);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = null;
            _tipShown = null;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _pressed = HitTest(e.Location);
            if (_pressed == null)
            {
                // Drag the bar from any empty spot.
                try
                {
                    Win32.ReleaseCapture();
                    Win32.SendMessage(Handle, Win32.WM_NCLBUTTONDOWN, (IntPtr)Win32.HTCAPTION, IntPtr.Zero);
                }
                catch { }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            string h = HitTest(e.Location);
            string pressed = _pressed;
            _pressed = null;
            if (e.Button != MouseButtons.Left || h == null || h != pressed) return;
            switch (h)
            {
                case "rec": ToggleRecord(); break;
                case "pause": TogglePause(); break;
                case "folder": OpenFolder(); break;
                case "gear":
                    _gearMenu.Show(this, new Point((int)(_rGear.X * _scale), (int)(_rGear.Bottom * _scale) + 6));
                    break;
                case "close": HideToTray(); break;
            }
        }

        // ------------------------------------------------------------------ icons
        private void BuildIcons()
        {
            _iconIdle = MakeTrayIcon(false);
            _iconRec = MakeTrayIcon(true);
        }

        private static Icon MakeTrayIcon(bool recording)
        {
            Size sz = SystemInformation.SmallIconSize;
            using (Bitmap bmp = new Bitmap(sz.Width, sz.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float w = sz.Width;
                    if (recording)
                    {
                        using (SolidBrush b = new SolidBrush(Red)) g.FillEllipse(b, w * 0.06f, w * 0.06f, w * 0.88f, w * 0.88f);
                        g.FillRectangle(Brushes.White, w * 0.34f, w * 0.34f, w * 0.32f, w * 0.32f);
                    }
                    else
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(0x2A, 0x2C, 0x31))) g.FillEllipse(b, w * 0.06f, w * 0.06f, w * 0.88f, w * 0.88f);
                        using (Pen p = new Pen(Color.FromArgb(0xCE, 0xD4, 0xDA), Math.Max(1f, w / 16f))) g.DrawEllipse(p, w * 0.08f, w * 0.08f, w * 0.84f, w * 0.84f);
                        using (SolidBrush b = new SolidBrush(Red)) g.FillEllipse(b, w * 0.3f, w * 0.3f, w * 0.4f, w * 0.4f);
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon tmp = Icon.FromHandle(h);
                Icon copy = (Icon)tmp.Clone();
                try { Win32.DestroyIcon(h); } catch { }
                return copy;
            }
        }

        private void SaveShot()
        {
            try
            {
                using (Bitmap b = new Bitmap(Width, Height))
                {
                    DrawToBitmap(b, new Rectangle(0, 0, Width, Height));
                    b.Save(_shotPath, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch { }
            Application.Exit();
        }
    }
}
