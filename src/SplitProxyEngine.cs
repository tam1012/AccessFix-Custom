using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Threading;

namespace AccessFix
{
    public class DomainTestResult
    {
        public bool Success { get; set; }
        public string Domain { get; set; }
        public string ResolvedIp { get; set; }
        public long ElapsedMs { get; set; }
        public string Message { get; set; }
    }

    internal sealed class SplitClientHelloStream : Stream
    {
        private readonly NetworkStream inner;
        private readonly int splitMode;
        private bool firstTlsHandled;

        public SplitClientHelloStream(NetworkStream innerStream, int mode)
        {
            inner = innerStream;
            splitMode = mode;
            firstTlsHandled = false;
        }

        public override bool CanRead { get { return inner.CanRead; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return inner.CanWrite; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush() { inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) { return inner.Read(buffer, offset, count); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!firstTlsHandled && count >= 5 && buffer[offset] == 0x16)
            {
                firstTlsHandled = true;
                byte[] copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                SplitProxyEngine.SendSplitStatic(inner, copy, count, splitMode);
                return;
            }
            inner.Write(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }

    public class SplitProxyEngine
    {
        private sealed class DnsCacheItem
        {
            public List<IPAddress> Ips = new List<IPAddress>();
            public long ExpireTimestamp;
        }

        private sealed class DirectHttpClient : WebClient
        {
            public int TimeoutMs = 3000;
            public DirectHttpClient()
            {
                this.Proxy = null;
            }
            protected override WebRequest GetWebRequest(Uri address)
            {
                HttpWebRequest req = (HttpWebRequest)base.GetWebRequest(address);
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                req.Proxy = null;
                req.KeepAlive = true;
                req.UserAgent = "AccessFix/2.0";
                return req;
            }
        }

        private static readonly ConcurrentDictionary<string, DnsCacheItem> dnsCache =
            new ConcurrentDictionary<string, DnsCacheItem>(StringComparer.OrdinalIgnoreCase);

        public static readonly string[] PubgDomains = new string[]
        {
            "prod-live-front.playbattlegrounds.com",
            "prod-live-entry.playbattlegrounds.com",
            "prod-live-xenuine.playbattlegrounds.com",
            "xenuine.playbattlegrounds.com",
            "api.pubg.com",
            "accounts.pubg.com"
        };

        private static readonly string[] DohEndpoints = new string[]
        {
            "https://1.1.1.1/dns-query?name={0}&type=A",
            "https://8.8.8.8/resolve?name={0}&type=A",
            "https://cloudflare-dns.com/dns-query?name={0}&type=A",
            "https://dns.google/resolve?name={0}&type=A"
        };

        static SplitProxyEngine()
        {
            try
            {
                // Bật TLS 1.2 + TLS 1.3 (nếu hệ điều hành hỗ trợ), fallback an toàn về TLS 1.2 trên Windows 10 cũ
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            }
            catch
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            }
            try { ServicePointManager.DefaultConnectionLimit = 512; } catch { }
            try { ServicePointManager.Expect100Continue = false; } catch { }
        }

        public int Port { get; private set; }
        public volatile int SplitMode = 3;
        public bool HasDirect443 { get; private set; }
        public bool IsRunning { get { return running; } }

        private long totalConnections;
        private int activeConnections;
        private volatile string lastConnectedHost = "";
        private volatile byte[] pacBytes = new byte[0];

        public long TotalConnections { get { return Interlocked.Read(ref totalConnections); } }
        public int ActiveConnections { get { return Volatile.Read(ref activeConnections); } }
        public string LastConnectedHost { get { return lastConnectedHost; } }

        private TcpListener listenerProxy;
        private TcpListener listenerDirectV4;
        private TcpListener listenerDirectV6;
        private volatile bool running;

        public void UpdatePacContent(string pacScript)
        {
            pacBytes = Encoding.UTF8.GetBytes(pacScript ?? "");
        }

        public bool Start(int preferredPort, string initialPac, int mode, bool enableDirect443, out string errorMessage)
        {
            errorMessage = null;
            if (running)
            {
                SplitMode = mode;
                UpdatePacContent(initialPac);
                return true;
            }

            SplitMode = mode;
            UpdatePacContent(initialPac);

            TcpListener boundListener = null;
            int boundPort = 0;
            for (int p = preferredPort; p <= preferredPort + 15; p++)
            {
                try
                {
                    TcpListener candidate = new TcpListener(IPAddress.Loopback, p);
                    candidate.Start();
                    boundListener = candidate;
                    boundPort = p;
                    break;
                }
                catch { }
            }

            if (boundListener == null)
            {
                errorMessage = string.Format("Không mở được cổng nội bộ trong dải {0}-{1}.", preferredPort, preferredPort + 15);
                return false;
            }

            listenerProxy = boundListener;
            Port = boundPort;
            running = true;

            Thread proxyThread = new Thread(AcceptLoopProxy);
            proxyThread.IsBackground = true;
            proxyThread.Name = "AccessFix-ProxyAccept";
            proxyThread.Start();

            HasDirect443 = false;
            if (enableDirect443)
            {
                TryStartDirect443();
                WarmUpDomainsAsync(PubgDomains);
            }

            return true;
        }

        private void TryStartDirect443()
        {
            try
            {
                listenerDirectV4 = new TcpListener(IPAddress.Loopback, 443);
                try { listenerDirectV4.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }
                listenerDirectV4.Start();
                HasDirect443 = true;

                Thread t4 = new Thread(AcceptLoopDirectV4);
                t4.IsBackground = true;
                t4.Name = "AccessFix-Direct443v4";
                t4.Start();
            }
            catch
            {
                HasDirect443 = false;
                listenerDirectV4 = null;
            }

            if (HasDirect443 && Socket.OSSupportsIPv6)
            {
                try
                {
                    listenerDirectV6 = new TcpListener(IPAddress.IPv6Loopback, 443);
                    try { listenerDirectV6.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }
                    listenerDirectV6.Start();

                    Thread t6 = new Thread(AcceptLoopDirectV6);
                    t6.IsBackground = true;
                    t6.Name = "AccessFix-Direct443v6";
                    t6.Start();
                }
                catch
                {
                    listenerDirectV6 = null;
                }
            }
        }

        public void Stop()
        {
            running = false;
            try { if (listenerProxy != null) listenerProxy.Stop(); } catch { }
            try { if (listenerDirectV4 != null) listenerDirectV4.Stop(); } catch { }
            try { if (listenerDirectV6 != null) listenerDirectV6.Stop(); } catch { }
            listenerProxy = null;
            listenerDirectV4 = null;
            listenerDirectV6 = null;
            HasDirect443 = false;
        }

        public static void ClearDnsCache()
        {
            dnsCache.Clear();
        }

        public static void WarmUpDomainsAsync(IEnumerable<string> domains)
        {
            if (domains == null) return;
            List<string> copy = new List<string>(domains);
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (string d in copy)
                {
                    if (!string.IsNullOrWhiteSpace(d))
                    {
                        try { ResolveSafeAll(d, false); } catch { }
                    }
                }
            });
        }

