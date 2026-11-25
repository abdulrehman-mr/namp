using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Namp
{
    /// <summary>Win32 interop used by NAMP (job objects, shutdown handling, services, TCP table).</summary>
    static class Native
    {
        public const int WM_QUERYENDSESSION = 0x0011;
        public const int WM_ENDSESSION = 0x0016;

        // ---------------- Job objects (kill Apache if NAMP itself dies) ----------------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint len);
        [DllImport("kernel32.dll")]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong A, B, C, D, E, F; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        public static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
            return job;
        }

        // ---------------- Named events (graceful Apache / MariaDB signalling) ----------------
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenEvent(uint access, bool inherit, string name);
        [DllImport("kernel32.dll")]
        static extern bool SetEvent(IntPtr h);

        public static bool SignalEvent(string name)
        {
            IntPtr h = OpenEvent(0x0002 /* EVENT_MODIFY_STATE */, false, name);
            if (h == IntPtr.Zero) return false;
            try { return SetEvent(h); }
            finally { CloseHandle(h); }
        }

        // ---------------- Processes ----------------
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

        public static string GetProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        // ---------------- Windows shutdown cooperation ----------------
        [DllImport("kernel32.dll")]
        public static extern bool SetProcessShutdownParameters(uint level, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);
        [DllImport("user32.dll")]
        public static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);

        // ---------------- Misc ----------------
        [DllImport("dnsapi.dll")]
        public static extern bool DnsFlushResolverCache();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr h);

        // ---------------- Service control manager ----------------
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManager(string machine, string db, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateService(IntPtr scm, string name, string display, uint access, uint type, uint start,
            uint errorControl, string binPath, string group, IntPtr tag, string deps, string user, string pwd);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig(IntPtr svc, uint type, uint start, uint errorControl, string binPath,
            string group, IntPtr tag, string deps, string user, string pwd, string display);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig2(IntPtr svc, uint level, ref SERVICE_DESCRIPTION info);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DeleteService(IntPtr svc);
        [DllImport("advapi32.dll")]
        static extern bool CloseServiceHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SERVICE_DESCRIPTION { public string Description; }

        const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

        /// <summary>Creates the service, or updates its command line if it already exists. Returns null on success.</summary>
        public static string CreateOrUpdateService(string name, string display, string description, string binPath, uint startType)
        {
            IntPtr scm = OpenSCManager(null, null, 0xF003F);
            if (scm == IntPtr.Zero) return "Cannot open the Windows service manager (error " + Marshal.GetLastWin32Error() + "). Run NAMP as administrator.";
            try
            {
                IntPtr svc = OpenService(scm, name, 0xF01FF);
                if (svc == IntPtr.Zero)
                {
                    svc = CreateService(scm, name, display, 0xF01FF, 0x10 /* own process */, startType,
                        1 /* normal */, binPath, null, IntPtr.Zero, null, null, null);
                    if (svc == IntPtr.Zero) return "Could not create the MariaDB service (error " + Marshal.GetLastWin32Error() + ").";
                }
                else
                {
                    if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, startType, SERVICE_NO_CHANGE, binPath, null, IntPtr.Zero, null, null, null, display))
                    {
                        int err = Marshal.GetLastWin32Error();
                        CloseServiceHandle(svc);
                        return "Could not update the MariaDB service (error " + err + ").";
                    }
                }
                var d = new SERVICE_DESCRIPTION { Description = description };
                ChangeServiceConfig2(svc, 1, ref d);
                CloseServiceHandle(svc);
                return null;
            }
            finally { CloseServiceHandle(scm); }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool QueryServiceStatusEx(IntPtr svc, int infoLevel, IntPtr buffer, int size, out int needed);

        /// <summary>PID of a running service via the service manager (works for normal users, unlike opening the process).</summary>
        public static int GetServicePid(string name)
        {
            IntPtr scm = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
            if (scm == IntPtr.Zero) return 0;
            try
            {
                IntPtr svc = OpenService(scm, name, 0x0004 /* SERVICE_QUERY_STATUS */);
                if (svc == IntPtr.Zero) return 0;
                IntPtr buf = Marshal.AllocHGlobal(64);
                try
                {
                    int needed;
                    if (!QueryServiceStatusEx(svc, 0 /* SC_STATUS_PROCESS_INFO */, buf, 64, out needed)) return 0;
                    return Marshal.ReadInt32(buf, 28); // SERVICE_STATUS_PROCESS.dwProcessId
                }
                finally { Marshal.FreeHGlobal(buf); CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        /// <summary>Runs a service under another account; for virtual accounts ("NT SERVICE\x") the password must be NULL.</summary>
        public static string SetServiceAccount(string name, string account)
        {
            IntPtr scm = OpenSCManager(null, null, 0xF003F);
            if (scm == IntPtr.Zero) return "cannot open service manager (" + Marshal.GetLastWin32Error() + ")";
            try
            {
                IntPtr svc = OpenService(scm, name, 0x0002 /* SERVICE_CHANGE_CONFIG */);
                if (svc == IntPtr.Zero) return "cannot open service (" + Marshal.GetLastWin32Error() + ")";
                try
                {
                    if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, account, null, null))
                        return "ChangeServiceConfig failed (" + Marshal.GetLastWin32Error() + ")";
                    return null;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        public static bool DeleteServiceByName(string name)
        {
            IntPtr scm = OpenSCManager(null, null, 0xF003F);
            if (scm == IntPtr.Zero) return false;
            try
            {
                IntPtr svc = OpenService(scm, name, 0x10000 /* DELETE */);
                if (svc == IntPtr.Zero) return true;
                bool ok = DeleteService(svc);
                CloseServiceHandle(svc);
                return ok;
            }
            finally { CloseServiceHandle(scm); }
        }

        // ---------------- TCP listeners (who owns a port?) ----------------
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        public struct Listener { public int Port; public int Pid; }

        public static List<Listener> GetTcpListeners()
        {
            var list = new List<Listener>();
            Collect(list, 2, 24, 8, 20);   // AF_INET:  MIB_TCPROW_OWNER_PID (port @8, pid @20)
            Collect(list, 23, 56, 20, 52); // AF_INET6: MIB_TCP6ROW_OWNER_PID (port @20, pid @52)
            return list;
        }

        static void Collect(List<Listener> list, int af, int rowSize, int portOffset, int pidOffset)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
            if (size <= 0) return;
            IntPtr buf = Marshal.AllocHGlobal(size + 1024);
            try
            {
                size += 1024;
                if (GetExtendedTcpTable(buf, ref size, false, af, 3, 0) != 0) return;
                int count = Marshal.ReadInt32(buf);
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(buf, 4 + i * rowSize);
                    int rawPort = Marshal.ReadInt32(row, portOffset);
                    int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                    int pid = Marshal.ReadInt32(row, pidOffset);
                    list.Add(new Listener { Port = port, Pid = pid });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
