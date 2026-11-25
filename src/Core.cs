using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Namp
{
    // =====================================================================
    // Paths: everything lives under the install folder (default C:\NAMP)
    // =====================================================================
    static class Paths
    {
        public static string Root;

        public static void Init(string root)
        {
            Root = Path.GetFullPath(root).TrimEnd('\\');
        }

        public static string R(params string[] parts)
        {
            string p = Root;
            foreach (var s in parts) p = Path.Combine(p, s);
            return p;
        }

        /// <summary>Forward-slash path for Apache / PHP / MariaDB config files.</summary>
        public static string Fwd(string p) { return p.Replace('\\', '/'); }

        public static string Apache { get { return R("apache"); } }
        public static string HttpdExe { get { return R("apache", "bin", "httpd.exe"); } }
        public static string HttpdConf { get { return R("apache", "conf", "httpd.conf"); } }
        public static string NampConfDir { get { return R("apache", "conf", "namp"); } }
        public static string NampPages { get { return R("apache", "namp-pages"); } }
        /// <summary>Active PHP build: php\8.5 or php\8.3 (chosen in Settings).</summary>
        public static string Php { get { return R("php", Settings.Current.PhpVersion ?? "8.5"); } }
        public static string PhpExe { get { return Path.Combine(Php, "php.exe"); } }
        public static string PhpIni { get { return Path.Combine(Php, "php.ini"); } }
        public static string MariaDb { get { return R("mariadb"); } }
        public static string Pma { get { return R("phpmyadmin"); } }
        public static string Config { get { return R("config"); } }
        /// <summary>Admin-only folder: files the MariaDB service reads (written by the NAMP Helper).</summary>
        public static string ServiceConfig { get { return R("config", "service"); } }
        public static string MyIni { get { return R("config", "service", "my.ini"); } }
        public static string MyCustomIni { get { return R("config", "service", "my-custom.ini"); } }
        public static string ClientCnf { get { return R("config", "client.cnf"); } }
        public static string ClientRootCnf { get { return R("config", "client-root.cnf"); } }
        public static string SettingsFile { get { return R("config", "settings.json"); } }
        public static string DbStateFile { get { return R("config", "mariadb.state"); } }
        public static string BackupStateFile { get { return R("config", "backup.state"); } }
        public static string Data { get { return R("data", "mariadb"); } }
        public static string Www { get { return R("www"); } }
        public static string Logs { get { return R("logs"); } }
        public static string SiteLogs { get { return R("logs", "sites"); } }
        public static string ApacheErrorLog { get { return R("logs", "apache-error.log"); } }
        public static string ApacheAccessLog { get { return R("logs", "apache-access.log"); } }
        public static string MariaErrorLog { get { return R("logs", "mariadb-error.log"); } }
        public static string PhpErrorLog { get { return R("logs", "php-error.log"); } }
        public static string AppLog { get { return R("logs", Log.FileName); } }
        public static string Backups { get { return R("backups"); } }
        public static string Snapshots { get { return R("backups", "snapshots"); } }
        public static string SqlBackups { get { return R("backups", "sql"); } }
        public static string Rescue { get { return R("backups", "rescue"); } }
        public static string Quarantine { get { return R("backups", "quarantine"); } }
        public static string Tmp { get { return R("tmp"); } }

        public static string MariaBin(string preferred, string fallback)
        {
            string p = R("mariadb", "bin", preferred);
            if (File.Exists(p)) return p;
            return R("mariadb", "bin", fallback);
        }
    }

    // =====================================================================
    // Log: console in the UI + logs\namp.log
    // =====================================================================
    enum LogLevel { Info, Ok, Warn, Error }

    class LogEntry
    {
        public DateTime Time;
        public string Tag;
        public string Message;
        public LogLevel Level;
    }

    static class Log
    {
        public static string FileName = "namp.log";
        public static event Action<LogEntry> Added;
        public static bool Echo;
        static readonly object Sync = new object();
        static readonly List<LogEntry> recent = new List<LogEntry>();

        public static List<LogEntry> Recent()
        {
            lock (Sync) return new List<LogEntry>(recent);
        }

        public static void Info(string tag, string msg) { Write(tag, msg, LogLevel.Info); }
        public static void Ok(string tag, string msg) { Write(tag, msg, LogLevel.Ok); }
        public static void Warn(string tag, string msg) { Write(tag, msg, LogLevel.Warn); }
        public static void Error(string tag, string msg) { Write(tag, msg, LogLevel.Error); }

        public static void Write(string tag, string msg, LogLevel level)
        {
            var e = new LogEntry { Time = DateTime.Now, Tag = tag, Message = msg ?? "", Level = level };
            lock (Sync)
            {
                recent.Add(e);
                if (recent.Count > 1500) recent.RemoveRange(0, 300);
                try
                {
                    if (Paths.Root != null)
                    {
                        string f = Paths.AppLog;
                        Directory.CreateDirectory(Path.GetDirectoryName(f));
                        var fi = new FileInfo(f);
                        if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                        {
                            string old = Path.Combine(fi.DirectoryName, Path.GetFileNameWithoutExtension(f) + ".1.log");
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(f, old);
                        }
                        File.AppendAllText(f, e.Time.ToString("yyyy-MM-dd HH:mm:ss") + " [" + tag + "] " + level.ToString().ToUpper() + ": " + e.Message + Environment.NewLine, Util.Utf8);
                    }
                }
                catch { }
            }
            if (Echo)
            {
                try { Console.WriteLine("  " + e.Time.ToString("HH:mm:ss") + " [" + tag + "] " + e.Message); } catch { }
            }
            var h = Added;
            if (h != null)
            {
                try { h(e); } catch { }
            }
        }
    }

    // =====================================================================
    // Settings (config\settings.json, written atomically with a .bak copy)
    // =====================================================================
    public class DomainEntry
    {
        public string Domain { get; set; }
        public string Root { get; set; }
    }

    public class Settings
    {
        public int ApachePort { get; set; }
        public int HttpsPort { get; set; }
        public bool EnableHttps { get; set; }
        public string PhpVersion { get; set; }
        public int MySqlPort { get; set; }
        public bool LanAccess { get; set; }
        public bool AutoStart { get; set; }
        public bool SnapshotOnStop { get; set; }
        public bool AutoRestart { get; set; }
        public int BackupIntervalMinutes { get; set; }
        public int BackupKeep { get; set; }
        public string PmaSecret { get; set; }
        public string AdminPassword { get; set; }
        public List<DomainEntry> Domains { get; set; }

        public Settings()
        {
            ApachePort = 80;
            HttpsPort = 443;
            EnableHttps = true;
            PhpVersion = "8.5";
            MySqlPort = 3306;
            AutoStart = true;
            SnapshotOnStop = true;
            AutoRestart = true;
            BackupIntervalMinutes = 60;
            BackupKeep = 10;
            Domains = new List<DomainEntry>();
        }

        public static Settings Current = new Settings();

        public static Settings Load()
        {
            foreach (var f in new[] { Paths.SettingsFile, Paths.SettingsFile + ".bak" })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(f, Util.Utf8));
                    if (s == null) continue;
                    if (f.EndsWith(".bak")) Log.Warn("main", "settings.json was damaged; restored the previous copy.");
                    s.Normalize();
                    return s;
                }
                catch { }
            }
            var n = new Settings();
            n.Normalize();
            return n;
        }

        void Normalize()
        {
            if (Domains == null) Domains = new List<DomainEntry>();
            Domains.RemoveAll(d => d == null || string.IsNullOrEmpty(d.Domain) || string.IsNullOrEmpty(d.Root));
            if (string.IsNullOrEmpty(PmaSecret) || PmaSecret.Length != 32) PmaSecret = Util.RandomString(32);
            if (string.IsNullOrEmpty(AdminPassword) || AdminPassword.Length < 16) AdminPassword = Util.RandomString(24);
            if (ApachePort < 1 || ApachePort > 65535) ApachePort = 80;
            if (MySqlPort < 1 || MySqlPort > 65535) MySqlPort = 3306;
            if (HttpsPort < 1 || HttpsPort > 65535) HttpsPort = 443;
            if (PhpVersion != "8.5" && PhpVersion != "8.3") PhpVersion = "8.5";
            if (BackupIntervalMinutes < 0) BackupIntervalMinutes = 60;
            if (BackupKeep < 1) BackupKeep = 10;
        }

        public void Save()
        {
            string json = new JavaScriptSerializer().Serialize(this);
            Util.WriteAtomic(Paths.SettingsFile, Util.PrettyJson(json), Paths.SettingsFile + ".bak");
        }

        public string BaseUrl
        {
            get { return "http://localhost" + (ApachePort == 80 ? "" : ":" + ApachePort); }
        }

        public string UrlFor(string domain)
        {
            if (Configs.HttpsActive) return "https://" + domain + (HttpsPort == 443 ? "" : ":" + HttpsPort) + "/";
            return "http://" + domain + (ApachePort == 80 ? "" : ":" + ApachePort) + "/";
        }
    }

    // =====================================================================
    // Util
    // =====================================================================
    class RunResult
    {
        public int ExitCode;
        public string Output = "";
        public bool TimedOut;
        public bool Ok { get { return ExitCode == 0 && !TimedOut; } }
    }

    static class Util
    {
        public static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static string Q(string s) { return "\"" + s + "\""; }

        /// <summary>
        /// Power-loss safe write: data goes to a temp file which is flushed to the
        /// physical disk, then atomically swapped in. The old file is either fully
        /// there or fully replaced, never half written.
        /// </summary>
        public static void WriteAtomic(string path, string content, string backupPath = null)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".namp-tmp";
            byte[] bytes = Utf8.GetBytes(content);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, backupPath, true);
                    return;
                }
                catch
                {
                    // e.g. hosts file locked/attributes set by security software
                    File.WriteAllBytes(path, bytes);
                    try { File.Delete(tmp); } catch { }
                    return;
                }
            }
            File.Move(tmp, path);
        }

        public static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Utf8, true))
                return sr.ReadToEnd();
        }

        public static long FileSize(string path)
        {
            try { var fi = new FileInfo(path); return fi.Exists ? fi.Length : 0; }
            catch { return 0; }
        }

        public static string ReadFrom(string path, long offset)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (offset > fs.Length) offset = 0;
                    fs.Seek(offset, SeekOrigin.Begin);
                    using (var sr = new StreamReader(fs, Utf8)) return sr.ReadToEnd();
                }
            }
            catch { return ""; }
        }

        public static List<string> TailLines(string path, int maxLines)
        {
            var result = new List<string>();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long max = 3L * 1024 * 1024;
                    if (fs.Length > max) fs.Seek(fs.Length - max, SeekOrigin.Begin);
                    using (var sr = new StreamReader(fs, Utf8))
                    {
                        string text = sr.ReadToEnd();
                        var lines = text.Replace("\r\n", "\n").Split('\n');
                        int start = Math.Max(0, lines.Length - maxLines);
                        for (int i = start; i < lines.Length; i++)
                            if (i < lines.Length - 1 || lines[i].Length > 0) result.Add(lines[i]);
                    }
                }
            }
            catch { }
            return result;
        }

        public static RunResult Run(string exe, string args, int timeoutMs, string workDir = null,
            IDictionary<string, string> env = null, Stream stdin = null, Stream stdout = null)
        {
            var res = new RunResult();
            var psi = new ProcessStartInfo(exe, args ?? "");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = stdin != null;
            psi.StandardErrorEncoding = Utf8;
            if (stdout == null) psi.StandardOutputEncoding = Utf8;
            if (workDir != null) psi.WorkingDirectory = workDir;
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;

            var sb = new StringBuilder();
            var sync = new object();
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sync) sb.AppendLine(e.Data); };
                if (stdout == null)
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sync) sb.AppendLine(e.Data); };
                if (stdin != null)
                {
                    try { Console.InputEncoding = Utf8; } catch { } // no BOM in front of piped data
                }
                try { p.Start(); }
                catch (Exception ex)
                {
                    res.ExitCode = -1;
                    res.Output = "Could not run " + exe + ": " + ex.Message;
                    return res;
                }
                p.BeginErrorReadLine();
                Thread outThread = null;
                if (stdout == null) p.BeginOutputReadLine();
                else
                {
                    outThread = new Thread(() => { try { p.StandardOutput.BaseStream.CopyTo(stdout, 1 << 16); } catch { } });
                    outThread.IsBackground = true;
                    outThread.Start();
                }
                if (stdin != null)
                {
                    var inThread = new Thread(() =>
                    {
                        try { stdin.CopyTo(p.StandardInput.BaseStream, 1 << 16); } catch { }
                        finally { try { p.StandardInput.Close(); } catch { } }
                    });
                    inThread.IsBackground = true;
                    inThread.Start();
                }
                if (!p.WaitForExit(timeoutMs))
                {
                    res.TimedOut = true;
                    try { p.Kill(); } catch { }
                    p.WaitForExit(5000);
                }
                else p.WaitForExit();
                if (outThread != null) outThread.Join(15000);
                res.ExitCode = res.TimedOut ? -1 : p.ExitCode;
            }
            lock (sync) res.Output = sb.ToString();
            return res;
        }

        // ---------------- Networking ----------------
        public static bool IsPortListening(int port)
        {
            try { return Native.GetTcpListeners().Any(l => l.Port == port); }
            catch { return false; }
        }

        public static int PortOwnerPid(int port)
        {
            try
            {
                foreach (var l in Native.GetTcpListeners()) if (l.Port == port) return l.Pid;
            }
            catch { }
            return 0;
        }

        public static string DescribePortOwner(int port)
        {
            int pid = PortOwnerPid(port);
            if (pid <= 0) return "another program";
            if (pid == 4) return "Windows itself (PID 4 - usually IIS / \"World Wide Web Publishing Service\" or another HTTP.sys user)";
            string name = "PID " + pid;
            try { using (var p = Process.GetProcessById(pid)) name = p.ProcessName + ".exe (PID " + pid + ")"; } catch { }
            string path = Native.GetProcessPath(pid);
            if (!string.IsNullOrEmpty(path)) name += " - " + path;
            return name;
        }

        public static bool CanConnect(int port, int timeoutMs = 700)
        {
            try
            {
                using (var c = new TcpClient(AddressFamily.InterNetwork))
                {
                    var ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        static bool? ipv6;
        public static bool Ipv6LoopbackAvailable()
        {
            if (ipv6.HasValue) return ipv6.Value;
            try
            {
                using (var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp))
                {
                    s.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
                    ipv6 = true;
                }
            }
            catch { ipv6 = false; }
            return ipv6.Value;
        }

        // ---------------- Misc ----------------
        public static string RandomString(int len)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
            var data = new byte[len];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(data);
            var sb = new StringBuilder(len);
            foreach (var b in data) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        public static string RandomHex(int bytes)
        {
            var data = new byte[bytes];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(data);
            data[0] &= 0x7F; // keep certificate serial numbers positive
            return BitConverter.ToString(data).Replace("-", "");
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
        }

        public static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + " h ago";
            return t.ToString("yyyy-MM-dd HH:mm");
        }

        /// <summary>robocopy wrapper. Returns true on success (robocopy exit codes 0-7).</summary>
        public static bool Robocopy(string src, string dst, out string output)
        {
            Directory.CreateDirectory(dst);
            var r = Run("robocopy.exe", Q(src) + " " + Q(dst) + " /MIR /COPY:DAT /DCOPY:T /R:2 /W:1 /NFL /NDL /NJH /NP /XJ", 6 * 3600 * 1000);
            output = r.Output;
            return !r.TimedOut && r.ExitCode >= 0 && r.ExitCode < 8;
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("main", "Could not open " + url + ": " + ex.Message); }
        }

        public static void OpenPath(string path)
        {
            try
            {
                if (Directory.Exists(path)) Process.Start("explorer.exe", Q(path));
                else Process.Start("notepad.exe", Q(path));
            }
            catch (Exception ex) { Log.Error("main", "Could not open " + path + ": " + ex.Message); }
        }

        public static string PrettyJson(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inStr = !inStr;
                if (inStr) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{':
                    case '[':
                        sb.Append(c).Append("\r\n").Append(new string(' ', ++indent * 2));
                        break;
                    case '}':
                    case ']':
                        sb.Append("\r\n").Append(new string(' ', --indent * 2)).Append(c);
                        break;
                    case ',':
                        sb.Append(c).Append("\r\n").Append(new string(' ', indent * 2));
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string StateTime()
        {
            return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        }

        public static DateTime? ParseStateTime(string s)
        {
            DateTime t;
            if (DateTime.TryParseExact(s, "yyyy-MM-ddTHH:mm:ss", null, System.Globalization.DateTimeStyles.None, out t)) return t;
            return null;
        }
    }
}
