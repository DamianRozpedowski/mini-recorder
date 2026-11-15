// Settings.cs - tiny key=value settings file in %APPDATA%\MiniRecorder\settings.ini
using System;
using System.Collections.Generic;
using System.IO;

namespace MiniRecorder
{
    internal sealed class Settings
    {
        public string SaveFolder;
        public string Format = "mp3";      // "mp3" or "wav"
        public int Mp3Kbps = 192;
        public bool AlwaysOnTop = true;
        public int X = int.MinValue, Y = int.MinValue;
        public bool TrayHintShown;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniRecorder");
                return Path.Combine(dir, "settings.ini");
            }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            s.SaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");
            try
            {
                if (!File.Exists(FilePath)) return s;
                Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                string v;
                int n;
                if (d.TryGetValue("SaveFolder", out v) && v.Length > 0) s.SaveFolder = v;
                if (d.TryGetValue("Format", out v) && (v == "mp3" || v == "wav")) s.Format = v;
                if (d.TryGetValue("Mp3Kbps", out v) && int.TryParse(v, out n)) s.Mp3Kbps = n;
                if (d.TryGetValue("AlwaysOnTop", out v)) s.AlwaysOnTop = v != "0";
                if (d.TryGetValue("X", out v) && int.TryParse(v, out n)) s.X = n;
                if (d.TryGetValue("Y", out v) && int.TryParse(v, out n)) s.Y = n;
                if (d.TryGetValue("TrayHintShown", out v)) s.TrayHintShown = v == "1";
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "SaveFolder=" + SaveFolder,
                    "Format=" + Format,
                    "Mp3Kbps=" + Mp3Kbps,
                    "AlwaysOnTop=" + (AlwaysOnTop ? "1" : "0"),
                    "X=" + X,
                    "Y=" + Y,
                    "TrayHintShown=" + (TrayHintShown ? "1" : "0")
                });
            }
            catch { }
        }
    }
}
