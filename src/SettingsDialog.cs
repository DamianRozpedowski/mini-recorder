// SettingsDialog.cs - small dark Settings window: recording device and save folder.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MiniRecorder
{
    internal sealed class SettingsDialog : Form
    {
        // Same palette as the bar.
        private static readonly Color Bg = Color.FromArgb(0x1C, 0x1D, 0x21);
        private static readonly Color FieldBg = Color.FromArgb(0x2A, 0x2C, 0x31);
        private static readonly Color BtnHover = Color.FromArgb(0x34, 0x36, 0x3C);
        private static readonly Color Ring = Color.FromArgb(0x3A, 0x3C, 0x42);
        private static readonly Color TextMain = Color.FromArgb(0xF1, 0xF3, 0xF5);
        private static readonly Color TextMuted = Color.FromArgb(0xAD, 0xB5, 0xBD);
        private static readonly Color Red = Color.FromArgb(0xE0, 0x31, 0x31);
        private static readonly Color RedHover = Color.FromArgb(0xF0, 0x3E, 0x3E);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private sealed class DeviceItem
        {
            public readonly string Id;
            private readonly string _text;
            public DeviceItem(string id, string text) { Id = id; _text = text; }
            public override string ToString() { return _text; }
        }

        private readonly Settings _s;
        private readonly ComboBox _device = new ComboBox();
        private readonly TextBox _folder = new TextBox();

        public SettingsDialog(Settings s, bool recording)
        {
            _s = s;

            Text = "Mini Recorder Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Font = new Font("Segoe UI", 9f);
            BackColor = Bg;
            ForeColor = TextMain;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(18, 14, 18, 14);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.ColumnCount = 2;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.Dock = DockStyle.Fill;

            // ---- recording device
            Label deviceHead = Heading("Recording device");
            grid.Controls.Add(deviceHead, 0, 0);
            grid.SetColumnSpan(deviceHead, 2);

            _device.DropDownStyle = ComboBoxStyle.DropDownList;
            _device.FlatStyle = FlatStyle.Flat;
            _device.BackColor = FieldBg;
            _device.ForeColor = TextMain;
            _device.Width = 400;
            _device.Margin = new Padding(0, 4, 0, 2);
            FillDevices();
            grid.Controls.Add(_device, 0, 1);
            grid.SetColumnSpan(_device, 2);

            Label deviceHint = Hint("System audio records what your computer plays. A microphone records your voice.");
            grid.Controls.Add(deviceHint, 0, 2);
            grid.SetColumnSpan(deviceHint, 2);

            // ---- save folder
            Label folderHead = Heading("Save recordings to");
            folderHead.Margin = new Padding(0, 14, 0, 0);
            grid.Controls.Add(folderHead, 0, 3);
            grid.SetColumnSpan(folderHead, 2);

            _folder.Text = s.SaveFolder;
            _folder.BorderStyle = BorderStyle.FixedSingle;
            _folder.BackColor = FieldBg;
            _folder.ForeColor = TextMain;
            _folder.Width = 314;
            _folder.Margin = new Padding(0, 5, 6, 2);
            grid.Controls.Add(_folder, 0, 4);

            Button browse = MakeButton("Browse…", false);
            browse.Margin = new Padding(0, 4, 0, 2);
            browse.Click += delegate { Browse(); };
            grid.Controls.Add(browse, 1, 4);

            if (recording)
            {
                Label note = Hint("Changes apply to the next recording.");
                note.Margin = new Padding(0, 12, 0, 0);
                grid.Controls.Add(note, 0, 5);
                grid.SetColumnSpan(note, 2);
            }

            // ---- buttons
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 18, 0, 0);
            Button save = MakeButton("Save", true);
            Button cancel = MakeButton("Cancel", false);
            cancel.DialogResult = DialogResult.Cancel;
            save.Click += delegate { SaveAndClose(); };
            save.Margin = new Padding(8, 0, 0, 0);
            cancel.Margin = new Padding(0);
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            grid.Controls.Add(buttons, 0, 6);
            grid.SetColumnSpan(buttons, 2);

            Controls.Add(grid);
            AcceptButton = save;
            CancelButton = cancel;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Dark title bar on Windows 10 20H1+ / 11; ignored elsewhere.
            try
            {
                int on = 1;
                Win32.DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
            }
            catch { }
        }

        private void FillDevices()
        {
            string def = CoreAudio.GetDefaultOutputName();
            _device.Items.Add(new DeviceItem("", "System audio · default playback device" +
                                                 (string.IsNullOrEmpty(def) ? "" : " (" + def + ")")));
            int selected = 0;
            List<AudioDeviceInfo> devices = CoreAudio.ListDevices();
            foreach (AudioDeviceInfo d in devices)
            {
                string label = d.IsInput ? "Microphone · " + d.Name : "System audio · " + d.Name;
                int idx = _device.Items.Add(new DeviceItem(d.Id, label));
                if (d.Id == _s.RecordDevice) selected = idx;
            }
            // Keep a saved device that's currently unplugged, rather than silently switching.
            if (selected == 0 && !string.IsNullOrEmpty(_s.RecordDevice))
                selected = _device.Items.Add(new DeviceItem(_s.RecordDevice, "Saved device (not connected)"));
            _device.SelectedIndex = selected;

            // Widen the drop-down to fit the longest name.
            int w = _device.Width;
            foreach (object o in _device.Items)
                w = Math.Max(w, TextRenderer.MeasureText(o.ToString(), _device.Font).Width + 24);
            _device.DropDownWidth = Math.Min(w, 700);
        }

        private void Browse()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Choose where recordings are saved";
                d.SelectedPath = Directory.Exists(_folder.Text) ? _folder.Text : _s.SaveFolder;
                d.ShowNewFolderButton = true;
                if (d.ShowDialog(this) == DialogResult.OK) _folder.Text = d.SelectedPath;
            }
        }

        private void SaveAndClose()
        {
            string folder = _folder.Text.Trim();
            if (folder.Length == 0 || !Path.IsPathRooted(folder))
            {
                MessageBox.Show(this, "Please choose a full folder path, for example C:\\Users\\You\\Music\\Recordings.",
                                "Save folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Can't use this folder:\n\n" + ex.Message, "Save folder",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DeviceItem dev = _device.SelectedItem as DeviceItem;
            _s.RecordDevice = dev != null ? dev.Id : "";
            _s.SaveFolder = folder;
            _s.Save();
            DialogResult = DialogResult.OK;
            Close();
        }

        // ------------------------------------------------------------------ control helpers
        private static Label Heading(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = TextMain;
            l.Font = new Font("Segoe UI Semibold", 9.5f);
            l.Margin = new Padding(0);
            return l;
        }

        private static Label Hint(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.MaximumSize = new Size(400, 0);
            l.ForeColor = TextMuted;
            l.Margin = new Padding(0, 2, 0, 0);
            return l;
        }

        private static Button MakeButton(string text, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.BackColor = primary ? Red : FieldBg;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = primary ? Red : Ring;
            b.FlatAppearance.MouseOverBackColor = primary ? RedHover : BtnHover;
            b.FlatAppearance.MouseDownBackColor = primary ? Red : Ring;
            b.AutoSize = true;
            b.MinimumSize = new Size(80, 28);
            b.Cursor = Cursors.Hand;
            return b;
        }
    }
}