        private static bool IsUsablePublicIpv4(IPAddress ip)
        {
            if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) return false;
            if (IPAddress.IsLoopback(ip)) return false;
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            return true;
        }

        private static void ParseDohJsonIps(string json, List<IPAddress> list)
        {
            if (string.IsNullOrEmpty(json)) return;
            int idx = json.IndexOf("\"data\"", StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                int colon = json.IndexOf(':', idx + 6);
                if (colon < 0) break;
                int q1 = json.IndexOf('"', colon + 1);
                if (q1 < 0) break;
                int q2 = json.IndexOf('"', q1 + 1);
                if (q2 < 0) break;

                string ipStr = json.Substring(q1 + 1, q2 - q1 - 1).Trim();
                IPAddress parsed;
                if (IPAddress.TryParse(ipStr, out parsed) && IsUsablePublicIpv4(parsed))
                {
                    if (!list.Contains(parsed)) list.Add(parsed);
                }
                idx = json.IndexOf("\"data\"", q2 + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static IPAddress[] ResolveSafeAll(string host, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(host)) return new IPAddress[0];
            host = host.Trim();

            IPAddress literalIp;
            if (IPAddress.TryParse(host, out literalIp))
            {
                return new IPAddress[] { literalIp };
            }

            long nowTicks = Stopwatch.GetTimestamp();
            DnsCacheItem cached;
            if (!forceRefresh && dnsCache.TryGetValue(host, out cached) && cached != null)
            {
                if (nowTicks < cached.ExpireTimestamp)
                {
                    lock (cached.Ips)
                    {
                        if (cached.Ips.Count > 0) return cached.Ips.ToArray();
                    }
                }
            }

            List<IPAddress> resolved = new List<IPAddress>();

            // 1. Ưu tiên DoH qua IP trực tiếp (1.1.1.1 / 8.8.8.8) để chống nhà mạng đầu độc DNS mà không cần đổi DNS máy
            for (int i = 0; i < DohEndpoints.Length && resolved.Count == 0; i++)
            {
                try
                {
                    using (DirectHttpClient wc = new DirectHttpClient())
                    {
                        wc.TimeoutMs = 2800;
                        wc.Headers[HttpRequestHeader.Accept] = "application/dns-json";
                        string url = string.Format(DohEndpoints[i], Uri.EscapeDataString(host));
                        string json = wc.DownloadString(url);
                        ParseDohJsonIps(json, resolved);
                    }
                }
                catch { }
            }

            // 2. Chỉ khi mọi kênh DoH đều lỗi mới hỏi OS DNS
            if (resolved.Count == 0)
            {
                try
                {
                    IPAddress[] sysIps = Dns.GetHostAddresses(host);
                    foreach (IPAddress a in sysIps)
                    {
                        if (IsUsablePublicIpv4(a) && !resolved.Contains(a))
                        {
                            resolved.Add(a);
                        }
                    }
                }
                catch { }
            }

            if (resolved.Count > 0)
            {
                long ttlTicks = Stopwatch.Frequency * 900L; // Cache 15 phút
                dnsCache[host] = new DnsCacheItem
                {
                    Ips = resolved,
                    ExpireTimestamp = nowTicks + ttlTicks
                };
                return resolved.ToArray();
            }

            // Nếu mất mạng tạm thời, dùng lại cache cũ nếu có
            if (dnsCache.TryGetValue(host, out cached) && cached != null)
            {
                lock (cached.Ips)
                {
                    if (cached.Ips.Count > 0) return cached.Ips.ToArray();
                }
            }

            return new IPAddress[0];
        }

        public static void InvalidateHostIp(string host, IPAddress failedIp)
        {
            if (string.IsNullOrEmpty(host) || failedIp == null) return;
            DnsCacheItem entry;
            if (dnsCache.TryGetValue(host, out entry) && entry != null && entry.Ips != null)
            {
                lock (entry.Ips)
                {
                    if (entry.Ips.Count > 1 && entry.Ips.Remove(failedIp))
                    {
                        entry.Ips.Add(failedIp);
                    }
                }
            }
        }

        public static void PromoteWorkingIp(string host, IPAddress workingIp)
        {
            if (string.IsNullOrEmpty(host) || workingIp == null) return;
            DnsCacheItem entry;
            if (dnsCache.TryGetValue(host, out entry) && entry != null && entry.Ips != null)
            {
                lock (entry.Ips)
                {
                    if (entry.Ips.Count > 1 && entry.Ips[0] != workingIp && entry.Ips.Remove(workingIp))
                    {
                        entry.Ips.Insert(0, workingIp);
                    }
                }
            }
        }

        private static void SetTcpKeepAlive(Socket s, uint timeMs, uint intervalMs)
        {
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                byte[] opt = new byte[12];
                BitConverter.GetBytes((uint)1).CopyTo(opt, 0);
                BitConverter.GetBytes(timeMs).CopyTo(opt, 4);
                BitConverter.GetBytes(intervalMs).CopyTo(opt, 8);
                s.IOControl(IOControlCode.KeepAliveValues, opt, null);
            }
            catch { }
        }

        private static TcpClient ConnectWithFailover(string host, int port, int perIpTimeoutMs, out IPAddress connectedIp)
        {
            connectedIp = null;
            IPAddress[] ips = ResolveSafeAll(host, false);
            if (ips.Length == 0) return null;

            foreach (IPAddress ip in ips)
            {
                TcpClient server = null;
                try
                {
                    server = new TcpClient();
                    server.NoDelay = true;
                    SetTcpKeepAlive(server.Client, 15000, 3000);
                    IAsyncResult ar = server.BeginConnect(ip, port, null, null);
                    if (ar.AsyncWaitHandle.WaitOne(perIpTimeoutMs, false))
                    {
                        server.EndConnect(ar);
                        if (server.Connected)
                        {
                            connectedIp = ip;
                            PromoteWorkingIp(host, ip);
                            return server;
                        }
                    }
                    try { server.Close(); } catch { }
                    InvalidateHostIp(host, ip);
                }
                catch
                {
                    try { if (server != null) server.Close(); } catch { }
                    InvalidateHostIp(host, ip);
                }
            }
            return null;
        }

        public static int FindSniOffset(byte[] d, int len, out string sniHost, out int nameLen)
        {
            sniHost = null;
            nameLen = 0;
            try
            {
                if (len < 43 || d[0] != 0x16 || d[5] != 0x01) return -1;
                int p = 5 + 4 + 2 + 32;
                if (p >= len) return -1;
                p += 1 + d[p]; // Session ID
                if (p + 2 > len) return -1;
                p += 2 + ((d[p] << 8) | d[p + 1]); // Cipher Suites
                if (p + 1 > len) return -1;
                p += 1 + d[p]; // Compression Methods
                if (p + 2 > len) return -1;
                int extEnd = p + 2 + ((d[p] << 8) | d[p + 1]);
                p += 2;
                while (p + 4 <= extEnd && p + 4 <= len)
                {
                    int extType = (d[p] << 8) | d[p + 1];
                    int extLen = (d[p + 2] << 8) | d[p + 3];
                    if (extType == 0)
                    {
                        int q = p + 4 + 2 + 1;
                        if (q + 2 <= len)
                        {
                            nameLen = (d[q] << 8) | d[q + 1];
                            if (nameLen > 0 && q + 2 + nameLen <= len)
                            {
                                sniHost = Encoding.ASCII.GetString(d, q + 2, nameLen);
                                return q + 2;
                            }
                        }
                        return -1;
                    }
                    p += 4 + extLen;
                }
            }
            catch { }
            return -1;
        }

        public static void SendSplitStatic(NetworkStream ss, byte[] d, int len, int mode)
        {
            string sniHost;
            int nameLen;
            int sniOffset = FindSniOffset(d, len, out sniHost, out nameLen);

            int mid = (sniOffset > 0 && nameLen > 1) ? (sniOffset + nameLen / 2) : Math.Min(2, len - 1);
            int first = (sniOffset > 0) ? (sniOffset + 1) : 1;

            int[] cuts;
            if (mode == 2) cuts = new int[] { 1 };
            else if (mode == 4) cuts = new int[] { first };
            else cuts = new int[] { 1, mid }; // Mode 3 mặc định: cắt byte đầu + cắt đôi giữa tên miền SNI

            int pos = 0;
            for (int i = 0; i < cuts.Length; i++)
            {
                int c = cuts[i];
                if (c <= pos || c >= len) continue;
                ss.Write(d, pos, c - pos);
                ss.Flush();
                Thread.Sleep(10);
                pos = c;
            }
            if (pos < len)
            {
                ss.Write(d, pos, len - pos);
                ss.Flush();
            }
        }

        private void AcceptLoopProxy()
        {
            while (running)
            {
                try
                {
                    TcpClient c = listenerProxy.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleProxyClient, c);
                }
                catch
                {
                    if (!running) return;
                }
            }
        }

