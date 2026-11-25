using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace Namp
{
    static class Program
    {
        public const string MutexName = "NAMP-ControlPanel-SingleInstance";

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 2 && args[0] == "--preview")
                return SelfTest.Preview(args[1], args[2], args.Contains("--demo"));
            if (args.Length > 0 && args[0] == "--selftest")
                return SelfTest.Run(args.Length > 1 ? args[1] : null);

            Paths.Init(AppDomain.CurrentDomain.BaseDirectory);

            if (args.Contains("--uninstall-cleanup")) return Setup.Cleanup();
            if (args.Contains("--install-services")) return Setup.Run();
            if (args.Contains("--helper"))
            {
                Log.FileName = "namp-helper.log";
                Stack.Privileged = true;
                System.ServiceProcess.ServiceBase.Run(new HelperService());
                return 0;
            }
            Stack.Privileged = IsAdmin();

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    ActivateExisting();
                    return 0;
                }
                // Ask Windows to notify NAMP early during shutdown so the database is stopped first.
                Native.SetProcessShutdownParameters(0x3FF, 0);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Error("main", "Unexpected error: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("main", "Fatal error: " + e.ExceptionObject);
                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        static void ActivateExisting()
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                if (Native.IsIconic(p.MainWindowHandle)) Native.ShowWindowAsync(p.MainWindowHandle, 9);
                Native.SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }

        /// <summary>Run by the uninstaller: stop + remove the service and the hosts entries. Data is kept.</summary>
        static int UninstallCleanup()
        {
            try
            {
                Settings.Current = Settings.Load();
                Stack.Apache.KillAll();
                Stack.Db.Stop(120);
                Stack.Db.RemoveService();
                Ssl.Untrust();
                string err;
                Hosts.Sync(new string[0], out err);
            }
            catch { }
            return 0;
        }
    }
}
