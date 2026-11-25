using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Namp
{
    // =====================================================================
    // Apache (+ PHP, which runs inside Apache as php8apache2_4.dll)
    // =====================================================================
    class ApacheServer
    {
        Process proc;
        IntPtr job = IntPtr.Zero;
        public volatile bool ExpectedRunning;

        int Port { get { return Settings.Current.ApachePort; } }

        public List<int> Pids()
        {
            var list = new List<int>();
            string exe = Path.GetFullPath(Paths.HttpdExe);
            foreach (var p in Process.GetProcessesByName("httpd"))
            {
                using (p)
                {
                    string path = Native.GetProcessPath(p.Id);
                    if (path != null && string.Equals(Path.GetFullPath(path), exe, StringComparison.OrdinalIgnoreCase))
                        list.Add(p.Id);
                }
            }
            list.Sort();
            return list;
        }

        public bool IsRunning { get { return Pids().Count > 0; } }

        Dictionary<string, string> Env()
        {
            string path = Paths.Php + ";" + Path.Combine(Paths.Apache, "bin") + ";" + Environment.GetEnvironmentVariable("PATH");
            return new Dictionary<string, string> {
                { "PATH", path }, { "PHPRC", Paths.Php }, { "TMP", Paths.Tmp }, { "TEMP", Paths.Tmp },
                // PHP's openssl_pkey_new()/CSR functions need an openssl.cnf on Windows
                { "OPENSSL_CONF", Path.Combine(Paths.Php, "extras", "ssl", "openssl.cnf") }
            };
        }

        string Args { get { return "-d " + Util.Q(Paths.Apache) + " -f " + Util.Q(Paths.HttpdConf); } }

        /// <summary>Returns null if the configuration is valid, else Apache's error text.</summary>
        public string TestConfig()
        {
            var r = Util.Run(Paths.HttpdExe, Args + " -t", 30000, Path.Combine(Paths.Apache, "bin"), Env());
            return r.Ok ? null : r.Output.Trim();
        }

        public bool Start()
        {
            if (IsRunning) { ExpectedRunning = true; return true; }
            if (Util.IsPortListening(Port))
            {
                Log.Error("apache", "Port " + Port + " is already in use by " + Util.DescribePortOwner(Port) +
                    ". Stop that program (e.g. XAMPP's Apache, IIS or Skype) or change the Apache port in Settings.");
                return false;
            }
            var cfg = Settings.Current;
            Configs.HttpsActive = false;
            if (cfg.EnableHttps)
            {
                if (Util.IsPortListening(cfg.HttpsPort))
                    Log.Warn("apache", "HTTPS port " + cfg.HttpsPort + " is in use by " + Util.DescribePortOwner(cfg.HttpsPort) +
                        " - starting with HTTP only. Close that program or change the HTTPS port in Settings.");
                else if (Stack.Privileged ? Ssl.EnsureCa() && Ssl.EnsureSiteCert("localhost", new[] { "localhost" }, new[] { "127.0.0.1", "::1" })
                                    : Helper.WaitFor(() => Ssl.HasSiteCert("localhost", new[] { "localhost" }), 20, "preparing HTTPS"))
                    Configs.HttpsActive = true;
                else Log.Warn("apache", "HTTPS certificates could not be prepared - starting with HTTP only.");
            }
            Configs.WriteApacheIncludes();
            try { Configs.WritePhpIni(); } catch (Exception ex) { Log.Warn("php", "php.ini: " + ex.Message); } // picks up NAMP's CA bundle
            string err = TestConfig();
            if (err != null && Configs.HttpsActive)
            {
                Log.Warn("apache", "HTTPS configuration problem, starting with HTTP only:\n" + err);
                Configs.HttpsActive = false;
                Configs.WriteApacheIncludes();
                err = TestConfig();
            }
            if (err != null)
            {
                Log.Error("apache", "Apache configuration error - not starting:\n" + err);
                return false;
            }

            long logPos = Util.FileSize(Paths.ApacheErrorLog);
            var psi = new ProcessStartInfo(Paths.HttpdExe, Args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = Path.Combine(Paths.Apache, "bin");
            foreach (var kv in Env()) psi.EnvironmentVariables[kv.Key] = kv.Value;

            try
            {
                proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("apache", e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn("apache", e.Data); };
                proc.Start();
                // Tie Apache's lifetime to NAMP: if NAMP is killed, Windows kills Apache too.
                if (job == IntPtr.Zero) job = Native.CreateKillOnCloseJob();
                if (job != IntPtr.Zero) Native.AssignProcessToJobObject(job, proc.Handle);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Log.Error("apache", "Could not launch Apache: " + ex.Message);
                return false;
            }

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 15)
            {
                if (proc.HasExited)
                {
                    Log.Error("apache", "Apache stopped right after starting (exit code " + proc.ExitCode + ").");
                    foreach (var line in Util.ReadFrom(Paths.ApacheErrorLog, logPos).Split('\n').Where(l => l.Trim().Length > 0).Take(10))
                        Log.Error("apache", line.Trim());
                    return false;
                }
                if (Util.CanConnect(Port)) break;
                Thread.Sleep(250);
            }
            ExpectedRunning = true;
            Log.Ok("apache", "Apache " + Versions.Apache + " with PHP " + Versions.Php + " is running on port " + Port +
                (Configs.HttpsActive ? " and HTTPS port " + cfg.HttpsPort : "") + " (PID " + proc.Id + ").");
            return true;
        }

        public void Stop()
        {
            ExpectedRunning = false;
            if (!IsRunning) return;
            Log.Info("apache", "Stopping Apache...");
            if (proc != null && !proc.HasExited)
            {
                Native.SignalEvent("ap" + proc.Id + "_shutdown");
                if (!proc.WaitForExit(10000)) { try { proc.Kill(); } catch { } }
            }
            KillAll();
            Log.Ok("apache", "Apache stopped.");
        }

        /// <summary>Kills any httpd.exe from this NAMP folder (Apache holds no data, so this is safe).</summary>
        public void KillAll()
        {
            for (int round = 0; round < 3; round++)
            {
                var pids = Pids();
                if (pids.Count == 0) return;
                foreach (var pid in pids)
                {
                    try { using (var p = Process.GetProcessById(pid)) { p.Kill(); p.WaitForExit(5000); } } catch { }
                }
            }
        }

        /// <summary>Re-reads the configuration without dropping the process (used after domain changes).</summary>
        public bool Reload()
        {
            if (!IsRunning) return true;
            Configs.WriteApacheIncludes();
            string err = TestConfig();
            if (err != null)
            {
                Log.Error("apache", "New configuration is invalid, keeping the old one:\n" + err);
                return false;
            }
            if (proc != null && !proc.HasExited)
            {
                // Graceful restart: Apache starts a new worker with the new config, then retires the old one.
                var oldWorkers = Pids().Where(p => p != proc.Id).ToList();
                if (Native.SignalEvent("ap" + proc.Id + "_restart"))
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 15 && Pids().Intersect(oldWorkers).Any()) Thread.Sleep(200);
                    if (IsRunning) { Log.Info("apache", "Apache reloaded its configuration."); return true; }
                }
            }
            Stop();
            return Start();
        }
    }

    // =====================================================================
    // Component versions (written by the build script)
    // =====================================================================
    static class Versions
    {
        public static string Apache = "2.4";
        public static string Php85 = "8.5", Php83 = "8.3";
        /// <summary>Full version of the PHP build currently selected in Settings.</summary>
        public static string Php { get { return Settings.Current.PhpVersion == "8.3" ? Php83 : Php85; } }
        public static string MariaDb = "11.8";
        public static string PhpMyAdmin = "5.2";
        public const string Namp = "1.0.0";

        public static void Load()
        {
            try
            {
                string f = Paths.R("versions.json");
                if (!File.Exists(f)) return;
                string json = File.ReadAllText(f);
                Apache = Get(json, "Apache", Apache).Split('-')[0];
                Php85 = Get(json, "Php85", Get(json, "Php", Php85));
                Php83 = Get(json, "Php83", Php83);
                MariaDb = Get(json, "MariaDB", MariaDb);
                PhpMyAdmin = Get(json, "PhpMyAdmin", PhpMyAdmin);
            }
            catch { }
        }

        static string Get(string json, string key, string def)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : def;
        }
    }

    // =====================================================================
    // Stack: orchestrates everything; the UI only talks to this class.
    // =====================================================================
    static class Stack
    {
        public static readonly ApacheServer Apache = new ApacheServer();
        public static readonly MariaDb Db = new MariaDb();
        public static Func<string, string, bool> Confirm = (t, m) => true;
        public static bool TestMode;
        /// <summary>
        /// True when this process may do admin work itself (NAMP Helper service, an elevated NAMP, or the self-test).
        /// A normal NAMP window is not privileged: it asks the NAMP Helper service for hosts/certificate changes.
        /// </summary>
        public static bool Privileged;
        public static volatile bool ShuttingDown;

        static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        static volatile int busyCount;
        public static bool Busy { get { return busyCount > 0; } }
        public static event Action BusyChanged;

        static Timer watchdog;
        static readonly List<DateTime> apacheRestarts = new List<DateTime>();
        static readonly List<DateTime> dbRestarts = new List<DateTime>();
        static volatile bool backupRunning;

        /// <summary>Runs an operation in the background, one at a time.</summary>
        public static Task RunOp(string name, Action action)
        {
            return Task.Run(() =>
            {
                gate.Wait();
                Interlocked.Increment(ref busyCount);
                RaiseBusy();
                try { action(); }
                catch (Exception ex) { Log.Error("main", name + " failed: " + ex.Message); }
                finally
                {
                    Interlocked.Decrement(ref busyCount);
                    gate.Release();
                    RaiseBusy();
                }
            });
        }

        static void RaiseBusy()
        {
            var h = BusyChanged;
            if (h != null) { try { h(); } catch { } }
        }

        // ------------------------------------------------------------------
        public static void Prepare()
        {
            Versions.Load();
            Configs.EnsureDirectories();
            Settings.Current = Settings.Load();
            Settings.Current.Save();
            Configs.WriteAll();
            Log.Info("main", "NAMP folder: " + Paths.Root);

            if (Db.UseService && !TestMode && (!Helper.IsInstalled(MariaDb.ServiceName) || !Helper.IsInstalled(HelperService.Name)))
            {
                if (Confirm("Finish NAMP setup",
                    "NAMP needs to finish a one-time setup: register its database service and the small \"NAMP Helper\" " +
                    "service that manages local domains and HTTPS.\n\nWindows will ask for permission once. After that NAMP runs without administrator rights.\n\nContinue?"))
                    Helper.RunElevatedSetup();
                if (!Helper.IsInstalled(MariaDb.ServiceName))
                {
                    Log.Warn("mariadb", "Setup was not completed - MariaDB will run as a normal process, and local domains/HTTPS are unavailable.");
                    Db.UseService = false;
                }
            }

            // Apache left over from a crashed NAMP session holds no data - just clear it.
            if (Apache.IsRunning)
            {
                Log.Info("apache", "Found Apache from a previous session - restarting it cleanly.");
                Apache.KillAll();
            }
            if (Db.IsRunning)
            {
                Log.Info("mariadb", "MariaDB is already running (PID " + Db.FindPid() + ") - attaching to it.");
                Db.ExpectedRunning = true;
            }

            CheckPhp();
            if (Privileged && !TestMode) SyncHosts();

            var snap = Backups.LatestSnapshot();
            DateTime? when;
            string state = MariaDb.ReadState(out when);
            Log.Info("main", "Crash protection: durable commits ON, doublewrite ON, last-good snapshot: " +
                (snap != null ? snap.Time.ToString("g") : "none yet") + ", last clean DB shutdown: " +
                (state == "stopped" && when.HasValue ? when.Value.ToString("g") : state == "running" ? "NO (will auto-recover)" : "n/a"));

            if (watchdog == null) watchdog = new Timer(_ => Tick(), null, 5000, 5000);
        }

        static void CheckPhp()
        {
            var r = Util.Run(Paths.PhpExe, "-c " + Util.Q(Paths.PhpIni) + " -r \"echo PHP_VERSION;\"", 30000, Paths.Php,
                new Dictionary<string, string> { { "PATH", Paths.Php + ";" + Environment.GetEnvironmentVariable("PATH") } });
            if (!r.Ok) { Log.Warn("php", "PHP self-check failed: " + r.Output.Trim()); return; }
            foreach (var line in r.Output.Split('\n').Where(l => l.Contains("Warning") || l.Contains("Unable to load")))
                Log.Warn("php", line.Trim());
        }

        public static bool StartAll()
        {
            bool db = StartDb();
            bool web = StartApache();
            return db && web;
        }

        public static void StopAll(bool snapshot)
        {
            StopApache();
            StopDb(snapshot);
        }

        public static bool StartApache() { return Apache.Start(); }
        public static void StopApache() { Apache.Stop(); }
        public static bool StartDb() { return Db.Start(Confirm); }

        public static bool StopDb(bool snapshot)
        {
            bool clean = Db.Stop(120);
            if (clean && snapshot && Settings.Current.SnapshotOnStop && !ShuttingDown) Backups.TakeSnapshot();
            return clean;
        }

        /// <summary>
        /// Called when Windows is shutting down / logging off: stop everything immediately
        /// (no snapshot - there is no time), even if another operation is in progress.
        /// </summary>
        public static void EmergencyStop()
        {
            ShuttingDown = true;
            try { Apache.KillAll(); } catch { }
            try { Db.Stop(25); } catch { }
        }

        // ------------------------------------------------------------------
        // Watchdog: notices crashes and restarts services, runs scheduled backups
        // ------------------------------------------------------------------
        static void Tick()
        {
            if (ShuttingDown || Busy) return;
            try
            {
                if (Db.ExpectedRunning && !Db.IsRunning)
                {
                    Db.ExpectedRunning = false;
                    Log.Error("mariadb", "MariaDB stopped unexpectedly!");
                    if (Settings.Current.AutoRestart && Allow(dbRestarts))
                        RunOp("restart MariaDB", () => { Log.Info("mariadb", "Restarting MariaDB automatically..."); StartDb(); });
                }
                if (Apache.ExpectedRunning && !Apache.IsRunning)
                {
                    Apache.ExpectedRunning = false;
                    Log.Error("apache", "Apache stopped unexpectedly!");
                    foreach (var l in Util.TailLines(Paths.ApacheErrorLog, 5)) Log.Error("apache", l);
                    if (Settings.Current.AutoRestart && Allow(apacheRestarts))
                        RunOp("restart Apache", () => { Log.Info("apache", "Restarting Apache automatically..."); StartApache(); });
                }

                int interval = Settings.Current.BackupIntervalMinutes;
                if (interval > 0 && !backupRunning && Db.ExpectedRunning && Db.IsRunning)
                {
                    var last = Backups.LastBackupRun();
                    if (!last.HasValue || (DateTime.Now - last.Value).TotalMinutes >= interval)
                    {
                        backupRunning = true;
                        Task.Run(() =>
                        {
                            try { Backups.BackupAll(Db); }
                            catch (Exception ex) { Log.Warn("backup", ex.Message); }
                            finally { backupRunning = false; }
                        });
                    }
                }
            }
            catch { }
        }

        static bool Allow(List<DateTime> history)
        {
            history.RemoveAll(t => (DateTime.Now - t).TotalMinutes > 10);
            if (history.Count >= 3)
            {
                Log.Error("main", "Not restarting automatically again (3 crashes in 10 minutes). Check the Logs page.");
                return false;
            }
            history.Add(DateTime.Now);
            return true;
        }

        // ------------------------------------------------------------------
        // Local domains
        // ------------------------------------------------------------------
        static readonly string[] HstsTlds = { "dev", "app", "page", "new", "day", "foo", "zip", "mov", "bank", "insurance" };

        public static string NormalizeDomain(string input, out string error)
        {
            error = null;
            string d = (input ?? "").Trim().ToLowerInvariant();
            d = Regex.Replace(d, "^[a-z]+://", "");
            d = d.Split('/')[0].Split(':')[0].Trim('.');
            if (d.StartsWith("www.")) d = d.Substring(4);
            if (d.Length == 0) { error = "Enter a domain name, e.g. myproject.local"; return null; }
            if (!d.Contains(".")) d += ".local";
            if (!Regex.IsMatch(d, "^(?=.{1,200}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$"))
            {
                error = "\"" + d + "\" is not a valid domain. Use letters, digits and hyphens, e.g. my-shop.local";
                return null;
            }
            string tld = d.Substring(d.LastIndexOf('.') + 1);
            if (d == "localhost" || d.EndsWith(".localhost") && d.Length < 11) { error = "Pick another name."; return null; }
            if (HstsTlds.Contains(tld) || !Ssl.IsLocalTld(d))
            {
                error = "\"" + d + "\" uses a real internet ending (." + tld + "), which would hide the real website on this PC.\n" +
                        "Use a local ending instead: ." + string.Join(", .", Ssl.LocalTlds) + "  (e.g. myshop.local)";
                return null;
            }
            return d;
        }

        public static string AddDomain(string input, string root)
        {
            string err;
            string domain = NormalizeDomain(input, out err);
            if (domain == null) return err;
            if (Settings.Current.Domains.Any(x => x.Domain == domain)) return domain + " already exists.";

            root = (root ?? "").Trim().Trim('"');
            if (root.Length == 0) root = Path.Combine(Paths.Www, domain.Substring(0, domain.LastIndexOf('.')));
            if (!Path.IsPathRooted(root)) return "Choose a full folder path, e.g. D:\\Projects\\myshop";
            root = Path.GetFullPath(root);
            try
            {
                Directory.CreateDirectory(root);
                if (!Directory.EnumerateFileSystemEntries(root).Any())
                    File.WriteAllText(Path.Combine(root, "index.php"), Configs.WelcomePage(domain), Util.Utf8);
            }
            catch (Exception ex) { return "Cannot use that folder: " + ex.Message; }

            Settings.Current.Domains.Add(new DomainEntry { Domain = domain, Root = root });
            Settings.Current.Domains.Sort((a, b) => string.Compare(a.Domain, b.Domain, StringComparison.Ordinal));
            Settings.Current.Save();
            WaitForHelper(domain);
            ApplyDomains();
            Log.Ok("domains", "Created " + Settings.Current.UrlFor(domain) + " -> " + root);
            return null;
        }

        public static string ChangeDomainRoot(string domain, string root)
        {
            var d = Settings.Current.Domains.FirstOrDefault(x => x.Domain == domain);
            if (d == null) return "Domain not found.";
            if (!Directory.Exists(root)) return "Folder does not exist.";
            d.Root = Path.GetFullPath(root);
            Settings.Current.Save();
            ApplyDomains();
            Log.Ok("domains", domain + " now serves " + d.Root);
            return null;
        }

        public static void RemoveDomain(string domain)
        {
            Settings.Current.Domains.RemoveAll(x => x.Domain == domain);
            Settings.Current.Save();
            ApplyDomains();
            Log.Ok("domains", "Removed " + domain + " (its project folder was not touched).");
        }

        public static void ApplyDomains()
        {
            Configs.WriteVhosts();
            if (Privileged && !TestMode) SyncHosts();
            if (Apache.IsRunning) Apache.Reload();
        }

        /// <summary>Normal (unprivileged) NAMP: wait until the Helper has added the hosts entry and certificate.</summary>
        static void WaitForHelper(string domain)
        {
            if (Privileged) return;
            Log.Info("domains", "Setting up " + domain + " (hosts file" + (Settings.Current.EnableHttps ? " + HTTPS certificate" : "") + ")...");
            Helper.WaitFor(() => Helper.HostsHas(domain) &&
                (!Settings.Current.EnableHttps || Ssl.HasSiteCert(domain, new[] { domain, "www." + domain })), 30, "setting up " + domain);
        }

        public static void SyncHosts()
        {
            string err;
            if (!Hosts.Sync(Settings.Current.Domains.Select(d => d.Domain), out err))
                Log.Error("domains", "Could not update the Windows hosts file: " + err +
                    " (NAMP must run as administrator; some antivirus tools also lock this file).");
        }
    }
}
