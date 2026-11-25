using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Namp
{
    /// <summary>
    /// HTTPS for local domains.
    ///
    /// Design (safe even if someone copies NAMP's files):
    ///   * A root certificate is created once and added to the Windows trusted roots.
    ///     Its private key is used exactly once - to sign the issuing CA below - and then destroyed.
    ///   * The issuing CA is name-constrained to local names (.local, .test, localhost, ...).
    ///     Windows, Chrome/Edge and Firefox all enforce constraints on an issuing CA, so its key
    ///     can never be used to impersonate a real website.
    ///   * Each site gets a leaf certificate (leaf + issuing CA are served together by Apache).
    /// </summary>
    static class Ssl
    {
        public static readonly string[] LocalTlds = { "local", "test", "localhost", "lan", "internal", "home.arpa" };
        const string RootName = "NAMP Local Root";
        const string IssuerName = "NAMP Local Development CA";

        static string Dir { get { return Paths.R("config", "ssl"); } }
        static string SitesDir { get { return Path.Combine(Dir, "sites"); } }
        /// <summary>The trust anchor (installed into Windows). Public certificate only.</summary>
        public static string CaCert { get { return Path.Combine(Dir, "namp-root.crt"); } }
        static string IssuerCert { get { return Path.Combine(Dir, "namp-issuing-ca.crt"); } }
        static string IssuerKey { get { return Path.Combine(Dir, "namp-issuing-ca.key"); } }
        static string OpenSslCnf { get { return Path.Combine(Dir, "openssl.cnf"); } }
        static string OpenSsl { get { return Paths.R("apache", "bin", "openssl.exe"); } }

        public static string CertFile(string name) { return Path.Combine(SitesDir, name + ".crt"); }
        public static string KeyFile(string name) { return Path.Combine(SitesDir, name + ".key"); }

        static RunResult Run(string args)
        {
            return Util.Run(OpenSsl, args, 60000, Dir, new Dictionary<string, string> { { "OPENSSL_CONF", OpenSslCnf } });
        }

        static string Constraints()
        {
            return "nameConstraints=critical," + string.Join(",", LocalTlds.Select(t => "permitted;DNS:" + t)) +
                ",permitted;IP:127.0.0.0/255.0.0.0,permitted;IP:0:0:0:0:0:0:0:1/FFFF:FFFF:FFFF:FFFF:FFFF:FFFF:FFFF:FFFF";
        }

        /// <summary>Creates the CA pair if needed and makes sure Windows trusts it.</summary>
        public static bool EnsureCa()
        {
            if (!File.Exists(OpenSsl)) { Log.Warn("ssl", "openssl.exe not found - HTTPS disabled."); return false; }
            Directory.CreateDirectory(SitesDir);
            if (!File.Exists(OpenSslCnf))
                File.WriteAllText(OpenSslCnf, "[req]\r\ndistinguished_name = dn\r\nprompt = no\r\n[dn]\r\nCN = NAMP\r\n", Util.Utf8);

            if (!File.Exists(CaCert) || !File.Exists(IssuerCert) || !File.Exists(IssuerKey))
            {
                if (!CreateCa()) return false;
            }
            Trust();
            return true;
        }

        static bool CreateCa()
        {
            Log.Info("ssl", "Creating NAMP's local certificate authority (one time)...");
            foreach (var f in Directory.GetFiles(SitesDir)) TryDelete(f); // old site certs belong to the old CA
            string rootKey = Path.Combine(Dir, "root-once.key");
            string csr = Path.Combine(Dir, "issuer.csr");
            string ext = Path.Combine(Dir, "issuer.ext");
            string machine = Environment.MachineName;
            try
            {
                var r = Run("req -x509 -new -nodes -newkey rsa:2048 -sha256 -days 3650 -keyout " + Util.Q(rootKey) + " -out " + Util.Q(CaCert) +
                    " -subj " + Util.Q("/CN=" + RootName + " (" + machine + ")/O=NAMP") +
                    " -addext \"basicConstraints=critical,CA:TRUE,pathlen:1\" -addext \"keyUsage=critical,keyCertSign,cRLSign\"" +
                    " -addext \"" + Constraints() + "\"");
                if (r.Ok)
                    r = Run("req -new -nodes -newkey rsa:2048 -sha256 -keyout " + Util.Q(IssuerKey) + " -out " + Util.Q(csr) +
                        " -subj " + Util.Q("/CN=" + IssuerName + " (" + machine + ")/O=NAMP"));
                if (r.Ok)
                {
                    File.WriteAllText(ext,
                        "basicConstraints=critical,CA:TRUE,pathlen:0\r\n" +
                        "keyUsage=critical,keyCertSign,cRLSign\r\n" +
                        "subjectKeyIdentifier=hash\r\n" +
                        "authorityKeyIdentifier=keyid\r\n" +
                        Constraints() + "\r\n", Util.Utf8);
                    r = Run("x509 -req -sha256 -days 3650 -in " + Util.Q(csr) + " -CA " + Util.Q(CaCert) + " -CAkey " + Util.Q(rootKey) +
                        " -set_serial 0x" + Util.RandomHex(16) + " -extfile " + Util.Q(ext) + " -out " + Util.Q(IssuerCert));
                }
                if (!r.Ok || !File.Exists(IssuerCert))
                {
                    Log.Error("ssl", "Could not create the certificate authority: " + r.Output.Trim());
                    TryDelete(CaCert); TryDelete(IssuerCert); TryDelete(IssuerKey);
                    return false;
                }
                ProtectKey(IssuerKey);
                Log.Ok("ssl", "Certificate authority created - it can only ever issue certificates for local names.");
                return true;
            }
            finally
            {
                Destroy(rootKey); // the root key is never needed again
                TryDelete(csr);
                TryDelete(ext);
            }
        }

        static void Destroy(string file)
        {
            try
            {
                if (!File.Exists(file)) return;
                long len = new FileInfo(file).Length;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write))
                {
                    fs.Write(new byte[len], 0, (int)len);
                    fs.Flush(true);
                }
                File.Delete(file);
            }
            catch { TryDelete(file); }
        }

        static void TryDelete(string f)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        /// <summary>Only administrators, SYSTEM and the NAMP user may read the issuing key.</summary>
        static void ProtectKey(string file)
        {
            try
            {
                var sec = new FileSecurity();
                sec.SetAccessRuleProtection(true, false);
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                using (var me = WindowsIdentity.GetCurrent())
                    sec.AddAccessRule(new FileSystemAccessRule(me.User, FileSystemRights.FullControl, AccessControlType.Allow));
                File.SetAccessControl(file, sec);
            }
            catch { }
        }

        public static bool IsTrusted()
        {
            try
            {
                using (var ca = new X509Certificate2(CaCert))
                using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadOnly);
                    return store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false).Count > 0;
                }
            }
            catch { return false; }
        }

        static void Trust()
        {
            if (IsTrusted()) return;
            try
            {
                Untrust(); // remove roots from an older NAMP CA
                using (var ca = new X509Certificate2(CaCert))
                using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(ca);
                }
                Log.Ok("ssl", "Windows now trusts NAMP's local certificates (Edge, Chrome and Firefox use this).");
            }
            catch (Exception ex)
            {
                Log.Warn("ssl", "Could not add NAMP's certificate to Windows (" + ex.Message + "). Browsers will show a certificate warning.");
            }
        }

        /// <summary>Removes every NAMP root from the Windows trusted roots (uninstall / re-create).</summary>
        public static void Untrust()
        {
            try
            {
                using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    foreach (var c in store.Certificates.Cast<X509Certificate2>().Where(c => c.Subject.Contains("CN=NAMP Local") && c.Subject.Contains("O=NAMP")).ToList())
                        store.Remove(c);
                }
            }
            catch { }
        }

        /// <summary>Issues (or renews) a site certificate; the .crt file holds leaf + issuing CA.</summary>
        public static bool EnsureSiteCert(string name, IEnumerable<string> dnsNames, IEnumerable<string> ips)
        {
            string crt = CertFile(name), key = KeyFile(name);
            var dns = dnsNames.ToList();
            var ipList = (ips ?? new string[0]).ToList();
            if (CertIsGood(crt, key, dns)) return true;

            string csr = Path.Combine(SitesDir, name + ".csr");
            string ext = Path.Combine(SitesDir, name + ".ext");
            string leaf = Path.Combine(SitesDir, name + ".leaf");
            var san = dns.Select(d => "DNS:" + d).Concat(ipList.Select(i => "IP:" + i));
            File.WriteAllText(ext,
                "basicConstraints=critical,CA:FALSE\r\n" +
                "keyUsage=critical,digitalSignature,keyEncipherment\r\n" +
                "extendedKeyUsage=serverAuth\r\n" +
                "subjectKeyIdentifier=hash\r\n" +
                "authorityKeyIdentifier=keyid\r\n" +
                "subjectAltName=" + string.Join(",", san) + "\r\n", Util.Utf8);
            var r = Run("req -new -nodes -newkey rsa:2048 -sha256 -keyout " + Util.Q(key) + " -out " + Util.Q(csr) + " -subj " + Util.Q("/CN=" + dns[0] + "/O=NAMP"));
            if (r.Ok)
                r = Run("x509 -req -sha256 -days 825 -in " + Util.Q(csr) + " -CA " + Util.Q(IssuerCert) + " -CAkey " + Util.Q(IssuerKey) +
                    " -set_serial 0x" + Util.RandomHex(16) + " -extfile " + Util.Q(ext) + " -out " + Util.Q(leaf));
            try
            {
                if (r.Ok && File.Exists(leaf))
                    File.WriteAllText(crt, File.ReadAllText(leaf).TrimEnd() + "\n" + File.ReadAllText(IssuerCert).TrimEnd() + "\n");
            }
            catch (Exception ex) { r.ExitCode = 1; r.Output = ex.Message; }
            TryDelete(csr); TryDelete(ext); TryDelete(leaf);
            if (!r.Ok || !File.Exists(crt))
            {
                Log.Error("ssl", "Could not create the certificate for " + name + ": " + r.Output.Trim());
                return false;
            }
            Log.Info("ssl", "Issued HTTPS certificate for " + string.Join(", ", dns));
            return true;
        }

        public static bool HasSiteCert(string name, IEnumerable<string> dnsNames)
        {
            return CertIsGood(CertFile(name), KeyFile(name), dnsNames.ToList());
        }

        /// <summary>True if the cert exists, comes from the current issuing CA, covers all names and is not about to expire.</summary>
        static bool CertIsGood(string crt, string key, List<string> dns)
        {
            try
            {
                if (!File.Exists(crt) || !File.Exists(key)) return false;
                using (var c = new X509Certificate2(crt))
                using (var ca = new X509Certificate2(IssuerCert))
                {
                    if (c.Issuer != ca.Subject) return false;
                    if (c.NotAfter < DateTime.Now.AddDays(30)) return false;
                    string sans = "";
                    foreach (X509Extension e in c.Extensions)
                        if (e.Oid.Value == "2.5.29.17") sans = e.Format(false);
                    return dns.All(d => sans.Contains("=" + d));
                }
            }
            catch { return false; }
        }

        public static bool IsLocalTld(string domain)
        {
            return LocalTlds.Any(t => domain == t || domain.EndsWith("." + t));
        }
    }
}
