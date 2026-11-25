using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AccessFix
{
    internal static class Program
    {
        private const string MutexName = @"Local\AccessFix_SingleInstance_v2";
        private const string ShowEventName = @"Local\AccessFix_ShowWindow_v2";

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private static MainForm mainFormInstance;

        [STAThread]
        private static void Main(string[] args)
        {
            bool startMinimized = false;
            bool cliResetOnly = false;

            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase))
                    {
                        startMinimized = true;
                    }
                    else if (string.Equals(a, "--reset", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "/reset", StringComparison.OrdinalIgnoreCase))
                    {
                        cliResetOnly = true;
                    }
                }
            }

            if (cliResetOnly)
            {
                SystemProxyManager.RemovePubgHostsEntries();
                SystemProxyManager.RestoreSystemProxy();
                return;
            }

            bool createdNew = true;
            Mutex instanceMutex = null;
            try
            {
                instanceMutex = new Mutex(true, MutexName, out createdNew);
            }
            catch
            {
                createdNew = true;
            }

            if (!createdNew)
            {
                // Đánh thức cửa sổ đang chạy ẩn dưới khay hệ thống lên tiền cảnh
                try
                {
                    using (EventWaitHandle evt = EventWaitHandle.OpenExisting(ShowEventName))
                    {
                        evt.Set();
                    }
                }
                catch { }
                return;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Đảm bảo luôn gỡ PAC trả lại mạng gốc khi tắt máy Windows hoặc gặp lỗi bất ngờ
            SystemEvents.SessionEnding += delegate
            {
                try
                {
                    if (mainFormInstance != null) mainFormInstance.StopBypass();
                    else SystemProxyManager.RestoreSystemProxy();
                }
                catch { }
            };

            AppDomain.CurrentDomain.ProcessExit += delegate
            {
                try
                {
                    SystemProxyManager.RemovePubgHostsEntries();
                    SystemProxyManager.RestoreSystemProxy();
                }
                catch { }
            };

            AppDomain.CurrentDomain.UnhandledException += delegate
            {
                try
                {
                    SystemProxyManager.RemovePubgHostsEntries();
                    SystemProxyManager.RestoreSystemProxy();
                }
                catch { }
            };

            EventWaitHandle showSignal = null;
            try
            {
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                Thread listener = new Thread((ThreadStart)delegate
                {
                    while (true)
                    {
                        try
                        {
                            showSignal.WaitOne();
                            MainForm f = mainFormInstance;
                            if (f != null && !f.IsDisposed && f.IsHandleCreated)
                            {
                                f.BeginInvoke(new MethodInvoker(delegate { f.RestoreFromTray(); }));
                            }
                        }
                        catch { break; }
                    }
                });
                listener.IsBackground = true;
                listener.Start();
            }
            catch { }

            try
            {
                mainFormInstance = new MainForm(startMinimized);
                Application.Run(mainFormInstance);
            }
            finally
            {
                try { if (showSignal != null) showSignal.Close(); } catch { }
                try
                {
                    if (instanceMutex != null)
                    {
                        instanceMutex.ReleaseMutex();
                        instanceMutex.Dispose();
                    }
                }
                catch { }
            }
        }
    }
}
