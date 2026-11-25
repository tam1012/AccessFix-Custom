using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AccessFix
{
    internal class ProxyBackupModel
    {
        public string AutoConfigURL { get; set; }
        public int? ProxyEnable { get; set; }
        public string ProxyServer { get; set; }
        public string ProxyOverride { get; set; }
    }

    public static class SystemProxyManager
    {
        private const string RegInternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const string RegConnections = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections";
        private const string RegRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "AccessFix";
        private const string HostsMarker = "# AccessFix";
        private const string LegacyHostsMarker = "# SteamAccessFix";

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;
        private const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;

        private const int INTERNET_PER_CONN_FLAGS = 1;
        private const int INTERNET_PER_CONN_AUTOCONFIG_URL = 4;

        private const int PROXY_TYPE_DIRECT = 0x00000001;
        private const int PROXY_TYPE_PROXY = 0x00000002;
        private const int PROXY_TYPE_AUTO_PROXY_URL = 0x00000004;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct INTERNET_PER_CONN_OPTION
        {
            public int dwOption;
            public INTERNET_PER_CONN_OPTION_VALUE Value;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INTERNET_PER_CONN_OPTION_VALUE
        {
            [FieldOffset(0)]
            public int dwValue;
            [FieldOffset(0)]
            public IntPtr pszValue;
            [FieldOffset(0)]
            public System.Runtime.InteropServices.ComTypes.FILETIME ftValue;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct INTERNET_PER_CONN_OPTION_LIST
        {
            public int dwSize;
            public IntPtr pszConnection;
            public int dwOptionCount;
            public int dwOptionError;
            public IntPtr pOptions;
        }

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        private static extern int DnsFlushResolverCache();

        private static long pacRevision = 1;

        private static string GetBackupFilePath()
        {
            return Path.Combine(AppConfig.GetStorageDirectory(), "AccessFix.proxy-backup.json");
        }

        private static string GetHostsSystemPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
        }

        public static bool IsAdministrator()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public static void FlushDns()
        {
            try { DnsFlushResolverCache(); } catch { }
        }

        public static string BuildPacScript(int port, IList<string> domains)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("function FindProxyForURL(url, host) {");
            sb.AppendLine("  if (!host) return \"DIRECT\";");
            sb.AppendLine("  host = host.toLowerCase();");
            sb.Append("  var d = [");
            if (domains != null)
            {
                for (int i = 0; i < domains.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('"').Append(domains[i].Replace("\"", "")).Append('"');
                }
            }
            sb.AppendLine("];");
            sb.AppendLine("  for (var i = 0; i < d.length; i++) {");
            sb.AppendLine("    if (host === d[i] || dnsDomainIs(host, \".\" + d[i])) {");
            sb.AppendFormat("      return \"PROXY 127.0.0.1:{0}; DIRECT\";\r\n", port);
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("  return \"DIRECT\";");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static void SanitizeResidualOnStartup()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegInternetSettings, false))
                {
                    if (k != null)
                    {
                        string autoUrl = Convert.ToString(k.GetValue("AutoConfigURL", ""));
                        string proxySrv = Convert.ToString(k.GetValue("ProxyServer", ""));
                        bool stalePac = autoUrl.IndexOf("127.0.0.1:", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                        autoUrl.IndexOf("/proxy.pac", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool staleLocalProxy = proxySrv.IndexOf("127.0.0.1:1808", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (stalePac || staleLocalProxy || File.Exists(GetBackupFilePath()))
                        {
                            RestoreSystemProxy();
                        }
                    }
                }
            }
            catch { }

            if (IsAdministrator())
            {
                RemovePubgHostsEntries();
            }
        }

        private static void SaveOriginalProxyIfNeeded()
        {
            string backupPath = GetBackupFilePath();
            if (File.Exists(backupPath)) return;

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegInternetSettings, false))
                {
                    if (k == null) return;
                    string autoUrl = k.GetValue("AutoConfigURL") as string;
                    string proxyServer = k.GetValue("ProxyServer") as string;
                    object proxyEnableObj = k.GetValue("ProxyEnable");
                    string proxyOverride = k.GetValue("ProxyOverride") as string;

                    int proxyEnable = 0;
                    if (proxyEnableObj != null)
                    {
                        try { proxyEnable = Convert.ToInt32(proxyEnableObj); } catch { }
                    }

                    // Không lưu lại nếu giá trị hiện tại đang là localhost PAC của phiên cũ
                    if (!string.IsNullOrEmpty(autoUrl) &&
                        (autoUrl.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         autoUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         autoUrl.IndexOf("proxy.pac", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        autoUrl = null;
                    }

                    if (!string.IsNullOrEmpty(proxyServer) &&
                        (proxyServer.IndexOf("127.0.0.1:1808", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        proxyServer = null;
                        proxyEnable = 0;
                    }

                    ProxyBackupModel model = new ProxyBackupModel
                    {
                        AutoConfigURL = autoUrl,
                        ProxyEnable = proxyEnable,
                        ProxyServer = proxyServer,
                        ProxyOverride = proxyOverride
                    };
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    File.WriteAllText(backupPath, ser.Serialize(model), Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void ApplyPacUrl(int port)
        {
            SaveOriginalProxyIfNeeded();
            pacRevision++;
            string pacUrl = string.Format("http://127.0.0.1:{0}/proxy.pac?v={1}", port, pacRevision);

            // 1. Dùng WinInet API chuẩn để đồng bộ cả DefaultConnectionSettings, SavedLegacySettings và AutoConfigURL
            ApplyWinInetPerConnectionPac(pacUrl);

            // 2. Ghi tường minh vào HKCU Internet Settings để đảm bảo mọi ứng dụng đọc trực tiếp Registry đều nhận
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegInternetSettings, true))
                {
                    if (k != null)
                    {
                        k.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
                        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }

            NotifyWinInetRefresh();
        }

        public static void RestoreSystemProxy()
        {
            string backupPath = GetBackupFilePath();
            ProxyBackupModel backup = null;

            if (File.Exists(backupPath))
            {
                try
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    backup = ser.Deserialize<ProxyBackupModel>(File.ReadAllText(backupPath, Encoding.UTF8));
                }
                catch { }
            }

            // Đưa kết nối WinInet về Direct (hoặc khôi phục proxy gốc nếu người dùng vốn có proxy công ty)
            if (backup != null && !string.IsNullOrEmpty(backup.AutoConfigURL))
            {
                ApplyWinInetPerConnectionPac(backup.AutoConfigURL);
            }
            else
            {
                ApplyWinInetDirectOnly(backup != null && backup.ProxyEnable == 1 && !string.IsNullOrEmpty(backup.ProxyServer));
            }

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegInternetSettings, true))
                {
                    if (k != null)
                    {
                        if (backup != null && !string.IsNullOrEmpty(backup.AutoConfigURL))
                            k.SetValue("AutoConfigURL", backup.AutoConfigURL, RegistryValueKind.String);
                        else
                            k.DeleteValue("AutoConfigURL", false);

                        if (backup != null && backup.ProxyEnable == 1 && !string.IsNullOrEmpty(backup.ProxyServer))
                        {
                            k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                            k.SetValue("ProxyServer", backup.ProxyServer, RegistryValueKind.String);
                        }
                        else
                        {
                            k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                            k.DeleteValue("ProxyServer", false);
                        }

                        if (backup != null && !string.IsNullOrEmpty(backup.ProxyOverride))
                            k.SetValue("ProxyOverride", backup.ProxyOverride, RegistryValueKind.String);
                    }
                }
            }
            catch { }

            // Đảm bảo byte cờ trong Connections về Direct nếu không dùng proxy công ty
            if (backup == null || (string.IsNullOrEmpty(backup.AutoConfigURL) && backup.ProxyEnable != 1))
            {
                ForceConnectionSettingsDirectFlag();
            }

            try
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            catch { }

            NotifyWinInetRefresh();
        }

        private static void ForceConnectionSettingsDirectFlag()
        {
            try
            {
                using (RegistryKey connKey = Registry.CurrentUser.OpenSubKey(RegConnections, true))
                {
                    if (connKey == null) return;
                    string[] names = new string[] { "DefaultConnectionSettings", "SavedLegacySettings" };
                    foreach (string name in names)
                    {
                        byte[] bytes = connKey.GetValue(name) as byte[];
                        if (bytes != null && bytes.Length >= 9)
                        {
                            bytes[8] = PROXY_TYPE_DIRECT;
                            connKey.SetValue(name, bytes, RegistryValueKind.Binary);
                        }
                    }
                }
            }
            catch { }
        }

        private static void ApplyWinInetPerConnectionPac(string pacUrl)
        {
            IntPtr urlPtr = IntPtr.Zero;
            IntPtr optsPtr = IntPtr.Zero;
            IntPtr listPtr = IntPtr.Zero;
            try
            {
                INTERNET_PER_CONN_OPTION[] opts = new INTERNET_PER_CONN_OPTION[2];
                opts[0].dwOption = INTERNET_PER_CONN_FLAGS;
                opts[0].Value.dwValue = PROXY_TYPE_DIRECT | PROXY_TYPE_AUTO_PROXY_URL;

                urlPtr = Marshal.StringToHGlobalAuto(pacUrl);
                opts[1].dwOption = INTERNET_PER_CONN_AUTOCONFIG_URL;
                opts[1].Value.pszValue = urlPtr;

                int optSize = Marshal.SizeOf(typeof(INTERNET_PER_CONN_OPTION));
                optsPtr = Marshal.AllocCoTaskMem(optSize * opts.Length);
                for (int i = 0; i < opts.Length; i++)
                {
                    IntPtr cur = new IntPtr(optsPtr.ToInt64() + i * optSize);
                    Marshal.StructureToPtr(opts[i], cur, false);
                }

                INTERNET_PER_CONN_OPTION_LIST list = new INTERNET_PER_CONN_OPTION_LIST();
                list.dwSize = Marshal.SizeOf(typeof(INTERNET_PER_CONN_OPTION_LIST));
                list.pszConnection = IntPtr.Zero;
                list.dwOptionCount = opts.Length;
                list.dwOptionError = 0;
                list.pOptions = optsPtr;

                listPtr = Marshal.AllocCoTaskMem(list.dwSize);
                Marshal.StructureToPtr(list, listPtr, false);

                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, listPtr, list.dwSize);
            }
            catch { }
            finally
            {
                if (urlPtr != IntPtr.Zero) Marshal.FreeHGlobal(urlPtr);
                if (optsPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(optsPtr);
                if (listPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(listPtr);
            }
        }

        private static void ApplyWinInetDirectOnly(bool keepManualProxy)
        {
            IntPtr urlPtr = IntPtr.Zero;
            IntPtr optsPtr = IntPtr.Zero;
            IntPtr listPtr = IntPtr.Zero;
            try
            {
                INTERNET_PER_CONN_OPTION[] opts = new INTERNET_PER_CONN_OPTION[2];
                opts[0].dwOption = INTERNET_PER_CONN_FLAGS;
                opts[0].Value.dwValue = keepManualProxy ? (PROXY_TYPE_DIRECT | PROXY_TYPE_PROXY) : PROXY_TYPE_DIRECT;

                urlPtr = Marshal.StringToHGlobalAuto("");
                opts[1].dwOption = INTERNET_PER_CONN_AUTOCONFIG_URL;
                opts[1].Value.pszValue = urlPtr;

                int optSize = Marshal.SizeOf(typeof(INTERNET_PER_CONN_OPTION));
                optsPtr = Marshal.AllocCoTaskMem(optSize * opts.Length);
                for (int i = 0; i < opts.Length; i++)
                {
                    IntPtr cur = new IntPtr(optsPtr.ToInt64() + i * optSize);
                    Marshal.StructureToPtr(opts[i], cur, false);
                }

                INTERNET_PER_CONN_OPTION_LIST list = new INTERNET_PER_CONN_OPTION_LIST();
                list.dwSize = Marshal.SizeOf(typeof(INTERNET_PER_CONN_OPTION_LIST));
                list.pszConnection = IntPtr.Zero;
                list.dwOptionCount = opts.Length;
                list.dwOptionError = 0;
                list.pOptions = optsPtr;

                listPtr = Marshal.AllocCoTaskMem(list.dwSize);
                Marshal.StructureToPtr(list, listPtr, false);

                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, listPtr, list.dwSize);
            }
            catch { }
            finally
            {
                if (urlPtr != IntPtr.Zero) Marshal.FreeHGlobal(urlPtr);
                if (optsPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(optsPtr);
                if (listPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(listPtr);
            }
        }

        public static void NotifyWinInetRefresh()
        {
            try
            {
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch { }
        }

        public static bool ApplyPubgHostsEntries()
        {
            if (!IsAdministrator()) return false;
            try
            {
                string hostsPath = GetHostsSystemPath();
                List<string> kept = new List<string>();
                FileAttributes origAttr = FileAttributes.Normal;
                bool wasReadOnly = false;

                if (File.Exists(hostsPath))
                {
                    origAttr = File.GetAttributes(hostsPath);
                    wasReadOnly = (origAttr & FileAttributes.ReadOnly) != 0;
                    if (wasReadOnly)
                    {
                        File.SetAttributes(hostsPath, origAttr & ~FileAttributes.ReadOnly);
                    }

                    string[] lines = File.ReadAllLines(hostsPath, Encoding.ASCII);
                    foreach (string l in lines)
                    {
                        if (l.IndexOf(HostsMarker, StringComparison.OrdinalIgnoreCase) < 0 &&
                            l.IndexOf(LegacyHostsMarker, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            kept.Add(l);
                        }
                    }
                }

                foreach (string d in SplitProxyEngine.PubgDomains)
                {
                    kept.Add(string.Format("127.0.0.1 {0} {1}", d, HostsMarker));
                }

                File.WriteAllLines(hostsPath, kept.ToArray(), Encoding.ASCII);
                if (wasReadOnly)
                {
                    try { File.SetAttributes(hostsPath, origAttr); } catch { }
                }
                FlushDns();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void RemovePubgHostsEntries()
        {
            if (!IsAdministrator()) return;
            try
            {
                string hostsPath = GetHostsSystemPath();
                if (!File.Exists(hostsPath)) return;

                string[] lines = File.ReadAllLines(hostsPath, Encoding.ASCII);
                List<string> kept = new List<string>();
                bool changed = false;
                foreach (string l in lines)
                {
                    if (l.IndexOf(HostsMarker, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        l.IndexOf(LegacyHostsMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        changed = true;
                    }
                    else
                    {
                        kept.Add(l);
                    }
                }

                if (changed)
                {
                    FileAttributes origAttr = File.GetAttributes(hostsPath);
                    bool wasReadOnly = (origAttr & FileAttributes.ReadOnly) != 0;
                    if (wasReadOnly) File.SetAttributes(hostsPath, origAttr & ~FileAttributes.ReadOnly);
                    File.WriteAllLines(hostsPath, kept.ToArray(), Encoding.ASCII);
                    if (wasReadOnly) try { File.SetAttributes(hostsPath, origAttr); } catch { }
                    FlushDns();
                }
            }
            catch { }
        }

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegRun, false))
                {
                    if (k == null) return false;
                    string val = k.GetValue(RunValueName) as string;
                    return !string.IsNullOrEmpty(val);
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SetAutoStart(bool enable, out string errorMsg)
        {
            errorMsg = null;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegRun, true))
                {
                    if (k == null)
                    {
                        errorMsg = "Không mở được khóa khởi động HKCU\\Run.";
                        return false;
                    }
                    if (enable)
                    {
                        string exePath = Assembly.GetExecutingAssembly().Location;
                        string cmd = string.Format("\"{0}\" --minimized", exePath);
                        k.SetValue(RunValueName, cmd, RegistryValueKind.String);
                    }
                    else
                    {
                        k.DeleteValue(RunValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return false;
            }
        }
    }
}
