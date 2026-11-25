using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Namp
{
    /// <summary>Generates every configuration file the stack needs.</summary>
    static class Configs
    {
        const string HttpdMarker = "# NAMP-MANAGED-HTTPD";

        public static void EnsureDirectories()
        {
            foreach (var d in new[] {
                Paths.Config, Path.GetDirectoryName(Paths.Data), Paths.Www, Paths.Logs, Paths.SiteLogs,
                Paths.Backups, Paths.Snapshots, Paths.SqlBackups, Paths.Rescue, Paths.Quarantine,
                Paths.Tmp, Path.Combine(Paths.Tmp, "sessions"), Path.Combine(Paths.Tmp, "uploads"),
                Path.Combine(Paths.Tmp, "phpmyadmin"), Paths.NampConfDir, Paths.NampPages })
            {
                Directory.CreateDirectory(d);
            }
        }

        public static void WriteAll()
        {
            EnsureDirectories();
            WriteHttpdConf();
            WriteApacheIncludes();
            WritePhpIni();
            if (Stack.Privileged) TryWriteMyIni();
            WriteClientCnf();
            WritePmaConfig();
            WriteInternalPages();
            EnsureWwwIndex();
        }

        static void WriteIfChanged(string path, string content)
        {
            try
            {
                if (File.Exists(path) && Util.ReadShared(path) == content) return;
            }
            catch { }
            Util.WriteAtomic(path, content);
        }

        // ------------------------------------------------------------------
        // Apache
        // ------------------------------------------------------------------
        static readonly string[] Modules = {
            "access_compat", "actions", "alias", "allowmethods", "asis", "auth_basic", "authn_core", "authn_file",
            "authz_core", "authz_groupfile", "authz_host", "authz_user", "autoindex", "cgi", "deflate", "dir", "env",
            "expires", "filter", "headers", "include", "log_config", "mime", "negotiation", "rewrite", "setenvif",
            "socache_shmcb", "version"
        };

        public static void WriteHttpdConf()
        {
            string root = Paths.Fwd(Paths.Root);
            string existing = File.Exists(Paths.HttpdConf) ? Util.ReadShared(Paths.HttpdConf) : null;
            if (existing == null || !existing.Contains(HttpdMarker))
            {
                if (existing != null)
                {
                    string keep = Paths.HttpdConf + ".original";
                    if (!File.Exists(keep)) File.Copy(Paths.HttpdConf, keep);
                }
                Util.WriteAtomic(Paths.HttpdConf, HttpdTemplate(root));
                return;
            }
            string updated = Regex.Replace(existing, "^Define NAMP_ROOT \"[^\"]*\"",
                m => "Define NAMP_ROOT \"" + root + "\"", RegexOptions.Multiline);
            if (updated != existing) Util.WriteAtomic(Paths.HttpdConf, updated);
        }

        static string HttpdTemplate(string root)
        {
            var sb = new StringBuilder();
            sb.AppendLine(HttpdMarker);
            sb.AppendLine("# Apache configuration for NAMP. You may edit this file.");
            sb.AppendLine("# NAMP only updates the \"Define NAMP_ROOT\" line (if the NAMP folder moves).");
            sb.AppendLine("# Ports, PHP, phpMyAdmin and your local domains are in conf/namp/*.conf,");
            sb.AppendLine("# which NAMP regenerates automatically - change those from the NAMP app.");
            sb.AppendLine();
            sb.AppendLine("Define NAMP_ROOT \"" + root + "\"");
            sb.AppendLine("ServerRoot \"${NAMP_ROOT}/apache\"");
            sb.AppendLine("Include \"${NAMP_ROOT}/apache/conf/namp/ports.conf\"");
            sb.AppendLine();
            foreach (var m in Modules)
            {
                if (File.Exists(Path.Combine(Paths.Apache, "modules", "mod_" + m + ".so")))
                    sb.AppendLine("LoadModule " + m + "_module modules/mod_" + m + ".so");
            }
            sb.AppendLine(@"
ServerAdmin admin@localhost
ServerName localhost

<IfModule mpm_winnt_module>
    ThreadsPerChild 150
    MaxConnectionsPerChild 0
</IfModule>
Timeout 300
KeepAlive On
MaxKeepAliveRequests 500
KeepAliveTimeout 5
# Windows-specific settings that avoid hangs with some network drivers/antivirus
AcceptFilter http none
AcceptFilter https none
EnableSendfile off
EnableMMAP off

<Directory />
    AllowOverride none
    Require all denied
</Directory>

DocumentRoot ""${NAMP_ROOT}/www""
<Directory ""${NAMP_ROOT}/www"">
    Options Indexes FollowSymLinks
    AllowOverride All
    Require all granted
</Directory>

<IfModule dir_module>
    DirectoryIndex index.php index.html index.htm default.php default.html
</IfModule>

<Files "".ht*"">
    Require all denied
</Files>

ErrorLog ""${NAMP_ROOT}/logs/apache-error.log""
LogLevel warn

<IfModule log_config_module>
    LogFormat ""%h %l %u %t \""%r\"" %>s %b \""%{Referer}i\"" \""%{User-Agent}i\"""" combined
    LogFormat ""%h %l %u %t \""%r\"" %>s %b"" common
    CustomLog ""${NAMP_ROOT}/logs/apache-access.log"" combined
</IfModule>

<IfModule mime_module>
    TypesConfig conf/mime.types
    AddType application/x-compress .Z
    AddType application/x-gzip .gz .tgz
</IfModule>

<IfModule headers_module>
    RequestHeader unset Proxy early
</IfModule>

Include ""${NAMP_ROOT}/apache/conf/namp/php.conf""
Include ""${NAMP_ROOT}/apache/conf/namp/namp-pages.conf""
Include ""${NAMP_ROOT}/apache/conf/namp/vhosts.conf""

# Add your own directives below this line (or in conf/extra/*.conf and Include them).
");
            return sb.ToString();
        }

        public static void WriteApacheIncludes()
        {
            var s = Settings.Current;
            string root = Paths.Fwd(Paths.Root);
            const string gen = "# Generated by NAMP - do not edit (it is rewritten on every start).\r\n";

            // ports.conf
            var ports = new StringBuilder(gen);
            // Apache's default PID file lives in the (read-only) program folder - keep it with the logs.
            ports.AppendLine("PidFile \"" + root + "/logs/httpd.pid\"");
            var listenPorts = new List<int> { s.ApachePort };
            if (HttpsActive) listenPorts.Add(s.HttpsPort);
            foreach (var port in listenPorts)
            {
                if (s.LanAccess) ports.AppendLine("Listen " + port);
                else
                {
                    ports.AppendLine("Listen 127.0.0.1:" + port);
                    if (Util.Ipv6LoopbackAvailable()) ports.AppendLine("Listen [::1]:" + port);
                }
            }
            if (HttpsActive)
            {
                ports.AppendLine();
                ports.AppendLine("<IfModule !ssl_module>");
                ports.AppendLine("    LoadModule ssl_module modules/mod_ssl.so");
                ports.AppendLine("</IfModule>");
            }
            WriteIfChanged(Path.Combine(Paths.NampConfDir, "ports.conf"), ports.ToString());

            // php.conf (PHP runs inside Apache as a module, exactly like XAMPP)
            var php = new StringBuilder(gen);
            string phpDir = Paths.Fwd(Paths.Php);
            php.AppendLine("PHPIniDir \"" + phpDir + "\"");
            // OpenSSL (libcrypto/libssl) comes from Apache's newer, ABI-compatible copy - never load two versions.
            foreach (var dll in new[] { "libssh2.dll", "nghttp2.dll", "libsqlite3.dll", "libsodium.dll" })
            {
                if (File.Exists(Path.Combine(Paths.Php, dll)))
                    php.AppendLine("LoadFile \"" + phpDir + "/" + dll + "\"");
            }
            php.AppendLine("LoadModule php_module \"" + phpDir + "/php8apache2_4.dll\"");
            php.AppendLine("<FilesMatch \"\\.php$\">");
            php.AppendLine("    SetHandler application/x-httpd-php");
            php.AppendLine("</FilesMatch>");
            php.AppendLine("<FilesMatch \"\\.phps$\">");
            php.AppendLine("    SetHandler application/x-httpd-php-source");
            php.AppendLine("</FilesMatch>");
            WriteIfChanged(Path.Combine(Paths.NampConfDir, "php.conf"), php.ToString());

            // phpMyAdmin + NAMP internal pages (only reachable from this PC)
            var pages = new StringBuilder(gen);
            pages.AppendLine("Alias /phpmyadmin \"" + root + "/phpmyadmin\"");
            pages.AppendLine("<Directory \"" + root + "/phpmyadmin\">");
            pages.AppendLine("    Options FollowSymLinks");
            pages.AppendLine("    AllowOverride All");
            pages.AppendLine("    Require local");
            pages.AppendLine("</Directory>");
            pages.AppendLine("Alias /namp \"" + root + "/apache/namp-pages\"");
            pages.AppendLine("<Directory \"" + root + "/apache/namp-pages\">");
            pages.AppendLine("    Require local");
            pages.AppendLine("</Directory>");
            WriteIfChanged(Path.Combine(Paths.NampConfDir, "namp-pages.conf"), pages.ToString());

            WriteVhosts();
        }

        /// <summary>True while Apache is configured to serve HTTPS (set by ApacheServer.Start).</summary>
        public static volatile bool HttpsActive;

        public static void WriteVhosts()
        {
            var s = Settings.Current;
            string root = Paths.Fwd(Paths.Root);
            var v = new StringBuilder("# Generated by NAMP from your Local domains list - do not edit by hand.\r\n\r\n");
            AppendLocalhost(v, s.ApachePort, null, root);
            foreach (var d in s.Domains) AppendSite(v, d, s.ApachePort, null, root);

            if (HttpsActive)
            {
                v.AppendLine();
                v.AppendLine("# ---------------- HTTPS (certificates issued by NAMP's local CA) ----------------");
                v.AppendLine("<IfModule !socache_shmcb_module>");
                v.AppendLine("    LoadModule socache_shmcb_module modules/mod_socache_shmcb.so");
                v.AppendLine("</IfModule>");
                v.AppendLine("SSLSessionCache \"shmcb:" + root + "/tmp/ssl_scache(512000)\"");
                v.AppendLine("SSLSessionCacheTimeout 300");
                v.AppendLine("SSLProtocol all -SSLv3 -TLSv1 -TLSv1.1");
                v.AppendLine("SSLHonorCipherOrder off");
                v.AppendLine("SSLPassPhraseDialog builtin");
                v.AppendLine();
                if (SiteCert("localhost", new[] { "localhost" }, new[] { "127.0.0.1", "::1" }))
                    AppendLocalhost(v, s.HttpsPort, "localhost", root);
                foreach (var d in s.Domains)
                {
                    if (SiteCert(d.Domain, new[] { d.Domain, "www." + d.Domain }, null))
                        AppendSite(v, d, s.HttpsPort, d.Domain, root);
                }
            }
            WriteIfChanged(Path.Combine(Paths.NampConfDir, "vhosts.conf"), v.ToString());
        }

        /// <summary>Privileged processes issue certificates; the normal NAMP window only uses ones the Helper made.</summary>
        static bool SiteCert(string name, string[] dns, string[] ips)
        {
            return Stack.Privileged ? Ssl.EnsureSiteCert(name, dns, ips) : Ssl.HasSiteCert(name, dns);
        }

        static void AppendSsl(StringBuilder v, string certName)
        {
            if (certName == null) return;
            v.AppendLine("    SSLEngine on");
            v.AppendLine("    SSLCertificateFile \"" + Paths.Fwd(Ssl.CertFile(certName)) + "\"");
            v.AppendLine("    SSLCertificateKeyFile \"" + Paths.Fwd(Ssl.KeyFile(certName)) + "\"");
            // Lets PHP code see the request as secure (e.g. $_SERVER['HTTPS'] == 'on')
            v.AppendLine("    <FilesMatch \"\\.php$\">");
            v.AppendLine("        SSLOptions +StdEnvVars");
            v.AppendLine("    </FilesMatch>");
        }

        static void AppendLocalhost(StringBuilder v, int port, string certName, string root)
        {
            v.AppendLine("<VirtualHost *:" + port + ">");
            v.AppendLine("    ServerName localhost");
            v.AppendLine("    ServerAlias 127.0.0.1");
            v.AppendLine("    DocumentRoot \"" + root + "/www\"");
            AppendSsl(v, certName);
            v.AppendLine("</VirtualHost>");
        }

        static void AppendSite(StringBuilder v, DomainEntry d, int port, string certName, string root)
        {
            string docRoot = Paths.Fwd(d.Root).TrimEnd('/');
            string logBase = root + "/logs/sites/" + d.Domain;
            v.AppendLine();
            v.AppendLine("<VirtualHost *:" + port + ">");
            v.AppendLine("    ServerName " + d.Domain);
            v.AppendLine("    ServerAlias www." + d.Domain);
            v.AppendLine("    DocumentRoot \"" + docRoot + "\"");
            v.AppendLine("    ErrorLog \"" + logBase + "-error.log\"");
            v.AppendLine("    CustomLog \"" + logBase + "-access.log\" combined");
            v.AppendLine("    <IfModule php_module>");
            v.AppendLine("        php_admin_value error_log \"" + logBase + "-php.log\"");
            v.AppendLine("    </IfModule>");
            AppendSsl(v, certName);
            v.AppendLine("    <Directory \"" + docRoot + "\">");
            v.AppendLine("        Options Indexes FollowSymLinks");
            v.AppendLine("        AllowOverride All");
            v.AppendLine("        Require all granted");
            v.AppendLine("    </Directory>");
            v.AppendLine("</VirtualHost>");
        }

        // ------------------------------------------------------------------
        // PHP
        // ------------------------------------------------------------------
        static readonly string[] Extensions = {
            "bz2", "curl", "fileinfo", "gd", "gettext", "intl", "mbstring", "exif", "mysqli", "openssl",
            "pdo_mysql", "pdo_sqlite", "sqlite3", "sodium", "zip", "soap", "xsl", "ftp"
        };

        public static void WritePhpIni()
        {
            string ini = Paths.PhpIni;
            bool fresh = !File.Exists(ini);
            string text;
            if (!fresh) text = Util.ReadShared(ini);
            else
            {
                string dev = Path.Combine(Paths.Php, "php.ini-development");
                text = File.Exists(dev) ? File.ReadAllText(dev) : "[PHP]\r\n";
            }
            string original = text;
            string r = Paths.Fwd(Paths.Root);

            // Paths are re-applied on every start (they follow the NAMP folder).
            string pd = Paths.Fwd(Paths.Php);
            text = SetIni(text, "extension_dir", "\"" + pd + "/ext\"");
            text = SetIni(text, "error_log", "\"" + r + "/logs/php-error.log\"");
            text = SetIni(text, "session.save_path", "\"" + r + "/tmp/sessions\"");
            text = SetIni(text, "upload_tmp_dir", "\"" + r + "/tmp/uploads\"");
            text = SetIni(text, "sys_temp_dir", "\"" + r + "/tmp\"");
            // Windows + Apache: when a worker can't map OPcache's shared memory at the same address
            // (ASLR), fall back to a file cache instead of aborting with a fatal error. Required for PHP 8.5,
            // where OPcache is always built in. revalidate_freq=0 = code changes show up immediately.
            Directory.CreateDirectory(Path.Combine(Paths.Tmp, "opcache"));
            text = SetIni(text, "opcache.file_cache", "\"" + r + "/tmp/opcache\"");
            text = SetIni(text, "opcache.file_cache_fallback", "1");
            text = SetIni(text, "opcache.revalidate_freq", "0");
            string mozilla = Path.Combine(Paths.Php, "extras", "ssl", "cacert.pem");
            if (File.Exists(mozilla))
            {
                // Public CAs + NAMP's local root, so PHP code can call https://yoursite.local too.
                string bundle = Path.Combine(Paths.Php, "extras", "ssl", "cacert-namp.pem");
                string name = "cacert.pem";
                try
                {
                    if (File.Exists(Ssl.CaCert))
                    {
                        string combined = File.ReadAllText(mozilla).TrimEnd() + "\n\nNAMP Local Root\n===============\n" + File.ReadAllText(Ssl.CaCert).Trim() + "\n";
                        if (!File.Exists(bundle) || File.ReadAllText(bundle) != combined) File.WriteAllText(bundle, combined, Util.Utf8);
                        name = "cacert-namp.pem";
                    }
                }
                catch { }
                text = SetIni(text, "curl.cainfo", "\"" + pd + "/extras/ssl/" + name + "\"");
                text = SetIni(text, "openssl.cafile", "\"" + pd + "/extras/ssl/" + name + "\"");
            }

            // Developer-friendly defaults are only applied once, so your own edits stick.
            if (fresh)
            {
                foreach (var ext in Extensions)
                    text = Regex.Replace(text, "^;extension=" + ext + "[ \\t]*(?=\\r?$)", "extension=" + ext, RegexOptions.Multiline);
                text = SetIni(text, "log_errors", "On");
                text = SetIni(text, "memory_limit", "512M");
                text = SetIni(text, "upload_max_filesize", "256M");
                text = SetIni(text, "post_max_size", "256M");
                text = SetIni(text, "max_execution_time", "300");
                text = SetIni(text, "max_input_time", "300");
                text = SetIni(text, "max_input_vars", "5000");
                text = SetIni(text, "date.timezone", TimeZoneMap.CurrentIana());
            }
            if (fresh || text != original) Util.WriteAtomic(ini, text);
        }

        /// <summary>Sets "key = value": replaces an active line, else a commented ";key" line, else appends.</summary>
        static string SetIni(string text, string key, string value)
        {
            string k = Regex.Escape(key);
            string line = key + " = " + value;
            var active = new Regex("^" + k + "[ \\t]*=[^\\r\\n]*", RegexOptions.Multiline);
            if (active.IsMatch(text)) return active.Replace(text, m => line);
            var commented = new Regex("^;" + k + "[ \\t]*=[^\\r\\n]*", RegexOptions.Multiline);
            if (commented.IsMatch(text)) return commented.Replace(text, m => line, 1);
            return text.TrimEnd() + "\r\n" + line + "\r\n";
        }

        // ------------------------------------------------------------------
        // MariaDB
        // ------------------------------------------------------------------
        public static void TryWriteMyIni()
        {
            try { WriteMyIni(); }
            catch (Exception ex) { Log.Warn("mariadb", "Could not write my.ini: " + ex.Message); }
        }

        public static bool MyIniHasPort(int port)
        {
            try { return File.Exists(Paths.MyIni) && Util.ReadShared(Paths.MyIni).Contains("\nport=" + port); }
            catch { return false; }
        }

        public static void WriteMyIni()
        {
            Directory.CreateDirectory(Paths.ServiceConfig);
            Directory.CreateDirectory(Path.Combine(Paths.Tmp, "mysql-files"));
            var s = Settings.Current;
            string r = Paths.Fwd(Paths.Root);
            if (!File.Exists(Paths.MyCustomIni))
            {
                Util.WriteAtomic(Paths.MyCustomIni,
                    "# Your own MariaDB settings. NAMP never overwrites this file.\r\n" +
                    "# Anything here overrides my.ini (which NAMP regenerates on every start).\r\n" +
                    "# Example:\r\n" +
                    "# [mysqld]\r\n" +
                    "# innodb_buffer_pool_size=1G\r\n" +
                    "# sql_mode=NO_ENGINE_SUBSTITUTION\r\n");
            }
            var sb = new StringBuilder();
            sb.AppendLine("# Generated by NAMP on every start - do not edit.");
            sb.AppendLine("# Put your own settings in my-custom.ini (same folder).");
            sb.AppendLine();
            sb.AppendLine("[client]");
            sb.AppendLine("port=" + s.MySqlPort);
            sb.AppendLine("default-character-set=utf8mb4");
            sb.AppendLine();
            sb.AppendLine("[mysqld]");
            sb.AppendLine("basedir=\"" + r + "/mariadb\"");
            sb.AppendLine("datadir=\"" + r + "/data/mariadb\"");
            sb.AppendLine("tmpdir=\"" + r + "/tmp\"");
            sb.AppendLine("port=" + s.MySqlPort);
            sb.AppendLine("bind-address=127.0.0.1");
            sb.AppendLine("log_error=\"" + r + "/logs/mariadb-error.log\"");
            sb.AppendLine("character-set-server=utf8mb4");
            sb.AppendLine("collation-server=utf8mb4_unicode_ci");
            sb.AppendLine("max_allowed_packet=256M");
            sb.AppendLine("default_storage_engine=InnoDB");
            sb.AppendLine("# LOAD DATA INFILE / SELECT INTO OUTFILE only inside this folder");
            sb.AppendLine("secure_file_priv=\"" + r + "/tmp/mysql-files\"");
            sb.AppendLine("innodb_buffer_pool_size=256M");
            sb.AppendLine("innodb_file_per_table=ON");
            sb.AppendLine();
            sb.AppendLine("# ---- Power-loss / crash protection ----");
            sb.AppendLine("# Every COMMIT is flushed to the physical disk before it is acknowledged,");
            sb.AppendLine("# so a committed transaction survives a sudden power cut.");
            sb.AppendLine("innodb_flush_log_at_trx_commit=1");
            sb.AppendLine("# Doublewrite buffer: protects pages that were half-written when power died.");
            sb.AppendLine("innodb_doublewrite=ON");
            sb.AppendLine("# Verify page checksums so damage is detected instead of silently used.");
            sb.AppendLine("innodb_checksum_algorithm=full_crc32");
            sb.AppendLine("# Normal fast, clean shutdown (fits inside Windows' shutdown time limit).");
            sb.AppendLine("innodb_fast_shutdown=1");
            sb.AppendLine("# Automatically check + repair Aria/MyISAM tables (incl. system tables) after a crash.");
            sb.AppendLine("aria_recover_options=BACKUP,FORCE");
            sb.AppendLine("myisam_recover_options=BACKUP,FORCE");
            sb.AppendLine();
            sb.AppendLine("[mariadb-dump]");
            sb.AppendLine("max_allowed_packet=256M");
            sb.AppendLine();
            sb.AppendLine("!include " + r + "/config/service/my-custom.ini");
            WriteIfChanged(Paths.MyIni, sb.ToString());
        }

        public static void WriteClientCnf()
        {
            var s = Settings.Current;
            WriteIfChanged(Paths.ClientCnf,
                "# NAMP maintenance account (used for backups, health checks and phpMyAdmin).\r\n" +
                "[client]\r\nuser=namp_admin\r\npassword=" + s.AdminPassword + "\r\nhost=127.0.0.1\r\nport=" + s.MySqlPort +
                "\r\ndefault-character-set=utf8mb4\r\n");
            WriteIfChanged(Paths.ClientRootCnf,
                "[client]\r\nuser=root\r\nhost=127.0.0.1\r\nport=" + s.MySqlPort + "\r\ndefault-character-set=utf8mb4\r\n");
        }

        // ------------------------------------------------------------------
        // phpMyAdmin
        // ------------------------------------------------------------------
        static readonly string[,] PmaTables = {
            { "bookmarktable", "pma__bookmark" }, { "relation", "pma__relation" }, { "table_info", "pma__table_info" },
            { "table_coords", "pma__table_coords" }, { "pdf_pages", "pma__pdf_pages" }, { "column_info", "pma__column_info" },
            { "history", "pma__history" }, { "table_uiprefs", "pma__table_uiprefs" }, { "tracking", "pma__tracking" },
            { "userconfig", "pma__userconfig" }, { "recent", "pma__recent" }, { "favorite", "pma__favorite" },
            { "users", "pma__users" }, { "usergroups", "pma__usergroups" }, { "navigationhiding", "pma__navigationhiding" },
            { "savedsearches", "pma__savedsearches" }, { "central_columns", "pma__central_columns" },
            { "designer_settings", "pma__designer_settings" }, { "export_templates", "pma__export_templates" }
        };

        public static void WritePmaConfig()
        {
            if (!Directory.Exists(Paths.Pma)) return;
            var s = Settings.Current;
            string r = Paths.Fwd(Paths.Root);
            var sb = new StringBuilder();
            sb.Append("<?php\n");
            sb.Append("/* Generated by NAMP on every start. Put overrides in config.custom.inc.php */\n");
            sb.Append("$cfg['blowfish_secret'] = '" + s.PmaSecret + "';\n");
            sb.Append("$i = 1;\n");
            sb.Append("$cfg['Servers'][$i]['verbose'] = 'NAMP MariaDB';\n");
            sb.Append("$cfg['Servers'][$i]['auth_type'] = 'config';\n");
            sb.Append("$cfg['Servers'][$i]['host'] = '127.0.0.1';\n");
            sb.Append("$cfg['Servers'][$i]['port'] = '" + s.MySqlPort + "';\n");
            sb.Append("$cfg['Servers'][$i]['user'] = 'namp_admin';\n");
            sb.Append("$cfg['Servers'][$i]['password'] = '" + s.AdminPassword + "';\n");
            sb.Append("$cfg['Servers'][$i]['AllowNoPassword'] = true;\n");
            sb.Append("$cfg['Servers'][$i]['controluser'] = 'namp_admin';\n");
            sb.Append("$cfg['Servers'][$i]['controlpass'] = '" + s.AdminPassword + "';\n");
            sb.Append("$cfg['Servers'][$i]['pmadb'] = 'phpmyadmin';\n");
            for (int i = 0; i < PmaTables.GetLength(0); i++)
                sb.Append("$cfg['Servers'][$i]['" + PmaTables[i, 0] + "'] = '" + PmaTables[i, 1] + "';\n");
            sb.Append("$cfg['TempDir'] = '" + r + "/tmp/phpmyadmin';\n");
            sb.Append("$cfg['UploadDir'] = '';\n");
            sb.Append("$cfg['SaveDir'] = '';\n");
            sb.Append("$cfg['VersionCheck'] = false;\n");
            sb.Append("$cfg['SendErrorReports'] = 'never';\n");
            sb.Append("$cfg['ExecTimeLimit'] = 600;\n");
            sb.Append("$cfg['MaxNavigationItems'] = 250;\n");
            sb.Append("$cfg['ShowPhpInfo'] = true;\n");
            sb.Append("if (is_file(__DIR__ . '/config.custom.inc.php')) {\n    include __DIR__ . '/config.custom.inc.php';\n}\n");
            WriteIfChanged(Path.Combine(Paths.Pma, "config.inc.php"), sb.ToString());
        }

        // ------------------------------------------------------------------
        // Pages
        // ------------------------------------------------------------------
        static void WriteInternalPages()
        {
            WriteIfChanged(Path.Combine(Paths.NampPages, "phpinfo.php"), "<?php phpinfo();\n");
        }

        static void EnsureWwwIndex()
        {
            string index = Path.Combine(Paths.Www, "index.php");
            if (File.Exists(index))
            {
                // Only refresh NAMP's own welcome page (old or new design) - never a page the user wrote.
                string current = File.ReadAllText(index);
                bool ours = current.Contains("NAMP-WELCOME") || current.Contains("Served by NAMP &middot;");
                if (!ours) return;
                string fresh = WelcomePage("localhost");
                if (current != fresh) File.WriteAllText(index, fresh, Util.Utf8);
                return;
            }
            if (Directory.EnumerateFileSystemEntries(Paths.Www).Any()) return;
            File.WriteAllText(index, WelcomePage("localhost"), Util.Utf8);
        }

        /// <summary>The branded (Lvato) welcome page shown until the user adds their own project files.</summary>
        public static string WelcomePage(string site)
        {
            using (var s = typeof(Configs).Assembly.GetManifestResourceStream("Namp.welcome.php"))
            using (var r = new StreamReader(s, Util.Utf8))
                return r.ReadToEnd().Replace("{{SITE}}", site.Replace("'", "").Replace("\\", ""));
        }
    }

    // =====================================================================
    // Hosts file (C:\Windows\System32\drivers\etc\hosts)
    // =====================================================================
    static class Hosts
    {
        public static string HostsPath = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
        const string Begin = "# >>> NAMP local domains (managed by NAMP - edits inside this block are overwritten) >>>";
        const string End = "# <<< NAMP local domains <<<";

        public static bool Sync(IEnumerable<string> domains, out string error)
        {
            error = null;
            try
            {
                string existing = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
                var kept = new List<string>();
                bool inBlock = false;
                foreach (var line in existing.Replace("\r\n", "\n").Split('\n'))
                {
                    if (line.Trim() == Begin) { inBlock = true; continue; }
                    if (inBlock) { if (line.Trim() == End) inBlock = false; continue; }
                    kept.Add(line);
                }
                while (kept.Count > 0 && kept[kept.Count - 1].Trim() == "") kept.RemoveAt(kept.Count - 1);

                var sb = new StringBuilder(string.Join("\r\n", kept));
                var list = domains.ToList();
                if (list.Count > 0)
                {
                    sb.Append("\r\n\r\n").Append(Begin).Append("\r\n");
                    foreach (var d in list)
                    {
                        sb.Append("127.0.0.1       ").Append(d).Append("\r\n");
                        sb.Append("127.0.0.1       www.").Append(d).Append("\r\n");
                        sb.Append("::1             ").Append(d).Append("\r\n");
                        sb.Append("::1             www.").Append(d).Append("\r\n");
                    }
                    sb.Append(End);
                }
                sb.Append("\r\n");
                string result = sb.ToString();
                if (result.Replace("\r\n", "\n").Trim() == existing.Replace("\r\n", "\n").Trim()) return true;

                string backup = HostsPath + ".namp-backup";
                if (!File.Exists(backup) && File.Exists(HostsPath)) File.Copy(HostsPath, backup);
                Util.WriteAtomic(HostsPath, result);
                try { Native.DnsFlushResolverCache(); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    // =====================================================================
    // Windows time zone -> IANA (for php.ini date.timezone)
    // =====================================================================
    static class TimeZoneMap
    {
        static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Pakistan Standard Time", "Asia/Karachi" }, { "India Standard Time", "Asia/Kolkata" },
            { "Arabian Standard Time", "Asia/Dubai" }, { "Arab Standard Time", "Asia/Riyadh" },
            { "Bangladesh Standard Time", "Asia/Dhaka" }, { "Afghanistan Standard Time", "Asia/Kabul" },
            { "Iran Standard Time", "Asia/Tehran" }, { "Turkey Standard Time", "Europe/Istanbul" },
            { "Egypt Standard Time", "Africa/Cairo" }, { "South Africa Standard Time", "Africa/Johannesburg" },
            { "W. Central Africa Standard Time", "Africa/Lagos" }, { "E. Africa Standard Time", "Africa/Nairobi" },
            { "GMT Standard Time", "Europe/London" }, { "Greenwich Standard Time", "Atlantic/Reykjavik" },
            { "W. Europe Standard Time", "Europe/Berlin" }, { "Romance Standard Time", "Europe/Paris" },
            { "Central Europe Standard Time", "Europe/Budapest" }, { "Central European Standard Time", "Europe/Warsaw" },
            { "E. Europe Standard Time", "Europe/Chisinau" }, { "FLE Standard Time", "Europe/Kiev" },
            { "GTB Standard Time", "Europe/Bucharest" }, { "Russian Standard Time", "Europe/Moscow" },
            { "China Standard Time", "Asia/Shanghai" }, { "Tokyo Standard Time", "Asia/Tokyo" },
            { "Korea Standard Time", "Asia/Seoul" }, { "Singapore Standard Time", "Asia/Singapore" },
            { "SE Asia Standard Time", "Asia/Bangkok" }, { "Taipei Standard Time", "Asia/Taipei" },
            { "West Asia Standard Time", "Asia/Tashkent" }, { "Nepal Standard Time", "Asia/Kathmandu" },
            { "Sri Lanka Standard Time", "Asia/Colombo" }, { "Myanmar Standard Time", "Asia/Yangon" },
            { "AUS Eastern Standard Time", "Australia/Sydney" }, { "E. Australia Standard Time", "Australia/Brisbane" },
            { "W. Australia Standard Time", "Australia/Perth" }, { "New Zealand Standard Time", "Pacific/Auckland" },
            { "Eastern Standard Time", "America/New_York" }, { "Central Standard Time", "America/Chicago" },
            { "Mountain Standard Time", "America/Denver" }, { "US Mountain Standard Time", "America/Phoenix" },
            { "Pacific Standard Time", "America/Los_Angeles" }, { "Alaskan Standard Time", "America/Anchorage" },
            { "Hawaiian Standard Time", "Pacific/Honolulu" }, { "Atlantic Standard Time", "America/Halifax" },
            { "Canada Central Standard Time", "America/Regina" }, { "Central Standard Time (Mexico)", "America/Mexico_City" },
            { "E. South America Standard Time", "America/Sao_Paulo" }, { "Argentina Standard Time", "America/Argentina/Buenos_Aires" },
            { "SA Pacific Standard Time", "America/Bogota" }, { "Pacific SA Standard Time", "America/Santiago" },
            { "Israel Standard Time", "Asia/Jerusalem" }, { "Jordan Standard Time", "Asia/Amman" },
            { "Arabic Standard Time", "Asia/Baghdad" }, { "Morocco Standard Time", "Africa/Casablanca" },
            { "UTC", "UTC" }, { "Coordinated Universal Time", "UTC" }
        };

        public static string CurrentIana()
        {
            try
            {
                string id = TimeZoneInfo.Local.Id;
                string iana;
                if (Map.TryGetValue(id, out iana)) return iana;
            }
            catch { }
            return "UTC";
        }
    }
}