        private void AcceptLoopDirectV4()
        {
            while (running)
            {
                try
                {
                    TcpClient c = listenerDirectV4.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleDirectClient, c);
                }
                catch
                {
                    if (!running) return;
                }
            }
        }

        private void AcceptLoopDirectV6()
        {
            while (running)
            {
                try
                {
                    TcpClient c = listenerDirectV6.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleDirectClient, c);
                }
                catch
                {
                    if (!running) return;
                }
            }
        }

        private static int FindHttpHeaderEnd(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
            {
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            }
            return -1;
        }

        private static void SplitHostPort(string hp, int defaultPort, out string host, out int port)
        {
            port = defaultPort;
            if (string.IsNullOrEmpty(hp)) { host = ""; return; }
            int idx = hp.LastIndexOf(':');
            if (hp.StartsWith("["))
            {
                int close = hp.IndexOf(']');
                if (close > 1)
                {
                    host = hp.Substring(1, close - 1);
                    if (idx > close && idx + 1 < hp.Length) int.TryParse(hp.Substring(idx + 1), out port);
                    return;
                }
            }
            if (idx > 0 && idx + 1 < hp.Length)
            {
                host = hp.Substring(0, idx);
                int.TryParse(hp.Substring(idx + 1), out port);
            }
            else
            {
                host = hp;
            }
        }

        private static void PumpStream(Stream from, Stream to, TcpClient dstClient)
        {
            byte[] buf = new byte[32768];
            try
            {
                int n;
                while ((n = from.Read(buf, 0, buf.Length)) > 0)
                {
                    to.Write(buf, 0, n);
                }
            }
            catch { }
            try
            {
                if (dstClient != null && dstClient.Connected)
                {
                    dstClient.Client.Shutdown(SocketShutdown.Send);
                }
            }
            catch { }
        }

        private void HandleProxyClient(object state)
        {
            TcpClient client = (TcpClient)state;
            TcpClient server = null;
            bool countedActive = false;

            try
            {
                client.NoDelay = true;
                SetTcpKeepAlive(client.Client, 15000, 3000);
                client.ReceiveTimeout = 15000;
                NetworkStream cs = client.GetStream();

                byte[] buf = new byte[16384];
                int len = 0;
                int he = -1;
                while (he < 0)
                {
                    if (len >= buf.Length) return;
                    int n = cs.Read(buf, len, buf.Length - len);
                    if (n <= 0) return;
                    len += n;
                    he = FindHttpHeaderEnd(buf, len);
                }

                string head = Encoding.ASCII.GetString(buf, 0, he);
                string firstLine = head.Split('\n')[0].Trim();
                string[] parts = firstLine.Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0].ToUpperInvariant();

                // Phục vụ file PAC động tại /proxy.pac (kể cả khi có query ?v=...)
                if (method == "GET" && parts[1].StartsWith("/proxy.pac", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] currentPac = pacBytes;
                    string hdr = "HTTP/1.1 200 OK\r\n" +
                                 "Content-Type: application/x-ns-proxy-autoconfig\r\n" +
                                 "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                                 "Content-Length: " + currentPac.Length + "\r\n" +
                                 "Connection: close\r\n\r\n";
                    byte[] hb = Encoding.ASCII.GetBytes(hdr);
                    cs.Write(hb, 0, hb.Length);
                    cs.Write(currentPac, 0, currentPac.Length);
                    cs.Flush();
                    return;
                }

                // Xử lý HTTP thường (không phải CONNECT)
                if (method != "CONNECT")
                {
                    string targetHost = "";
                    int targetPort = 80;
                    Uri absUri;
                    if (Uri.TryCreate(parts[1], UriKind.Absolute, out absUri))
                    {
                        targetHost = absUri.Host;
                        targetPort = absUri.Port;
                    }
                    else
                    {
                        int hi = head.IndexOf("Host:", StringComparison.OrdinalIgnoreCase);
                        if (hi >= 0)
                        {
                            int he2 = head.IndexOf("\r\n", hi);
                            string hp = (he2 > hi) ? head.Substring(hi + 5, he2 - (hi + 5)).Trim() : head.Substring(hi + 5).Trim();
                            SplitHostPort(hp, 80, out targetHost, out targetPort);
                        }
                    }

                    if (string.IsNullOrEmpty(targetHost)) return;

                    IPAddress connectedHttpIp;
                    server = ConnectWithFailover(targetHost, targetPort, 3500, out connectedHttpIp);
                    if (server == null) return;

                    Interlocked.Increment(ref totalConnections);
                    Interlocked.Increment(ref activeConnections);
                    countedActive = true;
                    lastConnectedHost = targetHost;

                    NetworkStream ssHttp = server.GetStream();
                    ssHttp.Write(buf, 0, len);
                    ssHttp.Flush();
                    client.ReceiveTimeout = 0;

                    TcpClient svHttp = server;
                    TcpClient clHttp = client;
                    Thread tHttp = new Thread((ThreadStart)delegate { PumpStream(ssHttp, cs, clHttp); });
                    tHttp.IsBackground = true;
                    tHttp.Start();
                    PumpStream(cs, ssHttp, svHttp);
                    try { tHttp.Join(3000); } catch { }
                    return;
                }

                // Xử lý HTTPS CONNECT với Multi-IP Failover
                string host;
                int port;
                SplitHostPort(parts[1], 443, out host, out port);
                if (string.IsNullOrEmpty(host)) return;

                IPAddress connectedIp;
                server = ConnectWithFailover(host, port, 3500, out connectedIp);
                if (server == null)
                {
                    byte[] badGw = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n");
                    try { cs.Write(badGw, 0, badGw.Length); } catch { }
                    return;
                }

                byte[] okResp = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                cs.Write(okResp, 0, okResp.Length);
                cs.Flush();

                Interlocked.Increment(ref totalConnections);
                Interlocked.Increment(ref activeConnections);
                countedActive = true;
                lastConnectedHost = host;

                NetworkStream ss = server.GetStream();
                byte[] first = new byte[16384];
                client.ReceiveTimeout = 15000;
                int fl = cs.Read(first, 0, first.Length);
                if (fl <= 0) return;

                // Đọc trọn vẹn TLS Record đầu tiên (ClientHello) trước khi cắt SNI
                if (first[0] == 0x16 && fl >= 5)
                {
                    int recordTotal = 5 + ((first[3] << 8) | first[4]);
                    while (fl < recordTotal && fl < first.Length)
                    {
                        int n2 = cs.Read(first, fl, first.Length - fl);
                        if (n2 <= 0) break;
                        fl += n2;
                    }
                    SendSplitStatic(ss, first, fl, SplitMode);
                }
                else
                {
                    ss.Write(first, 0, fl);
                    ss.Flush();
                }

                client.ReceiveTimeout = 0;

                TcpClient sv = server;
                TcpClient cl = client;
                Thread t = new Thread((ThreadStart)delegate { PumpStream(ss, cs, cl); });
                t.IsBackground = true;
                t.Start();
                PumpStream(cs, ss, sv);
                try { t.Join(3000); } catch { }
            }
            catch { }
            finally
            {
                if (countedActive) Interlocked.Decrement(ref activeConnections);
                try { client.Close(); } catch { }
                try { if (server != null) server.Close(); } catch { }
            }
        }

        private void HandleDirectClient(object state)
        {
            TcpClient client = (TcpClient)state;
            TcpClient server = null;
            bool countedActive = false;

            try
            {
                client.NoDelay = true;
                SetTcpKeepAlive(client.Client, 15000, 3000);
                client.ReceiveTimeout = 15000;
                NetworkStream cs = client.GetStream();

                byte[] first = new byte[16384];
                int fl = cs.Read(first, 0, first.Length);
                if (fl <= 0) return;

                if (first[0] == 0x16 && fl >= 5)
                {
                    int need = 5 + ((first[3] << 8) | first[4]);
                    while (fl < need && fl < first.Length)
                    {
                        int n2 = cs.Read(first, fl, first.Length - fl);
                        if (n2 <= 0) break;
                        fl += n2;
                    }
                }

                string sniHost;
                int nameLen;
                FindSniOffset(first, fl, out sniHost, out nameLen);
                if (string.IsNullOrEmpty(sniHost))
                {
                    sniHost = PubgDomains[0];
                }

                IPAddress connectedIp;
                server = ConnectWithFailover(sniHost, 443, 3500, out connectedIp);
                if (server == null) return;

                Interlocked.Increment(ref totalConnections);
                Interlocked.Increment(ref activeConnections);
                countedActive = true;
                lastConnectedHost = sniHost;

                NetworkStream ss = server.GetStream();
                if (first[0] == 0x16 && fl >= 5)
                {
                    SendSplitStatic(ss, first, fl, SplitMode);
                }
                else
                {
                    ss.Write(first, 0, fl);
                    ss.Flush();
                }

                client.ReceiveTimeout = 0;

                TcpClient sv = server;
                TcpClient cl = client;
                Thread t = new Thread((ThreadStart)delegate { PumpStream(ss, cs, cl); });
                t.IsBackground = true;
                t.Start();
                PumpStream(cs, ss, sv);
                try { t.Join(3000); } catch { }
            }
            catch { }
            finally
            {
                if (countedActive) Interlocked.Decrement(ref activeConnections);
                try { client.Close(); } catch { }
                try { if (server != null) server.Close(); } catch { }
            }
        }

        private static List<string> BuildProbeHostsForDomain(string norm)
        {
            List<string> list = new List<string>();
            if (string.Equals(norm, "licdn.com", StringComparison.OrdinalIgnoreCase))
            {
                list.Add("static.licdn.com");
                list.Add("media.licdn.com");
            }
            else if (string.Equals(norm, "quoracdn.net", StringComparison.OrdinalIgnoreCase))
            {
                list.Add("qph.cf2.quoracdn.net");
                list.Add("qsf.fs.quoracdn.net");
            }
            else if (string.Equals(norm, "steamstatic.com", StringComparison.OrdinalIgnoreCase))
            {
                list.Add("shared.akamai.steamstatic.com");
            }
            else if (string.Equals(norm, "bbci.co.uk", StringComparison.OrdinalIgnoreCase))
            {
                list.Add("ichef.bbci.co.uk");
            }
            else if (string.Equals(norm, "discordapp.net", StringComparison.OrdinalIgnoreCase))
            {
                list.Add("media.discordapp.net");
            }
            else if (string.Equals(norm, "redditmedia.com", StringComparison.OrdinalIgnoreCase))
            {
                list.Add("styles.redditmedia.com");
            }

            list.Add(norm);
            if (!norm.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                list.Add("www." + norm);
            }
            return list;
        }

        public static DomainTestResult TestDomainConnection(string domainInput, int splitMode)
        {
            string norm = AppConfig.NormalizeDomain(domainInput);
            if (string.IsNullOrEmpty(norm))
            {
                return new DomainTestResult
                {
                    Success = false,
                    Domain = domainInput ?? "",
                    Message = "Tên miền không hợp lệ"
                };
            }

            List<string> candidates = BuildProbeHostsForDomain(norm);
            DomainTestResult lastFailure = null;

            for (int c = 0; c < candidates.Count; c++)
            {
                string host = candidates[c];
                Stopwatch sw = Stopwatch.StartNew();

                IPAddress[] ips = ResolveSafeAll(host, false);
                if (ips.Length == 0)
                {
                    lastFailure = new DomainTestResult
                    {
                        Success = false,
                        Domain = norm,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Message = "Không phân giải được địa chỉ IP (DoH)"
                    };
                    continue;
                }

                TcpClient tcp = null;
                try
                {
                    IPAddress connectedIp;
                    tcp = ConnectWithFailover(host, 443, 3200, out connectedIp);
                    if (tcp == null || connectedIp == null)
                    {
                        lastFailure = new DomainTestResult
                        {
                            Success = false,
                            Domain = norm,
                            ResolvedIp = ips[0].ToString(),
                            ElapsedMs = sw.ElapsedMilliseconds,
                            Message = string.Format("Không kết nối được TCP 443 ({0}) - có thể bị chặn IP", ips[0])
                        };
                        continue;
                    }

                    tcp.ReceiveTimeout = 4500;
                    tcp.SendTimeout = 4500;

                    SplitClientHelloStream splitStream = new SplitClientHelloStream(tcp.GetStream(), splitMode);
                    using (SslStream ssl = new SslStream(splitStream, false))
                    {
                        SslProtocols protos = SslProtocols.Tls12;
                        try { protos = SslProtocols.Tls12 | (SslProtocols)12288; } catch { }
                        ssl.AuthenticateAsClient(host, null, protos, false);

                        sw.Stop();
                        return new DomainTestResult
                        {
                            Success = true,
                            Domain = norm,
                            ResolvedIp = connectedIp.ToString(),
                            ElapsedMs = sw.ElapsedMilliseconds,
                            Message = string.Format("Vượt chặn OK ({0} ms • IP {1})", sw.ElapsedMilliseconds, connectedIp)
                        };
                    }
                }
                catch (AuthenticationException aex)
                {
                    sw.Stop();
                    lastFailure = new DomainTestResult
                    {
                        Success = false,
                        Domain = norm,
                        ResolvedIp = ips[0].ToString(),
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Message = "Lỗi xác thực chứng chỉ SSL: " + aex.Message
                    };
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    lastFailure = new DomainTestResult
                    {
                        Success = false,
                        Domain = norm,
                        ResolvedIp = ips[0].ToString(),
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Message = "Lỗi kết nối TLS: " + ex.Message
                    };
                }
                finally
                {
                    try { if (tcp != null) tcp.Close(); } catch { }
                }
            }

            return lastFailure ?? new DomainTestResult
            {
                Success = false,
                Domain = norm,
                Message = "Kiểm tra thất bại"
            };
        }
    }
}
