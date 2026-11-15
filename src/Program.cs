// Program.cs - entry point. Mini Recorder: records system audio from a small always-on-top bar.
using System;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Mini Recorder")]
[assembly: System.Reflection.AssemblyProduct("Mini Recorder")]
[assembly: System.Reflection.AssemblyDescription("Records computer (system) audio")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace MiniRecorder
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try { Win32.SetProcessDPIAware(); } catch { }

            string demo = null;
            foreach (string a in args)
                if (a.StartsWith("--demo")) demo = a;

            bool createdNew;
            using (Mutex m = new Mutex(true, "MiniRecorder.SingleInstance", out createdNew))
            {
                if (!createdNew && demo == null)
                {
                    MessageBox.Show("Mini Recorder is already running. Look for it in the system tray.",
                                    "Mini Recorder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MiniBar(demo));
            }
        }
    }
}
