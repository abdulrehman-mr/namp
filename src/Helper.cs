using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;

namespace Namp
{
    /// <summary>
    /// "NAMP Helper" - a tiny Windows service (LocalSystem) so the NAMP window can run as a normal user.
    /// It only does three privileged jobs, driven by config\settings.json:
    ///   1. keeps the NAMP block of the hosts file in sync (local names only: .local, .test, ...)
    ///   2. creates/trusts NAMP's local certificate authority and issues site certificates
    ///   3. writes the MariaDB service configuration (ports) into the admin-only config\service folder
    /// It never runs anything from a user-writable folder.
    /// </summary>
    class HelperService : ServiceBase
    {
        public const string Name = "NAMP-Helper";
        Thread worker;
        volatile bool stopping;
        readonly ManualResetEvent wake = new ManualResetEvent(false);

        public HelperService()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            worker = new Thread(Loop) { IsBackground = true };
            worker.Start();
        }

        protected override void OnStop() { stopping = true; wake.Set(); if (worker != null) worker.Join(5000); }
        protected override void OnShutdown() { OnStop(); }

        void Loop()
        {
            DateTime lastWrite = DateTime.MinValue, lastFull = DateTime.MinValue;
            while (!stopping)
            {
                try
                {
                    DateTime w = File.Exists(Paths.SettingsFile) ? File.GetLastWriteTimeUtc(Paths.SettingsFile) : DateTime.MinValue;
                    if (w != lastWrite || (DateTime.UtcNow - lastFull).TotalMinutes >= 2)
                    {
                        lastWrite = w;
                        lastFull = DateTime.UtcNow;
                        Helper.Sync();
                    }
                }
                catch (Exception ex) { Log.Error("helper", ex.Message); }
                wake.WaitOne(1000);
            }
        }
    }

    static class Helper
    {
        static readonly Regex DomainRx = new Regex("^(?=.{1,200}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$");
        static string lastStatus;

        public static string StatusFile { get { return Paths.R("config", "service", "helper.status"); } }

        /// <summary>Applies settings.json to the privileged parts of the system. Safe to call repeatedly.</summary>
        public static void Sync()
        {
            Settings s;
            try { s = Settings.Load(); } catch { return; }
            Settings.Current = s;
            Directory.CreateDirectory(Paths.ServiceConfig);

            Configs.WriteMyIni();

            // Only well-formed local names ever reach the hosts file or a certificate.
            var domains = s.Domains.Select(d => (d.Domain ?? "").ToLowerInvariant())
                .Where(d => DomainRx.IsMatch(d) && Ssl.IsLocalTld(d)).Distinct().ToList();
            string err;
            bool hostsOk = Hosts.Sync(domains, out err);
            if (!hostsOk) Log.Error("helper", "hosts file: " + err);

            int certs = 0;
            if (s.EnableHttps && Ssl.EnsureCa())
            {
                if (Ssl.EnsureSiteCert("localhost", new[] { "localhost" }, new[] { "127.0.0.1", "::1" })) certs++;
                foreach (var d in domains)
                    if (Ssl.EnsureSiteCert(d, new[] { d, "www." + d }, null)) certs++;
            }
            string status = (hostsOk ? "ok" : "hosts-error") + "|" + domains.Count + " sites|" + certs + " certificates";
            if (status != lastStatus)
            {
                Log.Info("helper", "Synced: " + status.Replace("|", ", "));
                lastStatus = status;
            }
            try { Util.WriteAtomic(StatusFile, Util.StateTime() + "|" + status); } catch { }
        }

        // ------------------------------------------------------------------
        // Used by the (unprivileged) NAMP window
        // ------------------------------------------------------------------
        public static bool IsInstalled(string service)
        {
            try { return ServiceController.GetServices().Any(x => x.ServiceName == service); }
            catch { return false; }
        }

        public static bool IsRunning()
        {
            try { using (var sc = new ServiceController(HelperService.Name)) return sc.Status == ServiceControllerStatus.Running; }
            catch { return false; }
        }

        /// <summary>Waits until the helper has applied the current settings (condition true) - max timeoutSec.</summary>
        public static bool WaitFor(Func<bool> condition, int timeoutSec, string what)
        {
            if (condition()) return true;
            if (!IsRunning())
            {
                try { using (var sc = new ServiceController(HelperService.Name)) sc.Start(); } catch { }
            }
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSec)
            {
                Thread.Sleep(300);
                if (condition()) return true;
            }
            Log.Warn("main", "The NAMP Helper service did not finish " + what + " in time. Check that the \"NAMP Helper\" service is running.");
            return false;
        }

        public static bool HostsHas(string domain)
        {
            try { return File.ReadAllText(Hosts.HostsPath).Contains("127.0.0.1       " + domain + "\r\n"); }
            catch { return false; }
        }

        /// <summary>Starts the one-time setup with a UAC prompt. Returns true if it ran.</summary>
        public static bool RunElevatedSetup()
        {
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--install-services")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit(180000);
                return true;
            }
            catch { return false; } // user pressed "No"
        }
    }

    /// <summary>One-time machine setup, run elevated by the installer ("NAMP.exe --install-services").</summary>
    static class Setup
    {
        const string Sddl =
            "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)" +
            "(A;;CCLCSWRPWPDTLOCRRC;;;IU)(A;;CCLCSWRPWPDTLOCRRC;;;AU)(A;;CCLCSWLOCRRC;;;SU)";

        public static int Run()
        {
            Log.FileName = "namp-setup.log";
            Stack.Privileged = true;
            Settings.Current = Settings.Load();
            Configs.EnsureDirectories();
            Directory.CreateDirectory(Paths.ServiceConfig);
            Directory.CreateDirectory(Paths.R("config", "ssl"));
            Settings.Current.Save();

            Log.Info("setup", "Securing folder permissions...");
            SecureFolders();

            Log.Info("setup", "Registering services...");
            Configs.WriteMyIni();
            string db = MariaDb.ServiceName;
            string err = Native.CreateOrUpdateService(db, "NAMP MariaDB",
                "MariaDB database server for NAMP. Started and stopped by the NAMP Control Panel.",
                Util.Q(Stack.Db.Server) + " --defaults-file=" + Util.Q(Paths.MyIni) + " " + db, 3);
            if (err != null) Log.Error("setup", err);
            Sc("sidtype " + db + " unrestricted");
            string acctErr = Native.SetServiceAccount(db, "NT SERVICE\\" + db);
            if (acctErr != null) Log.Warn("setup", "Could not switch MariaDB to its own low-privilege account (" + acctErr + ") - it keeps running as LocalSystem.");
            Sc("privs " + db + " SeChangeNotifyPrivilege");
            Sc("sdset " + db + " " + Sddl);
            foreach (var dir in new[] { Paths.R("data"), Paths.Logs, Paths.Tmp })
                Icacls(dir, "/grant \"NT SERVICE\\" + db + ":(OI)(CI)M\"");
            Icacls(Paths.MariaDb, "/grant \"NT SERVICE\\" + db + ":(OI)(CI)RX\"");
            Icacls(Paths.ServiceConfig, "/grant \"NT SERVICE\\" + db + ":(OI)(CI)RX\"");

            string helperBin = Util.Q(Process.GetCurrentProcess().MainModule.FileName) + " --helper";
            err = Native.CreateOrUpdateService(HelperService.Name, "NAMP Helper",
                "Keeps NAMP's local domains (hosts file) and HTTPS certificates up to date so NAMP runs without administrator rights.", helperBin, 2);
            if (err != null) Log.Error("setup", err);
            Sc("sdset " + HelperService.Name + " " + Sddl);

            // Run one sync right now (creates the CA, trusts it, writes hosts) then (re)start the helper.
            try { Helper.Sync(); } catch (Exception ex) { Log.Error("setup", ex.Message); }
            try
            {
                using (var sc = new ServiceController(HelperService.Name))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
                    sc.Start();
                }
            }
            catch (Exception ex) { Log.Warn("setup", "Helper start: " + ex.Message); }
            Log.Ok("setup", "Setup complete.");
            return 0;
        }

        /// <summary>
        /// Program folders: Administrators/SYSTEM full, Users read-only (so nothing can swap the binaries that
        /// services run). Data folders: Users can modify. Service config + certificate folders: admin-only write.
        /// </summary>
        static void SecureFolders()
        {
            const string admins = "*S-1-5-32-544", system = "*S-1-5-18", users = "*S-1-5-32-545";
            Icacls(Paths.Root, "/inheritance:r /grant:r " + admins + ":(OI)(CI)F " + system + ":(OI)(CI)F " + users + ":(OI)(CI)RX");
            Icacls(Paths.Root + "\\*", "/reset /T /C /Q");
            foreach (var dir in new[] {
                Paths.Www, Paths.R("data"), Paths.Logs, Paths.Backups, Paths.Tmp, Paths.Config,
                Paths.R("apache", "conf"), Paths.R("apache", "namp-pages"), Paths.R("php"), Paths.Pma })
            {
                Directory.CreateDirectory(dir);
                Icacls(dir, "/grant " + users + ":(OI)(CI)M");
            }
            foreach (var dir in new[] { Paths.ServiceConfig, Paths.R("config", "ssl") })
            {
                Directory.CreateDirectory(dir);
                Icacls(dir, "/inheritance:r /grant:r " + admins + ":(OI)(CI)F " + system + ":(OI)(CI)F " + users + ":(OI)(CI)RX");
                Icacls(dir + "\\*", "/reset /T /C /Q");
            }
            // NAMP.exe itself and the folder it lives in stay read-only for users (the Helper runs it as SYSTEM).
        }

        static void Icacls(string path, string args)
        {
            var r = Util.Run("icacls.exe", Util.Q(path) + " " + args, 600000);
            if (!r.Ok && !r.Output.Contains("Successfully processed")) Log.Warn("setup", "icacls " + path + ": " + r.Output.Trim());
        }

        static void Sc(string args)
        {
            var r = Util.Run("sc.exe", args, 30000);
            if (!r.Ok) Log.Warn("setup", "sc " + args.Split(' ')[0] + ": " + r.Output.Trim());
        }

        /// <summary>Uninstall: stop + remove both services, the CA and the hosts block. Data stays.</summary>
        public static int Cleanup()
        {
            Log.FileName = "namp-setup.log";
            Stack.Privileged = true;
            try
            {
                Settings.Current = Settings.Load();
                Stack.Apache.KillAll();
                Stack.Db.Stop(120);
                foreach (var name in new[] { HelperService.Name, MariaDb.ServiceName })
                {
                    try
                    {
                        using (var sc = new ServiceController(name))
                        {
                            if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)); }
                        }
                    }
                    catch { }
                    Native.DeleteServiceByName(name);
                }
                Ssl.Untrust();
                string err;
                Hosts.Sync(new string[0], out err);
            }
            catch { }
            return 0;
        }
    }
}
