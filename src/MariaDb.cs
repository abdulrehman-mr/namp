using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Namp
{
    /// <summary>
    /// Runs MariaDB and keeps its data safe:
    ///  * durable InnoDB settings (see Configs.WriteMyIni)
    ///  * runs as a Windows service, so Windows itself shuts it down cleanly
    ///    even if NAMP is killed or the PC is shut down with NAMP open
    ///  * remembers clean vs. unclean shutdowns
    ///  * an automatic recovery ladder when the server refuses to start
    ///  * a "last good" snapshot after every clean stop + periodic SQL backups
    /// </summary>
    class MariaDb
    {
        public const string ServiceName = "NAMP-MariaDB";
        public static readonly string[] SystemDatabases = { "information_schema", "performance_schema", "mysql", "sys", "phpmyadmin" };

        /// <summary>true = Windows service (normal). false = child process (self-test / fallback).</summary>
        public bool UseService = true;
        public volatile bool ExpectedRunning;
        Process proc;

        public string Server { get { return Paths.MariaBin("mariadbd.exe", "mysqld.exe"); } }
        public string Client { get { return Paths.MariaBin("mariadb.exe", "mysql.exe"); } }
        public string Dump { get { return Paths.MariaBin("mariadb-dump.exe", "mysqldump.exe"); } }
        public string Admin { get { return Paths.MariaBin("mariadb-admin.exe", "mysqladmin.exe"); } }
        public string Check { get { return Paths.MariaBin("mariadb-check.exe", "mysqlcheck.exe"); } }
        public string InstallDb { get { return Paths.MariaBin("mariadb-install-db.exe", "mysql_install_db.exe"); } }
        public string AriaChk { get { return Paths.MariaBin("aria_chk.exe", "aria_chk.exe"); } }

        int Port { get { return Settings.Current.MySqlPort; } }

        public bool IsInitialized { get { return Directory.Exists(Path.Combine(Paths.Data, "mysql")); } }

        // ------------------------------------------------------------------
        // Process / service discovery
        // ------------------------------------------------------------------
        public int FindPid()
        {
            // Service mode: ask the service manager (a normal user may not open the service's process).
            var st = ServiceStatus();
            if (st != null && st != ServiceControllerStatus.Stopped)
            {
                int spid = Native.GetServicePid(ServiceName);
                if (spid > 0) return spid;
            }
            // Process mode (rescue / fallback): our own mariadbd.exe from this NAMP folder.
            foreach (var exe in new[] { Server, Paths.MariaBin("mysqld.exe", "mysqld.exe") })
            {
                string name = Path.GetFileNameWithoutExtension(exe);
                foreach (var p in Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        string path = Native.GetProcessPath(p.Id);
                        if (path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
                            return p.Id;
                    }
                }
            }
            return 0;
        }

        public bool IsRunning { get { return FindPid() != 0; } }

        ServiceControllerStatus? ServiceStatus()
        {
            // In process mode the Windows service (possibly another NAMP install) must never be touched.
            if (!UseService) return null;
            try
            {
                using (var sc = new ServiceController(ServiceName)) return sc.Status;
            }
            catch { return null; }
        }

        public bool EnsureService(out string error)
        {
            string bin = Util.Q(Server) + " --defaults-file=" + Util.Q(Paths.MyIni) + " " + ServiceName;
            error = Native.CreateOrUpdateService(ServiceName, "NAMP MariaDB",
                "MariaDB database server for NAMP. Started and stopped by the NAMP Control Panel.", bin, 3);
            return error == null;
        }

        public void RemoveService()
        {
            Native.DeleteServiceByName(ServiceName);
        }

        // ------------------------------------------------------------------
        // Clean / unclean shutdown tracking
        // ------------------------------------------------------------------
        public static string ReadState(out DateTime? when)
        {
            when = null;
            try
            {
                if (!File.Exists(Paths.DbStateFile)) return "stopped";
                var parts = File.ReadAllText(Paths.DbStateFile).Trim().Split('|');
                if (parts.Length > 1) when = Util.ParseStateTime(parts[1]);
                return parts[0];
            }
            catch { return "unknown"; }
        }

        static void WriteState(string state)
        {
            try { Util.WriteAtomic(Paths.DbStateFile, state + "|" + Util.StateTime()); } catch { }
        }

        // ------------------------------------------------------------------
        // First run: create the data directory
        // ------------------------------------------------------------------
        public bool Initialize(out string error)
        {
            error = null;
            if (IsInitialized) return true;
            if (Directory.Exists(Paths.Data) && Directory.EnumerateFileSystemEntries(Paths.Data).Any())
                Quarantine(Paths.Data, "incomplete-data");
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.Data));
            Log.Info("mariadb", "Creating the MariaDB data directory (first run)...");
            var r = Util.Run(InstallDb, "--datadir=" + Util.Q(Paths.Data) + " --port=" + Port, 600000);
            if (!r.Ok || !IsInitialized)
            {
                error = "mariadb-install-db failed: " + r.Output.Trim();
                return false;
            }
            // mariadb-install-db leaves a my.ini in the data folder; NAMP uses config\my.ini instead.
            try { File.Delete(Path.Combine(Paths.Data, "my.ini")); } catch { }
            WriteState("stopped");
            Log.Ok("mariadb", "Data directory created at " + Paths.Data);
            return true;
        }

        // ------------------------------------------------------------------
        // Start (with automatic recovery)
        // ------------------------------------------------------------------
        public bool Start(Func<string, string, bool> confirm)
        {
            if (IsRunning && Util.CanConnect(Port))
            {
                ExpectedRunning = true;
                return true;
            }
            if (IsRunning)
            {
                // Our server is alive but not answering yet (e.g. still recovering) - wait for it.
                if (WaitReady(180)) { ExpectedRunning = true; return true; }
            }
            if (Util.IsPortListening(Port))
            {
                Log.Error("mariadb", "Port " + Port + " is already in use by " + Util.DescribePortOwner(Port) +
                    ". Stop that program (e.g. XAMPP's MySQL) or change the MySQL port in Settings.");
                return false;
            }
            string err;
            if (!Initialize(out err)) { Log.Error("mariadb", err); return false; }
            if (Stack.Privileged || !UseService) Configs.TryWriteMyIni();
            else if (!Helper.WaitFor(() => Configs.MyIniHasPort(Port), 20, "updating the database settings")) return false;
            Configs.WriteClientCnf();

            DateTime? when;
            string prev = ReadState(out when);
            bool unclean = prev == "running";
            if (unclean)
            {
                Log.Warn("mariadb", "MariaDB was NOT shut down cleanly last time" + (when.HasValue ? " (running since " + when.Value.ToString("g") + ")" : "") +
                    " - power loss, forced shutdown or crash. InnoDB will now replay its transaction log to recover every committed change.");
            }
            WriteState("running");

            long pos = Util.FileSize(Paths.MariaErrorLog);
            Log.Info("mariadb", "Starting MariaDB on port " + Port + (UseService ? " (Windows service " + ServiceName + ")" : "") + "...");
            if (TryStart(null, 300))
            {
                OnStarted(unclean);
                return true;
            }

            // ---------------- Recovery ladder ----------------
            string errors = ErrorsSince(pos);
            ReportErrors(errors);
            if (Regex.IsMatch(errors, "Bind on TCP/IP port|Do you already have another (mysqld|server) running", RegexOptions.IgnoreCase))
            {
                Log.Error("mariadb", "Port " + Port + " is busy. Close the other database server (XAMPP?) or change the port in Settings.");
                WriteState("stopped");
                return false;
            }

            // Level 1: remove files that are safe to rebuild (pid, temp tablespace, crash-damaged Aria/tc logs)
            Log.Warn("recovery", "Level 1: cleaning up temporary and crash-damaged log files, then retrying...");
            if (CleanupTransientFiles(errors))
            {
                // Aria logs were reset: make every Aria table independent of the old log before starting.
                ariaReset = true;
                RepairAriaTables(true);
            }
            pos = Util.FileSize(Paths.MariaErrorLog);
            if (TryStart(null, 300)) { Log.Ok("recovery", "MariaDB recovered (level 1) and is running."); OnStarted(true); return true; }
            errors = ErrorsSince(pos);
            ReportErrors(errors);

            // Level 2: offline repair of Aria tables (MariaDB's system tables use Aria)
            Log.Warn("recovery", "Level 2: repairing Aria/system tables offline...");
            RepairAriaTables(false);
            pos = Util.FileSize(Paths.MariaErrorLog);
            if (TryStart(null, 300)) { Log.Ok("recovery", "MariaDB recovered (level 2) and is running."); OnStarted(true); return true; }
            errors = ErrorsSince(pos);
            ReportErrors(errors);

            string snapInfo = Backups.LatestSnapshot() != null ? Backups.LatestSnapshot().Time.ToString("g") : "none";
            bool ok = confirm("MariaDB needs repair",
                "MariaDB could not start - its data files were damaged (most likely by a power cut).\n\n" +
                "NAMP can repair it automatically:\n" +
                "  1. Open the data in safe mode, export everything and rebuild clean data files.\n" +
                "  2. If that fails, restore the last good snapshot (taken " + snapInfo + ").\n" +
                "  3. If that fails, rebuild from the automatic SQL backups.\n\n" +
                "Nothing is deleted - the damaged files are kept in:\n" + Paths.Quarantine + "\n\nRepair now?");
            if (!ok)
            {
                Log.Warn("recovery", "Repair postponed by user. Start MariaDB again to run the repair.");
                return false;
            }

            if (RescueRebuild()) return true;
            if (RestoreSnapshot()) return true;
            if (RebuildFromSqlBackups()) return true;

            Log.Error("recovery", "Automatic recovery failed. Your damaged data is kept in " + Paths.Quarantine +
                " and your SQL backups are in " + Paths.SqlBackups + ".");
            return false;
        }

        /// <summary>One start attempt. extraArgs forces child-process mode (used for safe-mode recovery).</summary>
        bool TryStart(string extraArgs, int timeoutSec, bool forceProcess = false)
        {
            bool asService = UseService && !forceProcess && extraArgs == null;
            if (asService && !Helper.IsInstalled(ServiceName))
            {
                Log.Warn("mariadb", "The NAMP MariaDB service is not installed - running MariaDB as a normal process.");
                asService = false;
            }
            try
            {
                if (asService)
                {
                    using (var sc = new ServiceController(ServiceName))
                    {
                        if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                    }
                }
                else
                {
                    var psi = new ProcessStartInfo(Server, "--defaults-file=" + Util.Q(Paths.MyIni) + (extraArgs ?? ""));
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WorkingDirectory = Path.GetDirectoryName(Server);
                    proc = Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                Log.Error("mariadb", "Could not launch MariaDB: " + ex.Message);
                return false;
            }
            return WaitReady(timeoutSec);
        }

        bool WaitReady(int timeoutSec)
        {
            var sw = Stopwatch.StartNew();
            int lastNote = 0;
            Thread.Sleep(400);
            while (sw.Elapsed.TotalSeconds < timeoutSec)
            {
                if (FindPid() == 0)
                {
                    Thread.Sleep(700); // service may still be spawning the process
                    if (FindPid() == 0)
                    {
                        var st = ServiceStatus();
                        if (!UseService || st == null || st == ServiceControllerStatus.Stopped) return false;
                    }
                }
                if (Util.CanConnect(Port)) return true;
                int secs = (int)sw.Elapsed.TotalSeconds;
                if (secs >= lastNote + 15)
                {
                    lastNote = secs;
                    if (secs > 0) Log.Info("mariadb", "Still starting (" + secs + "s) - crash recovery can take a moment on large databases...");
                }
                Thread.Sleep(400);
            }
            Log.Error("mariadb", "MariaDB did not become ready within " + timeoutSec + " seconds.");
            return false;
        }

        bool ariaReset;

        void OnStarted(bool afterUncleanShutdown)
        {
            ExpectedRunning = true;
            Log.Ok("mariadb", "MariaDB is running on port " + Port + " (PID " + FindPid() + ").");
            EnsureAdminAccount();
            if (ariaReset)
            {
                // Tables that lived through an Aria log reset must be re-stamped (REPAIR = zerofill) before use.
                ariaReset = false;
                Log.Info("recovery", "Re-checking and repairing Aria tables after the log reset...");
                var r = RunTool(Check, "--all-databases --check --auto-repair", 3600000);
                var after = RunTool(Check, "--all-databases --check", 3600000);
                var bad = after.Output.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimEnd().EndsWith(" OK") && !l.StartsWith("WARNING")).ToList();
                if (after.Ok && bad.Count == 0) Log.Ok("recovery", "All tables checked OK.");
                else Log.Warn("recovery", "Some tables still report problems:\n" + string.Join("\n", bad));
                afterUncleanShutdown = false;
            }
            EnsurePmaStorage();
            if (afterUncleanShutdown)
            {
                // Background integrity pass: checks every table and auto-repairs non-InnoDB ones.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Log.Info("mariadb", "Running an integrity check of all tables after the unclean shutdown...");
                    var r = RunTool(Check, "--all-databases --check --auto-repair --silent", 3600000);
                    string outp = r.Output.Trim();
                    if (r.Ok && outp.Length == 0) Log.Ok("mariadb", "Integrity check finished: all tables are OK.");
                    else Log.Warn("mariadb", "Integrity check output:\n" + outp);
                });
            }
        }

        static string ErrorsSince(long pos)
        {
            return Util.ReadFrom(Paths.MariaErrorLog, pos);
        }

        static void ReportErrors(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n')
                .Where(l => l.Contains("[ERROR]") || l.Contains("[FATAL]") || l.IndexOf("corrupt", StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(12).ToList();
            if (lines.Count == 0) lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).Reverse().Take(6).Reverse().ToList();
            foreach (var l in lines) Log.Error("mariadb", l.Trim());
        }

        /// <summary>Returns true if the Aria logs were moved away.</summary>
        bool CleanupTransientFiles(string errors)
        {
            bool ariaMoved = false;
            KillLeftovers();
            try
            {
                foreach (var f in Directory.GetFiles(Paths.Data, "*.pid")) TryDelete(f);
                TryDelete(Path.Combine(Paths.Data, "ibtmp1"));          // temporary tablespace, rebuilt on start
                TryDelete(Path.Combine(Paths.Data, "ib_buffer_pool"));  // only a cache warm-up list
                var moved = new List<string>();
                // aria_log_control / aria_log.* : the classic XAMPP "won't start after power loss" culprit.
                if (errors.IndexOf("aria", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    errors.IndexOf("mysql.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    errors.IndexOf("privilege", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    moved.AddRange(Directory.GetFiles(Paths.Data, "aria_log*"));
                }
                if (errors.IndexOf("tc.log", StringComparison.OrdinalIgnoreCase) >= 0 || errors.IndexOf("tc log", StringComparison.OrdinalIgnoreCase) >= 0)
                    moved.Add(Path.Combine(Paths.Data, "tc.log"));
                moved = moved.Where(File.Exists).ToList();
                if (moved.Count > 0)
                {
                    string q = Path.Combine(Paths.Quarantine, "logs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.CreateDirectory(q);
                    ariaMoved = moved.Any(f => Path.GetFileName(f).StartsWith("aria_log", StringComparison.OrdinalIgnoreCase));
                    foreach (var f in moved)
                    {
                        File.Move(f, Path.Combine(q, Path.GetFileName(f)));
                        Log.Info("recovery", "Moved damaged " + Path.GetFileName(f) + " to " + q);
                    }
                }
            }
            catch (Exception ex) { Log.Warn("recovery", "Cleanup: " + ex.Message); }
            return ariaMoved;
        }

        void RepairAriaTables(bool zerofill)
        {
            KillLeftovers();
            try
            {
                var tables = Directory.GetFiles(Paths.Data, "*.MAI", SearchOption.AllDirectories);
                foreach (var mai in tables)
                {
                    string table = mai.Substring(0, mai.Length - 4);
                    string mode = zerofill ? "--zerofill" : "--recover --force";
                    var r = Util.Run(AriaChk, mode + " --silent --silent --ignore-control-file --tmpdir=" + Util.Q(Paths.Tmp) + " " + Util.Q(table), 600000, Paths.Data);
                    if (!r.Ok) Log.Info("recovery", Path.GetFileName(table) + ": " + r.Output.Trim());
                }
                Log.Info("recovery", "Checked " + tables.Length + " Aria tables.");
            }
            catch (Exception ex) { Log.Warn("recovery", "Aria repair: " + ex.Message); }
        }

        /// <summary>Level 3: open the damaged data read-only in InnoDB safe mode, export everything, rebuild clean.</summary>
        public bool RescueRebuild()
        {
            for (int level = 1; level <= 3; level++)
            {
                KillLeftovers();
                Log.Warn("recovery", "Level 3: opening the data in InnoDB safe mode (innodb_force_recovery=" + level + ")...");
                if (!TryStart(" --innodb-force-recovery=" + level, 300)) { KillLeftovers(); continue; }

                Directory.CreateDirectory(Paths.Rescue);
                string dump = Path.Combine(Paths.Rescue, "rescue-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".sql");
                Log.Info("recovery", "Exporting every database to " + dump + "...");
                RunResult r;
                using (var fs = File.Create(dump))
                    r = RunTool(Dump, "--all-databases --routines --events --triggers --force --skip-lock-tables", 6 * 3600 * 1000, null, fs);
                StopProcessMode(120);
                KillLeftovers();
                bool complete = File.ReadAllText(dump).Contains("-- Dump completed");
                if (!complete)
                {
                    Log.Warn("recovery", "Safe-mode export was incomplete: " + r.Output.Trim());
                    continue;
                }

                Log.Info("recovery", "Export succeeded. Rebuilding clean data files...");
                string q = Quarantine(Paths.Data, "damaged-data");
                string err;
                if (!Initialize(out err)) { Log.Error("recovery", err); RestoreQuarantine(q); return false; }
                if (!TryStart(null, 300, true))
                {
                    Log.Error("recovery", "Fresh data directory did not start.");
                    KillLeftovers();
                    Directory.Delete(Paths.Data, true);
                    RestoreQuarantine(q);
                    return false;
                }
                Log.Info("recovery", "Importing the rescued data...");
                RunResult imp;
                using (var fs = File.OpenRead(dump))
                    imp = RunClientAsRoot("--force", fs);
                if (!imp.Ok) Log.Warn("recovery", "Import reported problems:\n" + Trim(imp.Output, 2000));
                RunClientAsRoot("-e \"FLUSH PRIVILEGES\"", null);
                StopProcessMode(120);
                KillLeftovers();
                WriteState("stopped");
                Log.Info("recovery", "Starting the rebuilt database normally...");
                if (TryStart(null, 300))
                {
                    Log.Ok("recovery", "Repair complete. All data was rescued and rebuilt into clean files. Old files: " + q);
                    OnStarted(false);
                    return true;
                }
                return false;
            }
            Log.Warn("recovery", "Safe-mode export was not possible.");
            return false;
        }

        /// <summary>Level 4: put back the last data snapshot taken after a clean shutdown.</summary>
        public bool RestoreSnapshot()
        {
            var snap = Backups.LatestSnapshot();
            if (snap == null) { Log.Warn("recovery", "Level 4: no snapshot available."); return false; }
            KillLeftovers();
            Log.Warn("recovery", "Level 4: restoring the last good snapshot from " + snap.Time.ToString("g") + "...");
            string q = Quarantine(Paths.Data, "damaged-data");
            string output;
            if (!Util.Robocopy(snap.Folder, Paths.Data, out output))
            {
                Log.Error("recovery", "Snapshot copy failed: " + output);
                return false;
            }
            WriteState("stopped");
            if (TryStart(null, 300))
            {
                Log.Ok("recovery", "Snapshot restored - your databases are back to their state at " + snap.Time.ToString("g") + ". Damaged files: " + q);
                var newer = Backups.SqlBackupsNewerThan(snap.Time);
                if (newer.Count > 0)
                    Log.Warn("recovery", "There are newer SQL backups for: " + string.Join(", ", newer) + ". Use Databases > Restore to bring them back if needed.");
                OnStarted(false);
                return true;
            }
            KillLeftovers();
            return false;
        }

        /// <summary>Level 5: brand-new data directory + import the newest SQL backup of every database.</summary>
        public bool RebuildFromSqlBackups()
        {
            var latest = Backups.LatestSqlBackupPerDatabase();
            if (latest.Count == 0) { Log.Warn("recovery", "Level 5: no SQL backups available."); return false; }
            KillLeftovers();
            Log.Warn("recovery", "Level 5: rebuilding from " + latest.Count + " SQL backups...");
            Quarantine(Paths.Data, "damaged-data");
            string err;
            if (!Initialize(out err)) { Log.Error("recovery", err); return false; }
            WriteState("running");
            if (!TryStart(null, 300)) return false;
            ExpectedRunning = true;
            EnsureAdminAccount();
            foreach (var kv in latest)
            {
                Log.Info("recovery", "Importing " + kv.Key + " from " + Path.GetFileName(kv.Value));
                Backups.Import(this, kv.Value, null);
            }
            Log.Ok("recovery", "Rebuilt from SQL backups.");
            OnStarted(false);
            return true;
        }

        static string Quarantine(string dir, string reason)
        {
            if (!Directory.Exists(dir)) return null;
            Directory.CreateDirectory(Paths.Quarantine);
            string dest = Path.Combine(Paths.Quarantine, reason + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.Move(dir, dest);
            Log.Info("recovery", "Kept a copy of the damaged data in " + dest);
            return dest;
        }

        static void RestoreQuarantine(string q)
        {
            try { if (q != null && !Directory.Exists(Paths.Data)) Directory.Move(q, Paths.Data); } catch { }
        }

        static void TryDelete(string f)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        static string Trim(string s, int max)
        {
            s = s.Trim();
            return s.Length > max ? s.Substring(0, max) + "..." : s;
        }

        // ------------------------------------------------------------------
        // Stop
        // ------------------------------------------------------------------
        /// <summary>Clean shutdown: MariaDB flushes everything to disk. Returns true if it stopped cleanly.</summary>
        public bool Stop(int timeoutSec)
        {
            ExpectedRunning = false;
            int pid = FindPid();
            if (pid == 0) return true; // already stopped (if it died on its own the state stays "running" = unclean)
            Log.Info("mariadb", "Stopping MariaDB (writing all data safely to disk)...");
            bool clean = false;
            var st = ServiceStatus();
            if (st == ServiceControllerStatus.Running || st == ServiceControllerStatus.StartPending)
            {
                try
                {
                    using (var sc = new ServiceController(ServiceName))
                    {
                        if (sc.CanStop) sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(timeoutSec));
                        clean = true;
                    }
                }
                catch (Exception ex) { Log.Warn("mariadb", "Service stop: " + ex.Message); }
            }
            else
            {
                clean = StopProcessMode(timeoutSec);
            }
            if (!WaitExit(15)) clean = false;
            if (!clean || FindPid() != 0)
            {
                Log.Error("mariadb", "MariaDB did not stop in time - forcing it. InnoDB will recover automatically on next start.");
                KillLeftovers();
                return false;
            }
            WriteState("stopped");
            Log.Ok("mariadb", "MariaDB stopped cleanly.");
            return true;
        }

        bool StopProcessMode(int timeoutSec)
        {
            int pid = FindPid();
            if (pid == 0) return true;
            // MariaDB on Windows listens for this named event: a clean shutdown with no password needed.
            if (!Native.SignalEvent("MySQLShutdown" + pid))
                RunTool(Admin, "shutdown", 30000);
            return WaitExit(timeoutSec);
        }

        bool WaitExit(int timeoutSec)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSec)
            {
                if (FindPid() == 0) return true;
                Thread.Sleep(250);
            }
            return FindPid() == 0;
        }

        /// <summary>Last resort: hard kill (InnoDB will still recover on the next start).</summary>
        public void KillLeftovers()
        {
            var st = ServiceStatus();
            if (st == ServiceControllerStatus.Running || st == ServiceControllerStatus.StartPending)
            {
                try
                {
                    using (var sc = new ServiceController(ServiceName))
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                    }
                }
                catch { }
            }
            int pid;
            int guard = 0;
            while ((pid = FindPid()) != 0 && guard++ < 5)
            {
                try { using (var p = Process.GetProcessById(pid)) { p.Kill(); p.WaitForExit(10000); } } catch { }
            }
        }

        // ------------------------------------------------------------------
        // SQL helpers (via the mariadb.exe client)
        // ------------------------------------------------------------------
        string workingCnf;

        string Cnf()
        {
            if (workingCnf != null) return workingCnf;
            foreach (var cnf in new[] { Paths.ClientCnf, Paths.ClientRootCnf })
            {
                var r = Util.Run(Client, "--defaults-file=" + Util.Q(cnf) + " -N -B -e \"SELECT 1\"", 20000);
                if (r.Ok) { workingCnf = cnf; return cnf; }
            }
            return Paths.ClientCnf;
        }

        public RunResult RunTool(string exe, string args, int timeoutMs, Stream stdin = null, Stream stdout = null)
        {
            return Util.Run(exe, "--defaults-file=" + Util.Q(Cnf()) + " " + args, timeoutMs, null, null, stdin, stdout);
        }

        RunResult RunClientAsRoot(string args, Stream stdin)
        {
            return Util.Run(Client, "--defaults-file=" + Util.Q(Paths.ClientRootCnf) + " " + args, 6 * 3600 * 1000, null, null, stdin);
        }

        /// <summary>Runs SQL and returns tab-separated rows, or null on error.</summary>
        public List<string[]> Query(string sql, out string error)
        {
            var r = RunTool(Client, "-N -B -e " + Util.Q(sql.Replace("\"", "\\\"")), 60000);
            if (!r.Ok)
            {
                workingCnf = null;
                error = r.Output.Trim();
                return null;
            }
            error = null;
            return r.Output.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).Select(l => l.Split('\t')).ToList();
        }

        public bool Exec(string sql, out string error)
        {
            return Query(sql, out error) != null;
        }

        void EnsureAdminAccount()
        {
            workingCnf = null;
            var test = Util.Run(Client, "--defaults-file=" + Util.Q(Paths.ClientCnf) + " -N -B -e \"SELECT 1\"", 20000);
            if (test.Ok) { workingCnf = Paths.ClientCnf; return; }
            string pw = Settings.Current.AdminPassword;
            var sql = new StringBuilder();
            foreach (var host in new[] { "localhost", "127.0.0.1", "::1" })
            {
                sql.Append("CREATE USER IF NOT EXISTS 'namp_admin'@'" + host + "' IDENTIFIED BY '" + pw + "'; ");
                sql.Append("ALTER USER 'namp_admin'@'" + host + "' IDENTIFIED BY '" + pw + "'; ");
                sql.Append("GRANT ALL PRIVILEGES ON *.* TO 'namp_admin'@'" + host + "' WITH GRANT OPTION; ");
            }
            sql.Append("FLUSH PRIVILEGES;");
            var r = Util.Run(Client, "--defaults-file=" + Util.Q(Paths.ClientRootCnf) + " -e " + Util.Q(sql.ToString()), 30000);
            if (r.Ok)
            {
                workingCnf = Paths.ClientCnf;
                Log.Info("mariadb", "NAMP maintenance account (namp_admin) is ready.");
            }
            else
            {
                Log.Warn("mariadb", "Could not create NAMP's maintenance account (was the root password changed?). " +
                    "Backups and phpMyAdmin auto-login need it. Details: " + r.Output.Trim());
            }
        }

        void EnsurePmaStorage()
        {
            try
            {
                string err;
                var rows = Query("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='phpmyadmin'", out err);
                if (rows == null || rows.Count == 0 || rows[0][0] != "0") return;
                string sqlFile = Path.Combine(Paths.Pma, "sql", "create_tables.sql");
                if (!File.Exists(sqlFile)) return;
                using (var fs = File.OpenRead(sqlFile))
                {
                    var r = RunTool(Client, "", 120000, fs);
                    if (r.Ok) Log.Info("mariadb", "phpMyAdmin configuration storage created.");
                }
            }
            catch { }
        }

        public class DbInfo
        {
            public string Name;
            public long Size;
            public int Tables;
        }

        public List<DbInfo> ListDatabases(bool includeSystem, out string error)
        {
            var rows = Query("SELECT s.schema_name, COALESCE(SUM(t.data_length + t.index_length), 0), COUNT(t.table_name) " +
                "FROM information_schema.schemata s LEFT JOIN information_schema.tables t ON t.table_schema = s.schema_name " +
                "GROUP BY s.schema_name ORDER BY s.schema_name", out error);
            var list = new List<DbInfo>();
            if (rows == null) return list;
            foreach (var r in rows)
            {
                if (r.Length < 3) continue;
                if (!includeSystem && SystemDatabases.Contains(r[0].ToLowerInvariant())) continue;
                long size; int tables;
                long.TryParse(r[1], out size);
                int.TryParse(r[2], out tables);
                list.Add(new DbInfo { Name = r[0], Size = size, Tables = tables });
            }
            return list;
        }
    }

    // =====================================================================
    // Backups: snapshots (after clean stop) + compressed SQL dumps
    // =====================================================================
    static class Backups
    {
        public class SnapshotInfo
        {
            public string Folder;
            public DateTime Time;
        }

        static readonly object SqlLock = new object();

        public static SnapshotInfo LatestSnapshot()
        {
            SnapshotInfo best = null;
            foreach (var slot in new[] { "A", "B" })
            {
                var info = ReadSnapshot(slot);
                if (info != null && (best == null || info.Time > best.Time)) best = info;
            }
            return best;
        }

        static SnapshotInfo ReadSnapshot(string slot)
        {
            try
            {
                string marker = Path.Combine(Paths.Snapshots, slot + ".complete");
                string folder = Path.Combine(Paths.Snapshots, slot);
                if (!File.Exists(marker) || !Directory.Exists(folder)) return null;
                var t = Util.ParseStateTime(File.ReadAllText(marker).Trim());
                if (!t.HasValue) return null;
                return new SnapshotInfo { Folder = folder, Time = t.Value };
            }
            catch { return null; }
        }

        /// <summary>Copies the data folder while MariaDB is stopped (files are consistent). Alternates between two slots.</summary>
        public static bool TakeSnapshot()
        {
            if (!Directory.Exists(Path.Combine(Paths.Data, "mysql"))) return false;
            var a = ReadSnapshot("A");
            var b = ReadSnapshot("B");
            string slot = a == null ? "A" : b == null ? "B" : (a.Time <= b.Time ? "A" : "B");
            string marker = Path.Combine(Paths.Snapshots, slot + ".complete");
            string folder = Path.Combine(Paths.Snapshots, slot);
            try
            {
                Directory.CreateDirectory(Paths.Snapshots);
                if (File.Exists(marker)) File.Delete(marker); // slot is invalid until the copy completes
                Log.Info("backup", "Saving last-good snapshot of the database files...");
                string output;
                if (!Util.Robocopy(Paths.Data, folder, out output))
                {
                    Log.Warn("backup", "Snapshot failed: " + output.Trim());
                    return false;
                }
                Util.WriteAtomic(marker, Util.StateTime());
                Log.Ok("backup", "Last-good snapshot saved (" + Util.FormatBytes(DirSize(folder)) + ").");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("backup", "Snapshot failed: " + ex.Message);
                return false;
            }
        }

        static long DirSize(string dir)
        {
            try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { return 0; }
        }

        public static DateTime? LastBackupRun()
        {
            try
            {
                if (!File.Exists(Paths.BackupStateFile)) return null;
                return Util.ParseStateTime(File.ReadAllText(Paths.BackupStateFile).Trim());
            }
            catch { return null; }
        }

        /// <summary>Dumps every user database (skipping ones that did not change). Returns number of new backup files.</summary>
        public static int BackupAll(MariaDb db)
        {
            if (!Monitor.TryEnter(SqlLock)) return 0;
            try
            {
                string err;
                var dbs = db.ListDatabases(false, out err);
                if (err != null) { Log.Warn("backup", "Automatic backup skipped: " + err); return 0; }
                int written = 0;
                foreach (var d in dbs)
                {
                    if (BackupDatabase(db, d.Name, false) != null) written++;
                }
                Util.WriteAtomic(Paths.BackupStateFile, Util.StateTime());
                if (dbs.Count > 0)
                    Log.Info("backup", "SQL backup finished: " + written + " changed database(s) saved, " + (dbs.Count - written) + " unchanged.");
                return written;
            }
            finally { Monitor.Exit(SqlLock); }
        }

        public static string BackupDirFor(string dbName)
        {
            return Path.Combine(Paths.SqlBackups, SafeName(dbName));
        }

        static string SafeName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        /// <summary>Creates a compressed dump. Returns the file, or null if unchanged/failed (unless force).</summary>
        public static string BackupDatabase(MariaDb db, string dbName, bool force)
        {
            string dir = BackupDirFor(dbName);
            Directory.CreateDirectory(dir);
            string final = Path.Combine(dir, SafeName(dbName) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".sql.gz");
            string part = final + ".part";
            string hashFile = Path.Combine(dir, ".last-hash");
            string hash;
            RunResult r;
            using (var sha = SHA256.Create())
            {
                using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write))
                {
                    using (var gz = new GZipStream(fs, CompressionMode.Compress, true))
                    using (var hs = new HashingStream(gz, sha))
                    {
                        r = db.RunTool(db.Dump, "--single-transaction --routines --events --triggers --skip-dump-date " +
                            "--add-drop-database --hex-blob --default-character-set=utf8mb4 --databases " + Util.Q(dbName),
                            6 * 3600 * 1000, null, hs);
                    }
                    fs.Flush(true);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                hash = BitConverter.ToString(sha.Hash).Replace("-", "");
            }
            if (!r.Ok)
            {
                File.Delete(part);
                Log.Warn("backup", "Backup of " + dbName + " failed: " + r.Output.Trim());
                return null;
            }
            string last = File.Exists(hashFile) ? File.ReadAllText(hashFile).Trim() : "";
            if (!force && last == hash && Directory.GetFiles(dir, "*.sql.gz").Length > 0)
            {
                File.Delete(part);
                return null;
            }
            File.Move(part, final);
            Util.WriteAtomic(hashFile, hash);
            Prune(dir, Settings.Current.BackupKeep);
            Log.Info("backup", "Backed up " + dbName + " -> " + Path.GetFileName(final));
            return final;
        }

        static void Prune(string dir, int keep)
        {
            var files = new DirectoryInfo(dir).GetFiles("*.sql.gz").OrderByDescending(f => f.Name).ToList();
            foreach (var f in files.Skip(keep)) { try { f.Delete(); } catch { } }
        }

        public static List<FileInfo> ListBackups(string dbName)
        {
            string dir = BackupDirFor(dbName);
            if (!Directory.Exists(dir)) return new List<FileInfo>();
            return new DirectoryInfo(dir).GetFiles("*.sql*").Where(f => !f.Name.EndsWith(".part"))
                .OrderByDescending(f => f.LastWriteTime).ToList();
        }

        public static Dictionary<string, string> LatestSqlBackupPerDatabase()
        {
            var result = new Dictionary<string, string>();
            if (!Directory.Exists(Paths.SqlBackups)) return result;
            foreach (var dir in Directory.GetDirectories(Paths.SqlBackups))
            {
                var f = new DirectoryInfo(dir).GetFiles("*.sql.gz").OrderByDescending(x => x.Name).FirstOrDefault();
                if (f != null) result[Path.GetFileName(dir)] = f.FullName;
            }
            return result;
        }

        public static List<string> SqlBackupsNewerThan(DateTime t)
        {
            var list = new List<string>();
            if (!Directory.Exists(Paths.SqlBackups)) return list;
            foreach (var dir in Directory.GetDirectories(Paths.SqlBackups))
            {
                if (new DirectoryInfo(dir).GetFiles("*.sql.gz").Any(f => f.LastWriteTime > t))
                    list.Add(Path.GetFileName(dir));
            }
            return list;
        }

        /// <summary>Imports a .sql or .sql.gz file. targetDb is used when the dump has no USE statement.</summary>
        public static bool Import(MariaDb db, string file, string targetDb)
        {
            string args = "";
            if (!string.IsNullOrEmpty(targetDb))
            {
                string e;
                db.Exec("CREATE DATABASE IF NOT EXISTS `" + targetDb.Replace("`", "``") + "` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", out e);
                args = Util.Q(targetDb);
            }
            RunResult r;
            using (var fs = File.OpenRead(file))
            using (Stream s = file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? (Stream)new GZipStream(fs, CompressionMode.Decompress) : fs)
            {
                r = db.RunTool(db.Client, args, 6 * 3600 * 1000, s);
            }
            if (!r.Ok) Log.Error("backup", "Import of " + Path.GetFileName(file) + " failed: " + r.Output.Trim());
            else Log.Ok("backup", "Imported " + Path.GetFileName(file));
            return r.Ok;
        }
    }

    /// <summary>Write-through stream that also feeds a hash (to detect unchanged dumps).</summary>
    class HashingStream : Stream
    {
        readonly Stream inner;
        readonly HashAlgorithm hash;
        public HashingStream(Stream inner, HashAlgorithm hash) { this.inner = inner; this.hash = hash; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.TransformBlock(buffer, offset, count, null, 0);
            inner.Write(buffer, offset, count);
        }
        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }
}
