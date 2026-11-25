using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace AccessFix
{
    public class DomainEntry
    {
        public string Domain { get; set; }
        public bool Enabled { get; set; }
        public string Group { get; set; }

        public DomainEntry()
        {
            Domain = "";
            Enabled = true;
            Group = "Tùy chỉnh";
        }

        public DomainEntry(string domain, bool enabled, string groupName)
        {
            Domain = domain;
            Enabled = enabled;
            Group = groupName;
        }
    }

    public class QuickPreset
    {
        public string Label { get; set; }
        public string GroupName { get; set; }
        public string[] Domains { get; set; }

        public QuickPreset(string label, string groupName, string[] domains)
        {
            Label = label;
            GroupName = groupName;
            Domains = domains;
        }
    }

    public class AppConfig
    {
        public int ProxyPort { get; set; }
        public int SplitMode { get; set; }
        public bool IncludeSteamGames { get; set; }
        public bool EnablePubgDirect443 { get; set; }
        public bool MinimizeToTrayOnClose { get; set; }
        public bool AutoEnableOnLaunch { get; set; }
        public List<DomainEntry> Domains { get; set; }

        public AppConfig()
        {
            ProxyPort = 18080;
            SplitMode = 3; // Mode 3: Cắt byte đầu + giữa tên miền SNI (tốt nhất cho cả Web & Steam)
            IncludeSteamGames = true;
            EnablePubgDirect443 = false; // Mặc định tắt để không chiếm cổng 443 và không đòi quyền Admin
            MinimizeToTrayOnClose = true;
            AutoEnableOnLaunch = true;
            Domains = new List<DomainEntry>();
        }

        public static readonly string[] BuiltInSteamGameDomains = new string[]
        {
            "steampowered.com", "steamcommunity.com", "steamstatic.com",
            "steam-chat.com", "steamusercontent.com", "steamgames.com",
            "steamcontent.com", "steamserver.net", "valvesoftware.com",
            "counter-strike.net", "dota2.com",
            "playbattlegrounds.com", "pubg.com", "krafton.com", "pubgapp.com", "pubgesports.com",
            "narakathegame.com", "24entertainment.net",
            "playdeltaforce.com", "levelinfinite.com"
        };

        public static readonly QuickPreset[] QuickPresets = new QuickPreset[]
        {
            new QuickPreset("+ LinkedIn", "LinkedIn", new string[] { "linkedin.com", "licdn.com", "lnkd.in" }),
            new QuickPreset("+ Quora", "Quora", new string[] { "quora.com", "quoracdn.net" }),
            new QuickPreset("+ Medium", "Medium", new string[] { "medium.com", "miro.medium.com" }),
            new QuickPreset("+ Reddit", "Reddit", new string[] { "reddit.com", "redd.it", "redditmedia.com", "redditstatic.com" }),
            new QuickPreset("+ BBC", "BBC", new string[] { "bbc.com", "bbc.co.uk", "bbci.co.uk" }),
            new QuickPreset("+ Telegram", "Telegram", new string[] { "telegram.org", "t.me", "telesco.pe" }),
            new QuickPreset("+ Discord", "Discord", new string[] { "discord.com", "discordapp.com", "discordapp.net", "discord.gg" })
        };

        public static AppConfig CreateDefault()
        {
            AppConfig cfg = new AppConfig();
            cfg.Domains.Add(new DomainEntry("linkedin.com", true, "LinkedIn"));
            cfg.Domains.Add(new DomainEntry("licdn.com", true, "LinkedIn (Ảnh/CSS/JS)"));
            cfg.Domains.Add(new DomainEntry("lnkd.in", true, "LinkedIn (Link rút gọn)"));
            cfg.Domains.Add(new DomainEntry("quora.com", true, "Quora"));
            cfg.Domains.Add(new DomainEntry("quoracdn.net", true, "Quora (Ảnh/CDN)"));
            return cfg;
        }

        public static string NormalizeDomain(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim().ToLowerInvariant();

            // Nếu người dùng dán cả URL như https://www.linkedin.com/in/abc?x=1
            int schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0)
            {
                s = s.Substring(schemeIdx + 3);
            }

            // Bỏ phần path, query, fragment
            int slashIdx = s.IndexOfAny(new char[] { '/', '?', '#' });
            if (slashIdx >= 0)
            {
                s = s.Substring(0, slashIdx);
            }

            // Bỏ user:pass@ nếu có
            int atIdx = s.LastIndexOf('@');
            if (atIdx >= 0)
            {
                s = s.Substring(atIdx + 1);
            }

            // Bỏ port :443
            int colonIdx = s.IndexOf(':');
            if (colonIdx >= 0)
            {
                s = s.Substring(0, colonIdx);
            }

            // Bỏ ký tự wildcard đầu kiểu *.example.com hoặc .example.com
            while (s.StartsWith("*.") || s.StartsWith("."))
            {
                if (s.StartsWith("*.")) s = s.Substring(2);
                else if (s.StartsWith(".")) s = s.Substring(1);
            }

            // Bỏ www. ở đầu để bao phủ cả domain gốc lẫn mọi subdomain (*.domain.com)
            if (s.StartsWith("www.") && s.Length > 4 && s.Substring(4).Contains("."))
            {
                s = s.Substring(4);
            }

            s = s.Trim().Trim('.');
            if (string.IsNullOrEmpty(s) || !s.Contains(".")) return null;

            // Kiểm tra ký tự hợp lệ của hostname
            foreach (char c in s)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.';
                if (!ok) return null;
            }
            if (s.Contains("..")) return null;

            return s;
        }

        public List<string> GetEffectiveDomains()
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> result = new List<string>();

            if (Domains != null)
            {
                foreach (DomainEntry d in Domains)
                {
                    if (d != null && d.Enabled && !string.IsNullOrWhiteSpace(d.Domain))
                    {
                        string norm = NormalizeDomain(d.Domain);
                        if (!string.IsNullOrEmpty(norm) && seen.Add(norm))
                        {
                            result.Add(norm);
                        }
                    }
                }
            }

            if (IncludeSteamGames)
            {
                foreach (string gd in BuiltInSteamGameDomains)
                {
                    if (seen.Add(gd))
                    {
                        result.Add(gd);
                    }
                }
            }

            return result;
        }

        private static string cachedStorageDir = null;

        public static string GetStorageDirectory()
        {
            if (cachedStorageDir != null) return cachedStorageDir;

            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
                string exeDir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    // Kiểm tra quyền ghi ở thư mục chứa file exe (chế độ Portable mang sang máy khác)
                    string probeFile = Path.Combine(exeDir, ".af_write_test_" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllText(probeFile, "ok");
                    File.Delete(probeFile);
                    cachedStorageDir = exeDir;
                    return cachedStorageDir;
                }
            }
            catch
            {
                // Nếu để trong Program Files không có quyền ghi thì lưu vào %AppData%\AccessFix
            }

            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AccessFix");
            try { Directory.CreateDirectory(appData); } catch { }
            cachedStorageDir = appData;
            return cachedStorageDir;
        }

        public static string GetConfigFilePath()
        {
            return Path.Combine(GetStorageDirectory(), "AccessFix.config.json");
        }

        public static AppConfig Load()
        {
            string path = GetConfigFilePath();
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    AppConfig loaded = ser.Deserialize<AppConfig>(json);
                    if (loaded != null)
                    {
                        if (loaded.Domains == null) loaded.Domains = new List<DomainEntry>();
                        if (loaded.ProxyPort < 1024 || loaded.ProxyPort > 65500) loaded.ProxyPort = 18080;
                        if (loaded.SplitMode < 2 || loaded.SplitMode > 4) loaded.SplitMode = 3;
                        return loaded;
                    }
                }
                catch { }
            }

            AppConfig def = CreateDefault();
            def.Save();
            return def;
        }

        public void Save()
        {
            try
            {
                string path = GetConfigFilePath();
                string pretty = SerializePretty(this);
                File.WriteAllText(path, pretty, new UTF8Encoding(false));
            }
            catch { }
        }

        private static string SerializePretty(AppConfig cfg)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            string raw = ser.Serialize(cfg);
            // Định dạng JSON có thụt lề để dễ đọc nếu mở bằng Notepad
            StringBuilder sb = new StringBuilder();
            bool inQuotes = false;
            bool escaped = false;
            int indent = 0;
            foreach (char ch in raw)
            {
                if (escaped)
                {
                    sb.Append(ch);
                    escaped = false;
                    continue;
                }
                if (ch == '\\' && inQuotes)
                {
                    sb.Append(ch);
                    escaped = true;
                    continue;
                }
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    sb.Append(ch);
                    continue;
                }
                if (!inQuotes)
                {
                    if (ch == '{' || ch == '[')
                    {
                        sb.Append(ch);
                        sb.Append("\r\n");
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                        continue;
                    }
                    if (ch == '}' || ch == ']')
                    {
                        sb.Append("\r\n");
                        indent = Math.Max(0, indent - 1);
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(ch);
                        continue;
                    }
                    if (ch == ',')
                    {
                        sb.Append(ch);
                        sb.Append("\r\n");
                        sb.Append(new string(' ', indent * 2));
                        continue;
                    }
                    if (ch == ':')
                    {
                        sb.Append(": ");
                        continue;
                    }
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
