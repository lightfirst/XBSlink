/**
 * Project: XBSlink
 * File name: xbs_easytier.cs
 *
 * 内置 EasyTier 支持：P/Invoke 调用 easytier_ffi.dll
 * 参考 C++ 项目 et_dataplane.cpp / EasyTierNet.cpp
 *
 * easytier.ini 是 EasyTier 官方 TOML 格式（不是 INI），
 * 整段读入后交给 parse_config()，不自己重拼。
 *
 * 语义：EasyTier 只是一台"虚拟网卡提供者"。
 * 程序启动时拉起虚拟网络，VIP 就绪后调用方可把它作为一个可选绑定 IP。
 * 是否真正绑定 VIP 由调用方（CLI 的 -o / GUI 的 comboBox_localIP）决定。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace XBSlink
{
    static class xbs_easytier
    {
        // ---------------- 错误码（与 C++ et_dataplane.h 一致） ----------------
        public const int kOk = 0;
        public const int kCancelled = 1;
        public const int kDeadlineExceeded = 2;
        public const int kInstanceStopped = 3;
        public const int kHandleClosed = 4;
        public const int kNoOverlayRoute = 5;
        public const int kPathNotReady = 6;
        public const int kAddressFamilyUnsupported = 7;
        public const int kAddressInUse = 8;
        public const int kConnectionRefused = 9;
        public const int kNetworkChanged = 10;
        public const int kResourceLimit = 11;
        public const int kIo = 12;
        public const int kBufferTooSmall = 13;
        public const int kNotLoaded = 9001;
        public const int kSessionBusy = 9002;
        public const int kNotFound = 9003;
        public const int kLocalWaitTimeout = 9999;

        // ---------------- FFI ----------------
        [StructLayout(LayoutKind.Sequential)]
        private struct KV { public IntPtr key; public IntPtr value; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PFN_parse_config([MarshalAs(UnmanagedType.LPStr)] string toml);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PFN_run_network_instance([MarshalAs(UnmanagedType.LPStr)] string toml);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PFN_retain_network_instance(IntPtr names, UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PFN_collect_network_infos([Out] KV[] kvs, UIntPtr cap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PFN_get_error_msg(out IntPtr msg);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PFN_free_string(IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PFN_session_open([MarshalAs(UnmanagedType.LPStr)] string instName, out ulong sess);

        private static IntPtr _hModule = IntPtr.Zero;
        private static volatile bool _loaded = false;

        private static PFN_parse_config _parse_config;
        private static PFN_run_network_instance _run_network_instance;
        private static PFN_retain_network_instance _retain_network_instance;
        private static PFN_collect_network_infos _collect_network_infos;
        private static PFN_get_error_msg _get_error_msg;
        private static PFN_free_string _free_string;
        private static PFN_session_open _session_open;

        // ---------------- 当前实例状态 ----------------
        private static volatile bool _vipReady = false;
        private static string _vip = null;
        private static int _prefixLen = 16;
        private static string _instanceName = "xbslink";

        public static bool IsLoaded { get { return _loaded; } }
        public static bool VipReady { get { return _vipReady; } }
        public static string Vip { get { return _vip; } }
        public static int PrefixLen { get { return _prefixLen; } }
        public static string InstanceName { get { return _instanceName; } }

        // ============================================================
        // DLL 加载
        // ============================================================
        public static bool Load(string dllPath = null)
        {
            if (_loaded) return true;

            string[] candidates;
            if (!string.IsNullOrEmpty(dllPath))
                candidates = new[] { dllPath, "easytier_ffi.dll", "easytier.dll" };
            else
                candidates = new[] { "easytier_ffi.dll", "easytier.dll", "easytier-ffi.dll" };

            foreach (string cand in candidates)
            {
                IntPtr h = LoadLibrary(cand);
                if (h == IntPtr.Zero)
                {
                    xbs_messages.addInfoMessage("!! [ET] LoadLibrary(" + cand + ") failed: " + Marshal.GetLastWin32Error(),
                        xbs_message_sender.GENERAL, xbs_message_type.WARNING);
                    continue;
                }

                _hModule = h;
                try
                {
                    _parse_config = Bind<PFN_parse_config>(h, "parse_config");
                    _run_network_instance = Bind<PFN_run_network_instance>(h, "run_network_instance");
                    _retain_network_instance = Bind<PFN_retain_network_instance>(h, "retain_network_instance");
                    _collect_network_infos = Bind<PFN_collect_network_infos>(h, "collect_network_infos");
                    _get_error_msg = Bind<PFN_get_error_msg>(h, "get_error_msg");
                    _free_string = Bind<PFN_free_string>(h, "free_string");
                    _session_open = Bind<PFN_session_open>(h, "data_plane_session_open");
                }
                catch (Exception ex)
                {
                    xbs_messages.addInfoMessage("!! [ET] missing required exports in " + cand + ": " + ex.Message,
                        xbs_message_sender.GENERAL, xbs_message_type.WARNING);
                    FreeLibrary(h);
                    _hModule = IntPtr.Zero;
                    continue;
                }

                _loaded = true;
                xbs_messages.addInfoMessage(" @ [ET] loaded: " + cand, xbs_message_sender.GENERAL);
                return true;
            }
            return false;
        }

        public static string LastFfiError()
        {
            if (!_loaded || _get_error_msg == null) return "";
            IntPtr p;
            _get_error_msg(out p);
            if (p == IntPtr.Zero) return "";
            string s = Marshal.PtrToStringAnsi(p);
            if (_free_string != null) _free_string(p);
            return s;
        }

        // ============================================================
        // 控制面
        // ============================================================
        public static int ParseConfig(string toml)
        {
            if (!_loaded) return kNotLoaded;
            if (string.IsNullOrEmpty(toml)) return kIo;
            return _parse_config(toml);
        }

        public static int RunNetworkInstance(string toml)
        {
            if (!_loaded) return kNotLoaded;
            if (string.IsNullOrEmpty(toml)) return kIo;
            int rc = _run_network_instance(toml);
            if (rc != 0)
                xbs_messages.addInfoMessage("!! [ET] run_network_instance failed (" + rc + "): " + LastFfiError(),
                    xbs_message_sender.GENERAL, xbs_message_type.ERROR);
            return rc;
        }

        public static int WaitInstanceReady(int timeoutMs)
        {
            if (!_loaded) return kNotLoaded;
            int waited = 0;
            while (waited <= timeoutMs)
            {
                if (TryReadVipOnce()) return kOk;
                Thread.Sleep(500);
                waited += 500;
                if (waited > 0 && waited % 5000 == 0)
                    xbs_messages.addInfoMessage(" @ [ET] waiting for instance init... " + waited + " ms",
                        xbs_message_sender.GENERAL);
            }
            return kDeadlineExceeded;
        }

        public static int GetInstanceInfo(string instName, out string json)
        {
            json = null;
            if (!_loaded || _collect_network_infos == null) return kNotLoaded;
            KV[] kv = new KV[16];
            int n = _collect_network_infos(kv, (UIntPtr)16);
            bool found = false;
            for (int i = 0; i < n && i < 16; i++)
            {
                string key = kv[i].key != IntPtr.Zero ? Marshal.PtrToStringAnsi(kv[i].key) : null;
                string val = kv[i].value != IntPtr.Zero ? Marshal.PtrToStringAnsi(kv[i].value) : null;
                if (!found && key == instName && val != null) { json = val; found = true; }
                if (_free_string != null)
                {
                    if (kv[i].key != IntPtr.Zero) _free_string(kv[i].key);
                    if (kv[i].value != IntPtr.Zero) _free_string(kv[i].value);
                }
            }
            return found ? kOk : kNotFound;
        }

        public static int StopAllInstances()
        {
            if (!_loaded || _retain_network_instance == null) return kOk;
            return _retain_network_instance(IntPtr.Zero, UIntPtr.Zero);
        }

        // ============================================================
        // 数据面会话
        // ============================================================
        private static ulong _session = 0;
        private static readonly object _sessionLock = new object();

        public static int OpenSession(string instName)
        {
            if (!_loaded) return kNotLoaded;
            if (string.IsNullOrEmpty(instName)) return kIo;
            lock (_sessionLock)
            {
                if (_session != 0) return kSessionBusy;
                int lastRc = 0;
                for (int i = 0; i < 60; i++)
                {
                    ulong s;
                    int rc = _session_open(instName, out s);
                    if (rc == 0 && s != 0)
                    {
                        _session = s;
                        xbs_messages.addInfoMessage(" @ [ET] data-plane session opened: " + instName,
                            xbs_message_sender.GENERAL);
                        return kOk;
                    }
                    lastRc = rc;
                    Thread.Sleep(500);
                }
                return lastRc != 0 ? Math.Abs(lastRc) : kInstanceStopped;
            }
        }

        public static void CloseSession() { lock (_sessionLock) { _session = 0; } }

        // ============================================================
        // 虚拟 IP 解析
        // ============================================================
        private static bool TryReadVipOnce()
        {
            if (_vipReady) return true;
            string json;
            if (GetInstanceInfo(_instanceName, out json) != kOk || string.IsNullOrEmpty(json))
                return false;
            return TryParseVipFromJson(json);
        }

        private static bool TryParseVipFromJson(string json)
        {
            int p = json.IndexOf("\"virtual_ipv4\"", StringComparison.Ordinal);
            if (p < 0) return false;
            int q = json.IndexOf("\"addr\":", p, StringComparison.Ordinal);
            if (q < 0) return false;
            if (ReadUInt32At(json, q + 7, out uint addr) == null) return false;
            _vip = ((addr >> 24) & 255) + "." + ((addr >> 16) & 255) + "." + ((addr >> 8) & 255) + "." + (addr & 255);

            int r = json.IndexOf("\"network_length\":", p, StringComparison.Ordinal);
            if (r >= 0)
            {
                // 从冒号后第一个数字开始读，跳过空格
                int colon = json.IndexOf(':', r);
                if (colon >= 0 && ReadUInt32At(json, colon + 1, out uint len) != null)
                    _prefixLen = (int)len;
            }

            _vipReady = true;
            xbs_messages.addInfoMessage(" @ [ET] VIP resolved: " + _vip + "/" + _prefixLen,
                xbs_message_sender.GENERAL);
            return true;
        }

        private static string ReadUInt32At(string s, int start, out uint value)
        {
            value = 0;
            int i = start;
            while (i < s.Length && s[i] == ' ') i++;
            int begin = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == begin) return null;
            value = uint.Parse(s.Substring(begin, i - begin));
            return s.Substring(begin, i - begin);
        }

        // ============================================================
        // TOML 只读抽取
        // ============================================================
        /// <summary>
        /// 从 easytier.ini（TOML）文本里抽取 XBSlink 需要用的字段：
        ///  instance_name / ipv4 / dhcp / network_name。只读，不修改文本。
        /// </summary>
        public static void ProbeToml(string toml,
                                     out string instanceName,
                                     out string ipv4, out int prefixLen,
                                     out bool dhcp, out string networkName)
        {
            instanceName = null;
            ipv4 = null;
            prefixLen = 24;
            dhcp = false;
            networkName = null;

            if (string.IsNullOrEmpty(toml)) return;

            foreach (string raw in toml.Split(new[] { '\n' }, StringSplitOptions.None))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                int hash = val.IndexOf('#');
                if (hash >= 0) val = val.Substring(0, hash).Trim();
                val = Unquote(val);

                switch (key)
                {
                    case "instance_name": instanceName = val; break;
                    case "network_name": networkName = val; break;
                    case "ipv4":
                        ipv4 = val;
                        int slash = ipv4.IndexOf('/');
                        if (slash >= 0)
                        {
                            int.TryParse(ipv4.Substring(slash + 1), out prefixLen);
                            ipv4 = ipv4.Substring(0, slash);
                        }
                        break;
                    case "dhcp":
                        dhcp = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
        }

        private static string Unquote(string v)
        {
            if (v.Length >= 2 &&
                ((v[0] == '"' && v[v.Length - 1] == '"') ||
                 (v[0] == '\'' && v[v.Length - 1] == '\'')))
                return v.Substring(1, v.Length - 2);
            return v;
        }

        /// <summary>
        /// 等价于 easytier-core 的 --no-tun：
        /// 把 [flags] no_tun 强制覆盖为 true（只改内存中的 toml 文本，不改磁盘文件）。
        /// </summary>
        private static string ForceNoTun(string toml)
        {
            if (string.IsNullOrEmpty(toml)) return toml;

            var rx = new Regex(
                @"(?im)^[ \t]*(no_tun|noTun)[ \t]*=[ \t]*(true|false)[ \t]*(#.*)?$");
            if (rx.IsMatch(toml))
                return rx.Replace(toml, "no_tun = true", 1);

            var mFlags = Regex.Match(toml, @"(?im)^[ \t]*\[flags\][ \t]*\r?$");
            if (mFlags.Success)
            {
                int insertAt = mFlags.Index + mFlags.Length;
                if (insertAt < toml.Length && toml[insertAt] == '\r') insertAt++;
                if (insertAt < toml.Length && toml[insertAt] == '\n') insertAt++;
                return toml.Insert(insertAt, "no_tun = true\n");
            }

            var sb = new StringBuilder(toml);
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
            sb.Append("[flags]\nno_tun = true\n");
            return sb.ToString();
        }

        /// <summary>读取 TOML 里 [flags] no_tun，缺省 false。</summary>
        private static void ProbeFlagNoTun(string toml, out bool noTun)
        {
            noTun = false;
            bool inFlags = false;
            foreach (string raw in toml.Split(new[] { '\n' }, StringSplitOptions.None))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    inFlags = line.StartsWith("[flags]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inFlags) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                int hash = val.IndexOf('#');
                if (hash >= 0) val = val.Substring(0, hash).Trim();
                if (key.Equals("no_tun", StringComparison.OrdinalIgnoreCase))
                    noTun = val.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// 强制覆盖为 DHCP 模式，并设置 hostname：
        ///  - 删除顶层 ipv4 行
        ///  - 把 dhcp 强制设为 true（无此行则插入）
        ///  - 把 hostname 强制设为 nickname（无此行则插入）
        /// 只改内存中的 toml 文本，不改磁盘文件。
        /// </summary>
        private static string ForceDhcpAndHostname(string toml, string nickname)
        {
            if (string.IsNullOrEmpty(toml)) return toml;

            // 1) 删除顶层 ipv4 行（含被注释掉的也保留注释，只删生效的）
            toml = Regex.Replace(toml,
                @"(?im)^[ \t]*ipv4[ \t]*=[ \t]*[^\r\n]*\r?\n?",
                "");

            // 2) 强制 dhcp = true
            var rxDhcp = new Regex(@"(?im)^[ \t]*dhcp[ \t]*=[ \t]*(true|false)[ \t]*(#.*)?$");
            if (rxDhcp.IsMatch(toml))
                toml = rxDhcp.Replace(toml, "dhcp = true", 1);
            else
                toml = "dhcp = true\n" + toml;

            // 3) 强制 hostname = nickname
            if (!string.IsNullOrEmpty(nickname))
            {
                // 转义双引号和反斜杠
                string escaped = nickname.Replace("\\", "\\\\").Replace("\"", "\\\"");
                var rxHost = new Regex(@"(?im)^[ \t]*hostname[ \t]*=[ \t]*[^\r\n]*$");
                if (rxHost.IsMatch(toml))
                    toml = rxHost.Replace(toml, "hostname = \"" + escaped + "\"", 1);
                else
                    toml = "hostname = \"" + escaped + "\"\n" + toml;
            }

            return toml;
        }

        /// <summary>
        /// 读 easytier.ini（TOML 格式）→ ForceNoTun → parse_config → run_network_instance → 等 VIP。
        /// 成功后 xbs_easytier.Vip 就是 EasyTier 虚拟 IP。
        /// 注意：本方法只把虚拟网络跑起来；是否真的绑定 VIP 由调用方决定。
        ///
        /// 流程：
        ///   1. parse_config(toml)
        ///   2. run_network_instance(toml)
        ///   3. 诊断：GetInstanceInfo(instanceName) 打印实例名匹配情况
        ///   4. no-tun 模式：不创建 TUN，不会有 virtual_ipv4，直接 OpenSession
        ///   5. TUN 模式：WaitInstanceReady 等 virtual_ipv4，再 OpenSession（如有必要）
        /// </summary>
        public static bool StartupFromIni(string iniPath, string nickname, out string error)
        {
            error = null;

            if (!File.Exists(iniPath))
            {
                error = "easytier.ini not found: " + iniPath;
                return false;
            }

            if (!Load())
            {
                error = "easytier_ffi.dll not found (must be in same folder as XBSlink)";
                return false;
            }

            string toml = File.ReadAllText(iniPath);

            // 等价 CLI --no-tun：在 parse_config 前把 no_tun 强制改为 true
            //toml = ForceNoTun(toml);
            //xbs_messages.addInfoMessage(" @ [ET] force no_tun = true (equivalent to --no-tun)",
            //    xbs_message_sender.GENERAL);

            // ★ 强制 DHCP + hostname
            toml = ForceDhcpAndHostname(toml, nickname);
            xbs_messages.addInfoMessage(" @ [ET] force dhcp = true, hostname = \"" + (nickname ?? "") + "\"",
                xbs_message_sender.GENERAL);

            ProbeToml(toml, out string instanceName, out string ipv4, out int prefixLen,
                      out bool dhcp, out string networkName);

            if (string.IsNullOrEmpty(instanceName))
                instanceName = "xbslink";
            _instanceName = instanceName;

            if (string.IsNullOrEmpty(ipv4) && !dhcp)
            {
                xbs_messages.addInfoMessage("!! [ET] easytier.ini 既没有静态 ipv4 也没开 dhcp=true，EasyTier 将无法分配虚拟 IP",
                    xbs_message_sender.GENERAL, xbs_message_type.WARNING);
            }
            if (string.IsNullOrEmpty(instanceName))
            {
                instanceName = "xbslink";
                // 在 toml 开头插入 instance_name，确保 EasyTier 用这个名字
                toml = "instance_name = \"" + instanceName + "\"\n" + toml;
            }

            // 1) 交给 EasyTier 官方 FFI
            if (ParseConfig(toml) != 0)
            {
                error = "ET parse_config failed: " + LastFfiError();
                return false;
            }
            if (RunNetworkInstance(toml) != 0)
            {
                error = "ET run_network_instance failed: " + LastFfiError();
                return false;
            }

            bool noTun;
            ProbeFlagNoTun(toml, out noTun);

            // 2) no-tun 分支：不创建 TUN，不会有 virtual_ipv4；直接 OpenSession
            if (noTun)
            {
                if (string.IsNullOrEmpty(ipv4))
                {
                    error = "no-tun mode requires static ipv4 in easytier.ini";
                    return false;
                }
                _vip = ipv4;
                _prefixLen = prefixLen;
                _vipReady = true;

                int orc = OpenSession(instanceName);
                if (orc != 0)
                {
                    error = "ET OpenSession failed: " + orc + " (" + ErrText(orc) + ")";
                    return false;
                }

                xbs_messages.addInfoMessage(
                    " @ [ET] ready (no-tun). virtual IP = " + _vip + "/" + _prefixLen,
                    xbs_message_sender.GENERAL);
                return true;
            }

            // 3) TUN 模式：等 virtual_ipv4 出现
            int rc = WaitInstanceReady(60000);
            if (rc != kOk)
            {
                if (!string.IsNullOrEmpty(ipv4))
                {
                    // 静态 IP：允许超时兜底
                    _vip = ipv4;
                    _prefixLen = prefixLen;
                    _vipReady = true;
                    xbs_messages.addInfoMessage(
                        " !! [ET] instance ready timed out, but static ipv4 " + _vip + "/" + _prefixLen + " will be used",
                        xbs_message_sender.GENERAL, xbs_message_type.WARNING);
                }
                else
                {
                    // DHCP：必须等到 IP 就绪
                    error = "ET DHCP did not assign an IP within 60s (rc=" + rc + " " + ErrText(rc) + ")";
                    return false;
                }
            }

            if (!_vipReady && !string.IsNullOrEmpty(ipv4))
            {
                _vip = ipv4;
                _prefixLen = prefixLen;
                _vipReady = true;
            }
            if (!_vipReady)
            {
                error = "ET could not resolve virtual IP";
                return false;
            }

            xbs_messages.addInfoMessage(
                " @ [ET] ready (TUN). virtual IP = " + _vip + "/" + _prefixLen,
                xbs_message_sender.GENERAL);
            return true;
        }

        public static string ErrText(int status)
        {
            switch (Math.Abs(status))
            {
                case kOk: return "Ok";
                case kCancelled: return "Cancelled";
                case kDeadlineExceeded: return "DeadlineExceeded";
                case kInstanceStopped: return "InstanceStopped";
                case kHandleClosed: return "HandleClosed";
                case kNoOverlayRoute: return "NoOverlayRoute";
                case kPathNotReady: return "PathNotReady";
                case kAddressFamilyUnsupported: return "AddressFamilyUnsupported";
                case kAddressInUse: return "AddressInUse";
                case kConnectionRefused: return "ConnectionRefused";
                case kNetworkChanged: return "NetworkChanged";
                case kResourceLimit: return "ResourceLimit";
                case kIo: return "Io";
                case kBufferTooSmall: return "BufferTooSmall";
                case kNotLoaded: return "NotLoaded";
                case kSessionBusy: return "SessionBusy";
                case kNotFound: return "NotFound";
                case kLocalWaitTimeout: return "LocalWaitTimeout";
                default: return "Unknown";
            }
        }

        public static void Shutdown()
        {
            CloseSession();
            if (_loaded)
            {
                try { StopAllInstances(); } catch { }
                if (_hModule != IntPtr.Zero) { FreeLibrary(_hModule); _hModule = IntPtr.Zero; }
                _loaded = false;
            }
        }

        // ============================================================
        // Win32
        // ============================================================
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        private static T Bind<T>(IntPtr h, string name) where T : class
        {
            IntPtr p = GetProcAddress(h, name);
            if (p == IntPtr.Zero) throw new Exception("export not found: " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }
    }
}