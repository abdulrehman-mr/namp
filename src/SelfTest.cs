using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace Namp
{
    /// <summary>
    /// End-to-end test of the real stack in a scratch folder (process mode, ports 18080/13306):
    /// web + PHP + MariaDB, phpMyAdmin, vhosts, hosts editing, backups, and crash/corruption recovery.
    /// </summary>
    static class SelfTest
    {
        static int fails;

        static void Check(string name, bool ok, string detail = null)
        {
            Console.WriteLine();
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (!ok && detail != null ? "\n      " + detail : ""));
            Console.WriteLine();
            if (!ok) fails++;
        }

        static string Get(string url, string host, out int status)
        {
            status = 0;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                if (host != null) req.Host = host;
                req.Timeout = 30000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    status = (int)resp.StatusCode;
                    return sr.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null) { status = (int)r.StatusCode; using (var sr = new StreamReader(r.GetResponseStream())) return sr.ReadToEnd(); }
                return ex.Message;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        static string Short(string s) { return s.Length > 1500 ? s.Substring(0, 1500) : s; }

        /// <summary>HTTPS request via openssl s_client with SNI + hostname verification against NAMP's CA.</summary>
        static string Tls(string host, string path)
        {
            string req = "GET " + path + " HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n";
            using (var ms = new MemoryStream(Encoding.ASCII.GetBytes(req)))
            {
                var r = Util.Run(Paths.R("apache", "bin", "openssl.exe"),
                    "s_client -connect 127.0.0.1:28443 -servername " + host + " -verify_hostname " + host + " -CAfile " + Util.Q(Ssl.CaCert) +
                    " -verify_return_error -quiet -ign_eof", 30000, null,
                    new Dictionary<string, string> { { "OPENSSL_CONF", Paths.R("config", "ssl", "openssl.cnf") } }, ms);
                return r.Output;
            }
        }

        /// <summary>Builds the chain with Windows CryptoAPI using NAMP's CA (not installed, so UntrustedRoot is the only allowed issue).</summary>
        static bool WindowsChainOk(string certFile, bool verbose)
        {
            if (!File.Exists(certFile)) return false;
            using (var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(certFile))
            using (var ca = new System.Security.Cryptography.X509Certificates.X509Certificate2(Ssl.CaCert))
            {
                var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
                chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                chain.ChainPolicy.ExtraStore.Add(ca);
                foreach (System.Security.Cryptography.X509Certificates.X509Certificate2 c in LoadAll(certFile)) chain.ChainPolicy.ExtraStore.Add(c);
                chain.ChainPolicy.VerificationFlags = System.Security.Cryptography.X509Certificates.X509VerificationFlags.AllowUnknownCertificateAuthority;
                chain.Build(cert);
                var flags = chain.ChainElements.Cast<System.Security.Cryptography.X509Certificates.X509ChainElement>()
                    .SelectMany(e => e.ChainElementStatus).Select(x => x.Status).Distinct().ToList();
                if (verbose) Console.WriteLine("  chain status: " + (flags.Count == 0 ? "none" : string.Join(", ", flags)));
                int n = chain.ChainElements.Count;
                bool rootOk = n == 3 && chain.ChainElements[n - 1].Certificate.Thumbprint == ca.Thumbprint;
                return rootOk && flags.All(f => f == System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.UntrustedRoot);
            }
        }

        static System.Security.Cryptography.X509Certificates.X509Certificate2Collection LoadAll(string pemFile)
        {
            var col = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(pemFile),
                "-----BEGIN CERTIFICATE-----(.*?)-----END CERTIFICATE-----", System.Text.RegularExpressions.RegexOptions.Singleline))
                col.Add(new System.Security.Cryptography.X509Certificates.X509Certificate2(Convert.FromBase64String(m.Groups[1].Value.Trim())));
            return col;
        }

        static string Scalar(string sql)
        {
            string err;
            var rows = Stack.Db.Query(sql, out err);
            if (rows == null) return "ERR " + err;
            return rows.Count > 0 ? rows[0][0] : "";
        }

        public static int Run(string root)
        {
            if (root == null) { Console.WriteLine("usage: --selftest <folder with apache/php/mariadb/phpmyadmin>"); return 2; }
            Paths.Init(root);
            Log.Echo = true;
            Stack.TestMode = true;
            Stack.Privileged = true;
            Stack.Db.UseService = false;
            Stack.Confirm = (t, m) => { Console.WriteLine("  [auto-confirm] " + t); return true; };
            Hosts.HostsPath = Path.Combine(root, "hosts-test.txt");
            File.WriteAllText(Hosts.HostsPath, "# test hosts file\r\n127.0.0.1 localhost\r\n");

            Configs.EnsureDirectories();
            var s = Settings.Load();
            s.ApachePort = 18080;
            s.MySqlPort = 13306;
            s.HttpsPort = 28443;
            s.BackupIntervalMinutes = 0; // the test triggers backups itself
            Settings.Current = s;
            s.Save();

            Stack.Prepare();
            Settings.Current.ApachePort = 18080;
            Settings.Current.MySqlPort = 13306;
            Settings.Current.HttpsPort = 28443;

            // ---------------- 1. MariaDB ----------------
            Check("MariaDB initializes and starts", Stack.StartDb());
            Check("Maintenance account works", Scalar("SELECT CURRENT_USER()").StartsWith("namp_admin"), Scalar("SELECT CURRENT_USER()"));
            Check("phpMyAdmin storage database created", Scalar("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='phpmyadmin'") != "0");
            string err;
            Stack.Db.Exec("CREATE DATABASE IF NOT EXISTS shop; CREATE TABLE IF NOT EXISTS shop.orders (id INT AUTO_INCREMENT PRIMARY KEY, item VARCHAR(100)) ENGINE=InnoDB; INSERT INTO shop.orders (item) VALUES ('alpha'),('beta'),('gamma')", out err);
            Check("Create database + table", err == null, err);

            // ---------------- 2. Apache + PHP ----------------
            Check("Apache starts", Stack.StartApache());
            File.WriteAllText(Path.Combine(Paths.Www, "namp_test.php"),
                "<?php\n$m = new mysqli('localhost', 'root', '', 'shop', 13306);\n" +
                "if ($m->connect_errno) { die('DBERR ' . $m->connect_error); }\n" +
                "$c = $m->query('SELECT COUNT(*) c FROM orders')->fetch_assoc()['c'];\n" +
                "$pdo = new PDO('mysql:host=127.0.0.1;port=13306;dbname=shop', 'root', '');\n" +
                "$exts = ['mbstring','curl','openssl','gd','intl','zip','pdo_mysql','mysqli','sodium','fileinfo'];\n" +
                "$missing = array_filter($exts, fn($e) => !extension_loaded($e));\n" +
                "$ssl = openssl_pkey_new(['private_key_bits' => 2048]) ? 'yes' : 'no';\n" +
                "echo 'OK php=' . PHP_VERSION . ' rows=' . $c . ' missing=' . implode(',', $missing) . ' sapi=' . PHP_SAPI . ' openssl=' . $ssl . ' https=' . ($_SERVER['HTTPS'] ?? 'off');\n");
            int status;
            string body = Get("http://127.0.0.1:18080/namp_test.php", null, out status);
            Check("PHP page connects to MariaDB (mysqli + PDO)", body.StartsWith("OK php=8.") && body.Contains("rows=3"), body);
            Check("PHP extensions all loaded", body.Contains("missing= "), body);
            Check("PHP OpenSSL works inside Apache (shared OpenSSL 3)", body.Contains("openssl=yes"), body);
            body = Get("http://127.0.0.1:18080/phpmyadmin/", null, out status);
            Check("phpMyAdmin opens and is logged in", status == 200 && body.Contains("phpMyAdmin") && !body.Contains("Cannot log in") && !body.Contains("Fatal error"),
                "HTTP " + status + " " + (body.Length > 600 ? body.Substring(0, 600) : body));
            body = Get("http://127.0.0.1:18080/phpmyadmin/index.php?route=/server/databases", null, out status);
            Check("phpMyAdmin lists the new database", body.Contains("shop"), "HTTP " + status);
            // Crawl the main phpMyAdmin screens and look for any PHP notice/deprecation/fatal output.
            var problems = new List<string>();
            foreach (var route in new[] {
                "index.php", "index.php?route=/server/databases", "index.php?route=/server/sql", "index.php?route=/server/status",
                "index.php?route=/server/variables", "index.php?route=/server/privileges", "index.php?route=/server/export",
                "index.php?route=/server/import", "index.php?route=/database/structure&db=shop", "index.php?route=/database/sql&db=shop",
                "index.php?route=/table/structure&db=shop&table=orders", "index.php?route=/sql&db=shop&table=orders&pos=0",
                "index.php?route=/table/change&db=shop&table=orders", "index.php?route=/table/export&db=shop&table=orders" })
            {
                string page = Get("http://127.0.0.1:18080/phpmyadmin/" + route, null, out status);
                foreach (var bad in new[] { "Deprecated", "Fatal error", "Warning</b>", "Notice</b>", "Uncaught", "Parse error" })
                    if (page.Contains(bad) || status >= 500) problems.Add(route + " -> HTTP " + status + " contains '" + bad + "'");
            }
            Check("phpMyAdmin pages have no PHP errors/deprecations (" + Versions.Php + ")", problems.Count == 0, string.Join("\n      ", problems.Distinct()));

            // ---------------- 3. Local domains ----------------
            string addErr = Stack.AddDomain("selftest", Path.Combine(root, "sites", "selftest"));
            Check("Add domain selftest.local", addErr == null, addErr);
            Stack.SyncHosts();
            body = Get("http://127.0.0.1:18080/", "selftest.local", out status);
            Check("Virtual host serves the project folder", body.Contains("selftest.local is running"), body.Length > 300 ? body.Substring(0, 300) : body);
            string hosts = File.ReadAllText(Hosts.HostsPath);
            Check("Hosts file entry written", hosts.Contains("127.0.0.1       selftest.local") && hosts.Contains("127.0.0.1 localhost"), hosts);

            // ---------------- 3b. HTTPS ----------------
            Check("HTTPS is active", Configs.HttpsActive);
            string tls = Tls("selftest.local", "/");
            Check("https://selftest.local serves the site with a valid certificate (OpenSSL verify)",
                tls.Contains("HTTP/1.1 200") && tls.Contains("selftest.local is running") && !tls.Contains("verify error"), Short(tls));
            tls = Tls("www.selftest.local", "/");
            Check("https://www.selftest.local also valid", tls.Contains("HTTP/1.1 200") && !tls.Contains("verify error"), Short(tls));
            tls = Tls("localhost", "/namp_test.php");
            Check("https://localhost runs PHP with HTTPS=on", tls.Contains("OK php=8.") && tls.Contains("https=on"), Short(tls));
            File.WriteAllText(Path.Combine(Paths.Www, "namp_curl.php"),
                "<?php $c = curl_init('https://selftest.local:28443/');\n" +
                "curl_setopt_array($c, [CURLOPT_RETURNTRANSFER => true, CURLOPT_RESOLVE => ['selftest.local:28443:127.0.0.1'], CURLOPT_TIMEOUT => 15]);\n" +
                "$r = curl_exec($c); echo $r === false ? 'CURLERR ' . curl_error($c) : 'CURLOK ' . (strpos($r, 'selftest.local is running') !== false ? 'match' : 'nomatch');\n");
            body = Get("http://127.0.0.1:18080/namp_curl.php", null, out status);
            Check("PHP cURL trusts https://*.local (NAMP CA in PHP's bundle)", body.Contains("CURLOK match"), body);
            tls = Tls("localhost", "/phpmyadmin/");
            Check("phpMyAdmin works over HTTPS", tls.Contains("HTTP/1.1 200") && tls.Contains("phpMyAdmin"), Short(tls));
            Check("Windows chain engine accepts the site certificate", WindowsChainOk(Ssl.CertFile("selftest.local"), true));
            // Safety: even if someone used NAMP's CA to sign a cert for a real site, it must be rejected.
            Ssl.EnsureSiteCert("evil-test", new[] { "www.google.com" }, null);
            var evil = Util.Run(Paths.R("apache", "bin", "openssl.exe"), "verify -CAfile " + Util.Q(Ssl.CaCert) + " -untrusted " + Util.Q(Ssl.CertFile("evil-test")) + " " + Util.Q(Ssl.CertFile("evil-test")), 30000);
            Check("CA cannot be abused for real websites (OpenSSL)", !evil.Ok && evil.Output.Contains("permitted"), evil.Output);
            Console.WriteLine("  note: Windows only evaluates name constraints for an installed (trusted) root - not testable here without admin.");
            File.Delete(Ssl.CertFile("evil-test"));
            File.Delete(Ssl.KeyFile("evil-test"));

            Stack.RemoveDomain("selftest.local");
            Stack.SyncHosts();
            hosts = File.ReadAllText(Hosts.HostsPath);
            Check("Hosts entry removed, other lines kept", !hosts.Contains("selftest.local") && hosts.Contains("127.0.0.1 localhost"), hosts);
            Check("Bad domain rejected", Stack.AddDomain("my shop!", "") != null);
            Check(".dev domain rejected (HSTS)", Stack.AddDomain("x.dev", "") != null);

            // ---------------- 4. Backups ----------------
            int n = Backups.BackupAll(Stack.Db);
            Check("SQL backup written", n >= 1 && Backups.ListBackups("shop").Count == 1);
            n = Backups.BackupAll(Stack.Db);
            Check("Unchanged database is not backed up twice", n == 0 && Backups.ListBackups("shop").Count == 1);
            Stack.Db.Exec("DROP TABLE shop.orders", out err);
            Backups.Import(Stack.Db, Backups.ListBackups("shop")[0].FullName, null);
            Check("Restore from backup brings data back", Scalar("SELECT COUNT(*) FROM shop.orders") == "3");

            // ---------------- 5. Power-loss simulation ----------------
            Stack.Db.Exec("CREATE TABLE shop.ledger (id INT AUTO_INCREMENT PRIMARY KEY, v VARCHAR(200), pad TEXT) ENGINE=InnoDB", out err);
            var values = string.Join(",", Enumerable.Range(0, 50).Select(i => "('row', REPEAT('x', 4000))"));
            int acked = 0;
            bool stop = false;
            var writers = new List<Thread>();
            for (int w = 0; w < 3; w++)
            {
                var t = new Thread(() =>
                {
                    while (!stop)
                    {
                        string e2;
                        if (Stack.Db.Exec("INSERT INTO shop.ledger (v, pad) VALUES " + values, out e2))
                            Interlocked.Add(ref acked, 50);
                        else if (!stop) Console.WriteLine("  insert failed: " + e2);
                    }
                });
                t.IsBackground = true;
                t.Start();
                writers.Add(t);
            }
            Thread.Sleep(6000);
            int pid = Stack.Db.FindPid();
            int ackedAtKill = acked;
            using (var p = Process.GetProcessById(pid)) p.Kill();   // like pulling the plug on the server process
            stop = true;
            foreach (var t in writers) t.Join(20000);
            Console.WriteLine("  killed MariaDB mid-write after " + ackedAtKill + " acknowledged rows");
            Stack.Db.ExpectedRunning = false;
            Check("MariaDB restarts after a hard kill (crash recovery)", Stack.StartDb());
            int count;
            int.TryParse(Scalar("SELECT COUNT(*) FROM shop.ledger"), out count);
            Check("No acknowledged commit was lost (" + count + " rows >= " + ackedAtKill + ")", ackedAtKill > 0 && count >= ackedAtKill);
            Check("Tables are consistent after crash", Scalar("CHECK TABLE shop.ledger") != "" && !Scalar("CHECK TABLE shop.ledger").Contains("ERR"));

            // ---------------- 6. Aria log corruption (classic XAMPP failure) ----------------
            Check("Clean stop + snapshot", Stack.StopDb(true) && Backups.LatestSnapshot() != null);
            File.WriteAllBytes(Path.Combine(Paths.Data, "aria_log_control"), Encoding.ASCII.GetBytes("garbage garbage garbage"));
            Check("Starts again after aria_log_control is corrupted", Stack.StartDb());
            Check("Data intact after Aria recovery", Scalar("SELECT COUNT(*) FROM shop.orders") == "3");
            var chk = Stack.Db.RunTool(Stack.Db.Check, "--all-databases --check", 600000);
            Check("All tables pass CHECK TABLE after Aria recovery", chk.Ok && !chk.Output.Contains("Corrupt") && !chk.Output.Contains("REPAIR") && !chk.Output.Contains("error"), chk.Output);

            // ---------------- 7. Safe-mode rescue rebuild ----------------
            Stack.Db.Stop(60);
            Check("Rescue rebuild (export + clean rebuild) works", Stack.Db.RescueRebuild());
            Check("Data intact after rescue rebuild", Scalar("SELECT COUNT(*) FROM shop.orders") == "3" && Scalar("SELECT CURRENT_USER()").StartsWith("namp_admin"),
                Scalar("SELECT COUNT(*) FROM shop.orders") + " / " + Scalar("SELECT CURRENT_USER()"));

            // ---------------- 8. Destroyed redo log -> snapshot restore ----------------
            Check("Clean stop + snapshot (2)", Stack.StopDb(true));
            string redo = Path.Combine(Paths.Data, "ib_logfile0");
            using (var fs = new FileStream(redo, FileMode.Open, FileAccess.Write))
            {
                var junk = new byte[64 * 1024];
                new Random(1).NextBytes(junk);
                fs.Write(junk, 0, junk.Length);
            }
            Check("Starts again after the redo log is destroyed", Stack.StartDb());
            Check("Data intact after destroyed redo log", Scalar("SELECT COUNT(*) FROM shop.orders") == "3", Scalar("SELECT COUNT(*) FROM shop.orders"));

            // ---------------- 9. Shutdown ----------------
            Stack.StopAll(false);
            Thread.Sleep(500);
            Check("Everything stopped", !Stack.Apache.IsRunning && !Stack.Db.IsRunning);
            DateTime? when;
            Check("Clean shutdown recorded", MariaDb.ReadState(out when) == "stopped");

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "ALL TESTS PASSED" : fails + " TEST(S) FAILED");
            return fails == 0 ? 0 : 1;
        }

        /// <summary>Renders every page of the real UI (off-screen) to PNG files, using the test folder.</summary>
        public static int Preview(string root, string outDir, bool demo = false)
        {
            MainForm.Demo = demo;
            Paths.Init(root);
            Directory.CreateDirectory(outDir);
            Stack.TestMode = true;
            Stack.Privileged = true;
            Stack.Db.UseService = false;
            Hosts.HostsPath = Path.Combine(root, "hosts-test.txt");
            var s = Settings.Load();
            s.Domains.Clear();
            foreach (var d in new[] { "shop.local", "blog.test", "client-portal.local" })
            {
                string dir = Path.Combine(root, "sites", d.Split('.')[0]);
                Directory.CreateDirectory(dir);
                s.Domains.Add(new DomainEntry { Domain = d, Root = dir });
            }
            s.Save();

            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm();
            form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-5000, -5000);
            form.ShowInTaskbar = false;
            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            int ticks = 0;
            timer.Tick += (o, e) =>
            {
                ticks++;
                if (ticks < 14 || Stack.Busy) return;
                if (demo && ticks == 14)
                {
                    // Display what a normal install shows; the servers themselves keep running on test ports.
                    Settings.Current.ApachePort = 80; Settings.Current.HttpsPort = 443; Settings.Current.MySqlPort = 3306;
                    form.DemoActivity(new[] {
                        new[] { "09:41:02", "info", "NAMP 1.0.0 starting." },
                        new[] { "09:41:02", "info", "NAMP folder: C:\\NAMP" },
                        new[] { "09:41:03", "info", "Crash protection: durable commits ON, doublewrite ON, last-good snapshot: today 09:12, last clean DB shutdown: today 09:12" },
                        new[] { "09:41:03", "info", "Starting MariaDB on port 3306 (Windows service NAMP-MariaDB)..." },
                        new[] { "09:41:05", "ok", "MariaDB is running on port 3306." },
                        new[] { "09:41:06", "ok", "Apache 2.4.71 with PHP 8.5.13 is running on port 80 and HTTPS port 443." },
                        new[] { "09:47:30", "info", "Setting up shop.local (hosts file + HTTPS certificate)..." },
                        new[] { "09:47:31", "ok", "Created https://shop.local/ -> D:\\Projects\\shop" },
                        new[] { "10:41:06", "info", "SQL backup finished: 2 changed database(s) saved, 4 unchanged." } });
                    ticks++;
                    return;
                }
                timer.Stop();
                foreach (var page in new[] { "overview", "sites", "databases", "logs", "settings" })
                {
                    form.ShowPage(page);
                    for (int i = 0; i < 20; i++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(100); }
                    using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height))
                    {
                        // PrintWindow renders every control (incl. rich text boxes) exactly as on screen.
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            IntPtr hdc = g.GetHdc();
                            bool ok = PrintWindow(form.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */);
                            g.ReleaseHdc(hdc);
                            if (!ok) form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                        }
                        bmp.Save(Path.Combine(outDir, page + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                Console.WriteLine("Live console characters: " + form.ConsoleChars + Environment.NewLine + form.ConsoleText);
                form.Close();
            };
            timer.Start();
            System.Windows.Forms.Application.Run(form);
            Console.WriteLine("Preview written to " + outDir + "; services running after close: apache=" + Stack.Apache.IsRunning + " db=" + Stack.Db.IsRunning);
            return 0;
        }
    }
}
