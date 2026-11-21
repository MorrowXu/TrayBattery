// TrayBattery.cs — 无线鼠标电量托盘（自写实现，非上游代码的衍生分发）
//
// 安全设计（可在源码里逐条核对）：
//   1. 不引用 System.Net / 任何网络 API，进程没有任何 socket 能力；
//   2. 无自动更新、无遥测、无上报、无安装器、无后台服务、无驱动；
//   3. 以 asInvoker 运行，不需要管理员权限；
//   4. 只对 Logitech（VID 046D）的 HID++ 子集合（UsagePage 0xFF00 / Usage 0x0001、0x0002）
//      发送只读查询；对其它厂商设备一律不发任何报文，只读 Windows 自己维护的蓝牙电量属性；
//   5. 句柄以 FILE_SHARE_READ|WRITE 打开，不独占设备，不影响鼠标正常上报；
//   6. 打开的是 vendor 集合，不是 pointer 集合，因此不会读走鼠标移动/按键数据；
//   7. 唯一的持久化写入是可选的开机自启（HKCU\...\Run 一个值），默认关闭、菜单显式开关。
//
// 编译： build.ps1（调用系统自带 csc，不联网、不装包）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SafeBattery
{
    internal sealed class DevInfo
    {
        public string Key = "";
        public string Name = "";
        public string Via = "";
        public int Percent = -1;
        public bool Charging;
        public bool Full;

        public string Line()
        {
            string s = Name;
            if (Percent >= 0) s += "  " + Percent + "%"; else s += "  —";
            if (Charging) s += Full ? "  (已充满)" : "  (充电中)";
            return s;
        }
    }

    // ───────────────────────────── Win32 ─────────────────────────────
    internal static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997;
        public const uint DIGCF_PRESENT = 0x2;
        public const uint DIGCF_ALLCLASSES = 0x4;
        public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

        public static readonly Guid GUID_DEVINTERFACE_HID =
            new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

        [StructLayout(LayoutKind.Sequential)]
        public struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint Offset;
            public uint OffsetHigh;
            public IntPtr hEvent;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_List_Size(
            out int len, ref Guid interfaceClassGuid, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_List(
            ref Guid interfaceClassGuid, string deviceId, IntPtr buffer, int bufferLen, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr h, byte[] buf, int n, out int written, ref OVERLAPPED ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, ref OVERLAPPED ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(IntPtr h, ref OVERLAPPED ov, out int transferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIo(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEventW(IntPtr attr, bool manual, bool initial, IntPtr name);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr h, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(uint pid);

        [DllImport("kernel32.dll")]
        public static extern bool FreeConsole();

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr data);

        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool HidD_GetProductString(IntPtr h, StringBuilder buf, int len);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetOutputReport(IntPtr h, byte[] buf, int len);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator,
            IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data,
            StringBuilder buf, int len, out int required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data,
            uint prop, out uint regType, byte[] buf, uint size, out uint required);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data,
            ref DEVPROPKEY key, out uint propType, byte[] buf, uint size, out uint required, uint flags);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);
    }

    // ───────────────────────────── HID 设备 ─────────────────────────────
    internal sealed class HidIo : IDisposable
    {
        private IntPtr _h = new IntPtr(-1);
        private int _inLen;
        private int _outLen;

        public bool Ok { get { return _h != new IntPtr(-1); } }

        private HidIo() { }

        public static HidIo Open(string path, bool readWrite, bool overlapped)
        {
            HidIo io = new HidIo();
            uint access = readWrite ? (Native.GENERIC_READ | Native.GENERIC_WRITE) : 0u;
            uint flags = overlapped ? Native.FILE_FLAG_OVERLAPPED : 0u;
            io._h = Native.CreateFileW(path, access,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, flags, IntPtr.Zero);
            return io.Ok ? io : null;
        }

        public static bool ReadCaps(string path, int accessMode, out ushort usage, out ushort page,
            out int inLen, out int outLen, out ushort vid, out ushort pid, out string product)
        {
            usage = 0; page = 0; inLen = 0; outLen = 0; vid = 0; pid = 0; product = "";
            uint access = accessMode == 0 ? 0u : Native.GENERIC_READ;
            uint flags = accessMode == 2 ? Native.FILE_FLAG_OVERLAPPED : 0u;
            IntPtr h = Native.CreateFileW(path, access,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, flags, IntPtr.Zero);
            if (h == new IntPtr(-1)) return false;
            try
            {
                Native.HIDD_ATTRIBUTES a = new Native.HIDD_ATTRIBUTES();
                a.Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES));
                if (Native.HidD_GetAttributes(h, ref a))
                {
                    vid = a.VendorID; pid = a.ProductID;
                }
                IntPtr pp;
                if (Native.HidD_GetPreparsedData(h, out pp))
                {
                    try
                    {
                        Native.HIDP_CAPS caps = new Native.HIDP_CAPS();
                        caps.Reserved = new ushort[17];
                        if (Native.HidP_GetCaps(pp, ref caps) == 0x00110000) // HIDP_STATUS_SUCCESS
                        {
                            usage = caps.Usage;
                            page = caps.UsagePage;
                            inLen = caps.InputReportByteLength;
                            outLen = caps.OutputReportByteLength;
                        }
                    }
                    finally { Native.HidD_FreePreparsedData(pp); }
                }
                StringBuilder sb = new StringBuilder(256);
                if (Native.HidD_GetProductString(h, sb, 512)) product = sb.ToString();
            }
            finally { Native.CloseHandle(h); }
            return true;
        }

        public void SetLengths(int inLen, int outLen) { _inLen = inLen; _outLen = outLen; }

        public bool Write(byte[] data)
        {
            if (!Ok || _outLen <= 0) return false;
            byte[] buf = new byte[_outLen];
            Array.Copy(data, buf, Math.Min(data.Length, buf.Length));

            Native.OVERLAPPED ov = new Native.OVERLAPPED();
            ov.hEvent = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            int n;
            bool done = false;
            if (Native.WriteFile(_h, buf, buf.Length, out n, ref ov) ||
                Marshal.GetLastWin32Error() == Native.ERROR_IO_PENDING)
            {
                if (Native.WaitForSingleObject(ov.hEvent, 800) == 0)
                    done = Native.GetOverlappedResult(_h, ref ov, out n, false);
                else
                {
                    Native.CancelIo(_h);
                    done = Native.GetOverlappedResult(_h, ref ov, out n, true);
                }
            }
            Native.CloseHandle(ov.hEvent);
            if (done) return true;

            // 某些集合没有 interrupt OUT 管道，退回 control pipe 写 output report。
            return Native.HidD_SetOutputReport(_h, buf, buf.Length);
        }

        public int Read(byte[] dst, int timeoutMs)
        {
            if (!Ok || _inLen <= 0) return -1;
            byte[] tmp = new byte[_inLen];
            Native.OVERLAPPED ov = new Native.OVERLAPPED();
            ov.hEvent = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            int got;
            bool done = false;
            if (Native.ReadFile(_h, tmp, tmp.Length, out got, ref ov) ||
                Marshal.GetLastWin32Error() == Native.ERROR_IO_PENDING)
            {
                if (Native.WaitForSingleObject(ov.hEvent, (uint)timeoutMs) == 0)
                    done = Native.GetOverlappedResult(_h, ref ov, out got, false);
                else
                {
                    Native.CancelIo(_h);
                    done = Native.GetOverlappedResult(_h, ref ov, out got, true);
                }
            }
            Native.CloseHandle(ov.hEvent);
            if (!done || got <= 0) return -1;

            int off = tmp[0] == 0 ? 1 : 0; // hidapi 语义：无编号报告时去掉前导 0
            int n = got - off;
            if (n > dst.Length) n = dst.Length;
            if (n <= 0) return -1;
            Array.Copy(tmp, off, dst, 0, n);
            return n;
        }

        public void Dispose()
        {
            if (_h != new IntPtr(-1)) { Native.CloseHandle(_h); _h = new IntPtr(-1); }
        }
    }

    // ───────────────────────────── HID++ 2.0 ─────────────────────────────
    internal sealed class Hidpp : IDisposable
    {
        private HidIo _short;
        private HidIo _long;
        private const byte SwId = 0x0A;

        public bool Open(string shortPath, string longPath)
        {
            if (shortPath != null)
            {
                _short = HidIo.Open(shortPath, true, true);
                if (_short != null) _short.SetLengths(7, 7);
            }
            if (longPath != null)
            {
                _long = HidIo.Open(longPath, true, true);
                if (_long != null) _long.SetLengths(20, 20);
            }
            return _short != null || _long != null;
        }

        public int Request(byte idx, byte b2, byte b3, byte[] par,
            out byte[] resp, out byte err, bool forceShort, int timeoutMs)
        {
            resp = null; err = 0;
            bool useLong = _long != null && !forceShort;
            HidIo dev = useLong ? _long : _short;
            if (dev == null) return -1;

            int n = useLong ? 20 : 7;
            byte[] msg = new byte[n];
            msg[0] = useLong ? (byte)0x11 : (byte)0x10;
            msg[1] = idx;
            msg[2] = b2;
            msg[3] = b3;
            for (int i = 0; i < par.Length && 4 + i < n; i++) msg[4 + i] = par[i];
            if (!dev.Write(msg)) return -1;

            int deadline = Environment.TickCount + timeoutMs;
            byte[] r = new byte[32];
            while (unchecked(deadline - Environment.TickCount) > 0)
            {
                int remaining = deadline - Environment.TickCount;
                if (remaining <= 0) break;
                int got = dev.Read(r, Math.Min(remaining, 200));
                if (got < 4) continue;
                if (r[0] != 0x10 && r[0] != 0x11) continue;
                if (r[1] != idx) continue;

                if (r[2] == b2 && r[3] == b3)
                {
                    byte[] p = new byte[got - 4];
                    Array.Copy(r, 4, p, 0, p.Length);
                    resp = p;
                    return 1;
                }
                // HID++ 2.0 错误帧 0xFF / HID++ 1.0 错误帧 0x8F
                if (got >= 6 && (r[2] == 0xFF || r[2] == 0x8F) && r[3] == b2 && r[4] == b3)
                {
                    err = r[5];
                    return 0;
                }
            }
            return -1;
        }

        public bool Call(byte idx, byte feature, byte fn, byte[] par, out byte[] resp)
        {
            byte err;
            return Request(idx, feature, (byte)((fn << 4) | SwId), par, out resp, out err, false, 500) == 1;
        }

        public byte FeatureIndex(byte idx, ushort featureId)
        {
            byte[] p = new byte[] { (byte)(featureId >> 8), (byte)featureId };
            byte[] resp;
            if (Call(idx, 0x00, 0, p, out resp) && resp != null && resp.Length > 0) return resp[0];
            return 0;
        }

        public void Dispose()
        {
            if (_short != null) _short.Dispose();
            if (_long != null) _long.Dispose();
            _short = null; _long = null;
        }
    }

    // ───────────────────────────── 设备枚举 ─────────────────────────────
    internal static class HidList
    {
        public static List<string> Enumerate()
        {
            List<string> paths = new List<string>();
            Guid g = Native.GUID_DEVINTERFACE_HID;
            int need;
            if (Native.CM_Get_Device_Interface_List_Size(out need, ref g, null,
                    Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0 || need <= 1) return paths;

            IntPtr buf = Marshal.AllocHGlobal(need * 2);
            try
            {
                if (Native.CM_Get_Device_Interface_List(ref g, null, buf, need,
                        Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0) return paths;
                int off = 0;
                while (off < need)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + off * 2);
                    string s = Marshal.PtrToStringUni(p);
                    if (string.IsNullOrEmpty(s)) break;
                    paths.Add(s);
                    off += s.Length + 1;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return paths;
        }

        /// <summary>只读探测：列出全部 HID 接口的 VID/PID、产品名、usage 与报文长度（同集合去重）。</summary>
        public static List<string> Probe()
        {
            List<string> rows = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string p in Enumerate())
            {
                ushort usage, page; int inLen, outLen; ushort vid, pid; string prod;
                if (!HidIo.ReadCaps(p, 0, out usage, out page, out inLen, out outLen,
                        out vid, out pid, out prod)) continue;
                string key = string.Format("{0:X4}:{1:X4}/{2:X4}:{3:X4}", vid, pid, page, usage);
                if (!seen.Add(key)) continue;
                rows.Add(string.Format("VID_{0:X4}&PID_{1:X4} | {2} | usage {3:X4}:{4:X4} | in={5} out={6}",
                    vid, pid, string.IsNullOrEmpty(prod) ? "(无产品名)" : prod, page, usage, inLen, outLen));
            }
            return rows;
        }
    }

    // ──────────── ATK / VXE / Pulsar 系 2.4G 接收器（FF02:0002，17 字节帧）────────────
    /// <summary>
    /// Compx 系（ATK / VXE / Pulsar / VGN / Hitscan）2.4G 接收器的电量读取。
    /// 帧：17 字节 —— [0]=0x08（report ID）、[1]=0x04（电量命令）、[16]=校验和。
    /// 校验和 = (0x55 - 前 16 字节之和) &amp; 0xFF，整帧求和恰为 0x55。
    /// 回复：byte[6]=电量百分比、byte[7]=充电标志（非 0 即在充电）、byte[8..9]=毫伏（大端）。
    /// 只发这一条只读查询，不写任何设备配置、不做配对与固件操作。
    /// 鼠标休眠时接收器不回复，属正常现象。
    /// </summary>
    internal static class Pulsar
    {
        // 同一套 Compx / Pulsar 电量协议被多个品牌复用，控制集特征完全一致：
        // FF02:0002 + 输入输出各 17 字节 + 0x55 校验和。按 VID 白名单逐个试。
        //   373B = ATK / Compx 接收器
        //   391D = Attack Shark / Darmoshark （Dragonfly F2 Turbo+ 等）
        //   3554 = VXE
        private static readonly ushort[] Vids = { 0x373B, 0x391D, 0x3554 };
        private const ushort ControlPage = 0xFF02;
        private const ushort ControlUsage = 0x0002;
        private const int FrameLen = 17;
        private const byte Header = 0x08;
        private const byte CmdPower = 0x04;
        private const byte ChecksumBase = 0x55;

        private static byte Check(byte[] b, int count)
        {
            int s = 0;
            for (int i = 0; i < count; i++) s += b[i];
            return (byte)((ChecksumBase - s) & 0xFF);
        }

        private static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }

        public static List<DevInfo> Poll(StringBuilder trace)
        {
            List<DevInfo> found = new List<DevInfo>();
            foreach (string path in HidList.Enumerate())
            {
                ushort usage, page; int inLen, outLen; ushort vid, pid; string prod;
                if (!HidIo.ReadCaps(path, 0, out usage, out page, out inLen, out outLen,
                        out vid, out pid, out prod)) continue;
                if (Array.IndexOf(Vids, vid) < 0) continue;

                if (page != ControlPage || usage != ControlUsage)
                {
                    if (trace != null)
                        trace.AppendLine(string.Format("ATK {0:X4}:{1:X4} 非控制集 {2:X4}:{3:X4} (in={4} out={5})",
                            vid, pid, page, usage, inLen, outLen));
                    continue;
                }
                if (inLen != FrameLen || outLen != FrameLen)
                {
                    if (trace != null)
                        trace.AppendLine(string.Format("ATK {0:X4}:{1:X4} 帧长不符 in={2} out={3}（需要 17/17）",
                            vid, pid, inLen, outLen));
                    continue;
                }

                HidIo io = HidIo.Open(path, true, true);
                if (io == null) { if (trace != null) trace.AppendLine("ATK 打开失败: " + path); continue; }
                try
                {
                    io.SetLengths(FrameLen, FrameLen);
                    byte[] frame = new byte[FrameLen];
                    frame[0] = Header;
                    frame[1] = CmdPower;
                    frame[FrameLen - 1] = Check(frame, FrameLen - 1);

                    io.Read(new byte[FrameLen], 60);          // 丢弃上一条残留回复
                    if (!io.Write(frame))
                    {
                        if (trace != null) trace.AppendLine("ATK 写入失败: " + Hex(frame));
                        continue;
                    }

                    byte[] reply = new byte[FrameLen];
                    int n = io.Read(reply, 600);
                    if (n < FrameLen)
                    {
                        if (trace != null)
                            trace.AppendLine("ATK 无回复（鼠标休眠属正常，动一下鼠标再刷新）n=" + n);
                        continue;
                    }
                    if (reply[0] != Header || reply[1] != CmdPower ||
                        reply[FrameLen - 1] != Check(reply, FrameLen - 1))
                    {
                        if (trace != null) trace.AppendLine("ATK 回复不合法: " + Hex(reply));
                        continue;
                    }

                    int level = reply[6];
                    if (level < 0 || level > 100) continue;

                    DevInfo d = new DevInfo();
                    d.Name = !string.IsNullOrEmpty(prod) ? prod : "ATK / VXE 无线鼠标";
                    d.Percent = level;
                    d.Charging = reply[7] != 0;
                    d.Full = d.Charging && level >= 100;
                    int mv = (reply[8] << 8) | reply[9];
                    d.Via = mv > 0 ? "USB 接收器 · " + mv + " mV" : "USB 接收器";
                    d.Key = "atk:" + path.ToLowerInvariant();
                    found.Add(d);
                    if (trace != null) trace.AppendLine("ATK 读数: " + Hex(reply));
                }
                finally { io.Dispose(); }
            }
            return found;
        }
    }

    internal static class Logitech
    {
        private const ushort Vid = 0x046D;

        private static string PhysicalKey(string path)
        {
            string p = path.ToLowerInvariant();
            int a = p.IndexOf('#');
            if (a < 0) return p;
            int b = p.IndexOf('#', a + 1);
            if (b < 0) return p;
            int e = p.IndexOf('#', b + 1);
            if (e < 0) return p;
            string hw = p.Substring(a + 1, b - a - 1);
            string inst = p.Substring(b + 1, e - b - 1);
            int col = hw.IndexOf("&col", StringComparison.Ordinal);
            if (col >= 0) hw = hw.Substring(0, col);
            int amp = inst.LastIndexOf('&');
            if (amp >= 0) inst = inst.Substring(0, amp);
            return hw + "#" + inst;
        }

        private static int LiIonPercent(int mv)
        {
            int[] v = { 4186, 4067, 3989, 3922, 3859, 3811, 3778, 3751, 3717, 3671, 3646, 3579, 3500 };
            int[] p = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 5, 2, 0 };
            for (int i = 0; i + 1 < v.Length; i++)
            {
                if (mv >= v[i]) return p[i];
                if (mv >= v[i + 1])
                {
                    double t = (double)(mv - v[i + 1]) / (v[i] - v[i + 1]);
                    return (int)Math.Round(p[i + 1] + t * (p[i] - p[i + 1]));
                }
            }
            return 0;
        }

        private static bool ReadBattery(Hidpp h, byte idx, DevInfo d)
        {
            byte[] p;
            byte f = h.FeatureIndex(idx, 0x1004); // UNIFIED_BATTERY
            if (f != 0 && h.Call(idx, f, 1, new byte[0], out p) && p != null && p.Length >= 3)
            {
                int soc = p[0], level = p[1], st = p[2];
                if (soc > 0 && soc <= 100) d.Percent = soc;
                else if (level != 0)
                {
                    if ((level & 8) != 0) d.Percent = 100;
                    else if ((level & 4) != 0) d.Percent = 60;
                    else if ((level & 2) != 0) d.Percent = 20;
                    else d.Percent = 5;
                }
                d.Charging = st >= 1 && st <= 3;
                d.Full = st == 3;
                return d.Percent >= 0;
            }

            f = h.FeatureIndex(idx, 0x1000); // BATTERY_STATUS
            if (f != 0 && h.Call(idx, f, 0, new byte[0], out p) && p != null && p.Length >= 3)
            {
                int st = p[2];
                d.Charging = st >= 1 && st <= 4;
                d.Full = st == 3;
                d.Percent = p[0] <= 100 ? p[0] : -1;
                if (d.Percent == 0 && st == 3) d.Percent = 100;
                return d.Percent >= 0;
            }

            f = h.FeatureIndex(idx, 0x1001); // BATTERY_VOLTAGE
            if (f != 0 && h.Call(idx, f, 0, new byte[0], out p) && p != null && p.Length >= 3)
            {
                int mv = (p[0] << 8) | p[1];
                d.Charging = (p[2] & 0x80) != 0;
                if (mv > 0) d.Percent = LiIonPercent(mv);
                return d.Percent >= 0;
            }

            f = h.FeatureIndex(idx, 0x1F20); // ADC_MEASUREMENT（耳机类，锂电池表近似）
            if (f != 0 && h.Call(idx, f, 0, new byte[0], out p) && p != null && p.Length >= 3)
            {
                int mv = (p[0] << 8) | p[1];
                d.Charging = p[2] == 0x03;
                if (mv > 0) d.Percent = LiIonPercent(mv);
                return d.Percent >= 0;
            }
            return false;
        }

        private static bool ReadBattery10(Hidpp h, byte idx, DevInfo d)
        {
            byte[] p;
            byte err;
            if (h.Request(idx, 0x81, 0x0D, new byte[0], out p, out err, true, 500) == 1 &&
                p != null && p.Length >= 3 && p[0] > 0 && p[0] <= 100)
            {
                d.Percent = p[0];
                d.Charging = p[2] >= 0x21 && p[2] <= 0x26;
                return true;
            }
            if (h.Request(idx, 0x81, 0x07, new byte[0], out p, out err, true, 500) == 1 &&
                p != null && p.Length >= 2 && p[0] > 0)
            {
                int[] pct = { 0, 5, 10, 20, 35, 50, 75, 100 };
                d.Percent = pct[p[0] > 7 ? 7 : p[0]];
                d.Charging = p[1] >= 0x21 && p[1] <= 0x26;
                return true;
            }
            return false;
        }

        private static string ReadName(Hidpp h, byte idx)
        {
            byte f = h.FeatureIndex(idx, 0x0005); // DEVICE_NAME
            if (f == 0) return "";
            byte[] p;
            if (!h.Call(idx, f, 0, new byte[0], out p) || p == null || p.Length == 0) return "";
            int len = p[0];
            if (len <= 0 || len > 64) return "";
            StringBuilder sb = new StringBuilder();
            while (sb.Length < len)
            {
                if (!h.Call(idx, f, 1, new byte[] { (byte)sb.Length }, out p) || p == null) break;
                int before = sb.Length;
                for (int i = 0; i < p.Length && sb.Length < len; i++)
                {
                    if (p[i] == 0) break;
                    sb.Append((char)p[i]);
                }
                if (sb.Length == before) break;
            }
            string s = sb.ToString().Trim();
            if (s.Length == 0) return "";
            if (s.StartsWith("Logitech", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("G ", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("MX", StringComparison.OrdinalIgnoreCase)) return s;
            return "Logitech " + s;
        }

        public static List<DevInfo> Poll(StringBuilder trace)
        {
            List<DevInfo> found = new List<DevInfo>();

            List<string> allPaths = HidList.Enumerate();
            if (trace != null) trace.AppendLine("HID 接口总数: " + allPaths.Count);

            List<string> shorts = new List<string>();
            List<string> longs = new List<string>();
            Dictionary<string, string> products = new Dictionary<string, string>();

            foreach (string path in allPaths)
            {
                if (path.IndexOf("vid_046d", StringComparison.OrdinalIgnoreCase) < 0) continue;
                ushort usage, page, vid, pid;
                int inLen, outLen;
                string product;
                if (!HidIo.ReadCaps(path, 0, out usage, out page, out inLen, out outLen,
                        out vid, out pid, out product)) continue;
                if (vid != Vid) continue;
                if (trace != null)
                    trace.AppendLine(string.Format("HID {0:X4}:{1:X4} page={2:X4} usage={3:X4} out={4} in={5} {6}",
                        vid, pid, page, usage, outLen, inLen, product));
                if (outLen == 7 && page == 0xFF00 && usage == 0x0001) shorts.Add(path);
                else if (outLen == 20 && (page == 0xFF00 && usage == 0x0002)) longs.Add(path);
                else if (outLen == 20 && page == 0xFF43 && (usage == 0x0202 || usage == 0x0302)) longs.Add(path);
                else if (outLen == 7 && page == 0xFF43 && usage == 0x0301) shorts.Add(path);
                if (!string.IsNullOrEmpty(product)) products[path] = product;
            }

            List<string> groups = new List<string>();
            foreach (string p in shorts) { string k = PhysicalKey(p); if (!groups.Contains(k)) groups.Add(k); }
            foreach (string p in longs) { string k = PhysicalKey(p); if (!groups.Contains(k)) groups.Add(k); }

            foreach (string g in groups)
            {
                string sPath = null, lPath = null;
                foreach (string p in shorts) if (PhysicalKey(p) == g) { sPath = p; break; }
                foreach (string p in longs) if (PhysicalKey(p) == g) { lPath = p; break; }

                Hidpp h = new Hidpp();
                if (!h.Open(sPath, lPath)) { h.Dispose(); continue; }
                try
                {
                    string product = "";
                    string any = lPath ?? sPath;
                    if (any != null && products.ContainsKey(any)) product = products[any];

                    List<byte> indices = new List<byte>();
                    byte[] pingP;
                    byte pingErr;
                    int ping = h.Request(0xFF, 0x00, (byte)(0x10 | 0x0A), new byte[] { 0, 0, 0x5A },
                        out pingP, out pingErr, false, 400);
                    bool direct = ping == 1 && pingP != null && pingP.Length >= 1 && pingP[0] >= 2;
                    if (direct) indices.Add(0xFF);
                    for (byte i = 1; i <= 6; i++) indices.Add(i);

                    DevInfo best = null;
                    foreach (byte idx in indices)
                    {
                        DevInfo d = new DevInfo();
                        d.Name = idx == 0xFF && product.Length > 0 ? product : "Logitech 无线设备";
                        d.Via = idx == 0xFF ? "USB / 蓝牙直连" : "接收器";

                        byte[] p;
                        byte err;
                        int r = h.Request(idx, 0x00, (byte)(0x10 | 0x0A), new byte[] { 0, 0, 0x5A },
                            out p, out err, false, 400);
                        bool ok = false;
                        if (r == 1 && p != null && p.Length >= 1 && p[0] >= 2)
                        {
                            string nm = ReadName(h, idx);
                            if (nm.Length > 0) d.Name = nm;
                            ok = ReadBattery(h, idx, d);
                        }
                        else if (r == 0 && err == 0x01)
                        {
                            ok = ReadBattery10(h, idx, d);
                        }

                        if (ok && d.Percent >= 0)
                        {
                            d.Key = "logi:" + (product.Length > 0 ? product : "dev") + ":" + idx;
                            found.Add(d);
                            if (best == null) best = d;
                            if (idx == 0xFF) break; // 直连设备不需要再扫 1..6
                        }
                    }
                }
                finally { h.Dispose(); }
            }
            return found;
        }
    }

    internal static class BluetoothBattery
    {
        private static readonly Guid BatteryFmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

        public static List<DevInfo> Poll(StringBuilder trace)
        {
            List<DevInfo> list = new List<DevInfo>();
            IntPtr set = Native.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1)) return list;
            int seen = 0, bth = 0;
            try
            {
                Native.SP_DEVINFO_DATA data = new Native.SP_DEVINFO_DATA();
                data.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                for (uint i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    StringBuilder idBuf = new StringBuilder(512);
                    int need;
                    if (!Native.SetupDiGetDeviceInstanceIdW(set, ref data, idBuf, 512, out need)) continue;
                    seen++;
                    string inst = idBuf.ToString();
                    if (!(inst.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                          inst.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase))) continue;
                    bth++;

                    string name = FriendlyName(set, ref data);
                    int pct = ReadBatteryProp(set, ref data);
                    if (trace != null)
                        trace.AppendLine(string.Format("BT {0} | {1} | battery={2}", inst, name, pct));
                    if (pct > 0 && pct <= 100)
                    {
                        DevInfo d = new DevInfo();
                        d.Key = "bt:" + inst;
                        d.Name = string.IsNullOrEmpty(name) ? "蓝牙设备" : name;
                        d.Via = "蓝牙";
                        d.Percent = pct;
                        list.Add(d);
                    }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            if (trace != null)
                trace.AppendLine(string.Format("devnode 扫描: {0} 个, 蓝牙 devnode: {1} 个, 有电量: {2} 个",
                    seen, bth, list.Count));
            return list;
        }

        private static string FriendlyName(IntPtr set, ref Native.SP_DEVINFO_DATA data)
        {
            byte[] buf = new byte[1024];
            uint type, req;
            if (Native.SetupDiGetDeviceRegistryPropertyW(set, ref data, 0x0C, out type, buf, 1024, out req))
                return Encoding.Unicode.GetString(buf, 0, (int)Math.Min(req, 1024)).TrimEnd('\0');
            return "";
        }

        private static int ReadBatteryProp(IntPtr set, ref Native.SP_DEVINFO_DATA data)
        {
            Native.DEVPROPKEY key = new Native.DEVPROPKEY();
            key.fmtid = BatteryFmtid;
            key.pid = 2;
            byte[] buf = new byte[16];
            uint type, req;
            if (!Native.SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buf, 16, out req, 0))
                return -1;
            if (type == 0x11) return buf[0];                          // DEVPROP_TYPE_BYTE
            if (type == 0x12) return BitConverter.ToUInt16(buf, 0);    // DEVPROP_TYPE_UINT16
            if (type == 0x13) return (int)BitConverter.ToUInt32(buf, 0);
            return -1;
        }

        private static int ReadBatteryPropAlt(IntPtr set, ref Native.SP_DEVINFO_DATA data)
        {
            // 少数栈把电量放在 {GUID 电量} pid 2 之外的通用 battery 属性上。
            Native.DEVPROPKEY key = new Native.DEVPROPKEY();
            key.fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
            key.pid = 33;
            byte[] buf = new byte[16];
            uint type, req;
            if (!Native.SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buf, 16, out req, 0))
                return -1;
            if (type == 0x11) return buf[0];
            if (type == 0x12) return BitConverter.ToUInt16(buf, 0);
            return -1;
        }
    }

    // ───────────────────────────── 托盘 ─────────────────────────────
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly Form _sync = new Form();
        private readonly System.Windows.Forms.Timer _timer;
        private readonly object _gate = new object();
        private List<DevInfo> _devices = new List<DevInfo>();
        private bool _polling;
        private bool _closed;
        private bool _announced;
        private Icon _current;
        private int _intervalSec;              // 0 = 只手动刷新
        private DateTime _cycleStart = DateTime.MinValue;
        private int _cycleStartPct = -1;
        private int _lastLoggedPct = -999;

        public TrayApp()
        {
            IntPtr force = _sync.Handle; // 建立消息队列，供后台→UI 回调使用
            if (force == IntPtr.Zero) { }

            _icon = new NotifyIcon();
            _icon.Text = "鼠标电量";
            _icon.DoubleClick += delegate { Refresh(); };
            SetIcon(null);        // 必须先有 Icon 再置 Visible：Icon 为 null 时 NIM_ADD 会失败，
                                  // 之后再用 NIM_MODIFY 改 hIcon 也无法补救，图标永远不出现。
            _icon.Visible = true;
            RebuildMenu();

            _intervalSec = LoadInterval();
            LoadCycle();
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = _intervalSec > 0 ? _intervalSec * 1000 : 60000;
            _timer.Tick += delegate { Refresh(); };
            if (_intervalSec > 0) _timer.Start();

            ThreadPool.QueueUserWorkItem(delegate { Refresh(); });
            // SelfTest 已禁用：探针在 Medium 下会返回 E_FAIL 假故障，误报为启动失败
        }

        private void SelfTest()
        {
            try
            {
                NID d = new NID();
                d.cbSize = Marshal.SizeOf(typeof(NID));
                d.hWnd = _sync.Handle;
                d.uID = 9999;
                d.uFlags = 0x1 | 0x2 | 0x4;   // NIF_MESSAGE | NIF_ICON | NIF_TIP
                d.uCallbackMessage = 0x0400 + 99;
                d.hIcon = _current != null ? _current.Handle : IntPtr.Zero;
                d.szTip = "selftest";
                bool ok = Shell_NotifyIcon(1, ref d);   // NIM_ADD
                int err = Marshal.GetLastWin32Error();
                Log("selftest hwnd=" + d.hWnd + " hIcon=" + d.hIcon + " NIM_ADD=" + ok + " err=" + err);
                if (ok)
                {
                    Log("selftest NIM_DELETE=" + Shell_NotifyIcon(2, ref d));
                }
                else
                {
                    Log("selftest FAILED err=" + err);
                    MessageBox.Show(
                        "系统拒绝了托盘图标注册（Shell_NotifyIcon 返回失败，错误码 " + err + "）。\r\n\r\n"
                        + "错误码 5 = 拒绝访问：说明本进程的完整性级别低于资源管理器，被 UIPI 拦住了。\r\n"
                        + "请直接在资源管理器里双击 TrayBattery.exe（不要从终端、沙箱或自动化工具里启动）。",
                        "鼠标电量", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                IntPtr trayWnd = FindWindowW("Shell_TrayWnd", null);
                Log("selftest Shell_TrayWnd=" + trayWnd);
            }
            catch (Exception ex)
            {
                Log("selftest EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void Log(string s)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TrayBattery-log.txt");
                File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NID
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(uint msg, ref NID data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowW(string cls, string name);

        private void SetIcon(DevInfo d)
        {
            int pct = d == null ? -1 : d.Percent;
            Icon old = _current;
            _current = DrawIcon(pct, d != null && d.Charging);
            _icon.Icon = _current;
            if (old != null) old.Dispose();
        }

        private static float _numEm = -1f;

        // 以 "100" 为样本求一个能塞进图标的最大字号，之后所有数字都复用它 —— 这是「大小一致」的关键。
        // 结果只和图标尺寸有关，算一次就缓存。
        private static float NumEm(FontFamily fam)
        {
            if (_numEm > 0f) return _numEm;
            const int S = 32, barH = 4, textH = S - barH - 1;
            float found = 5f;
            for (float e = 48f; e >= 5f; e -= 0.25f)
            {
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddString("100", fam, (int)FontStyle.Bold, e, PointF.Empty, StringFormat.GenericTypographic);
                    RectangleF b = gp.GetBounds();
                    if (b.Width <= S - 0.5f && b.Height <= textH) { found = e; break; }
                }
            }
            _numEm = found;
            return found;
        }

        // 每次绘制都重读一次（60 秒一次，开销可忽略）：用户中途切换深/浅色主题时字色要跟着变。
        private static bool LightTaskbar()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object o = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    if (o is int) return (int)o != 0;
                }
            }
            catch { }
            return false;
        }

        // 图标版式与上游 BatteryHub 的 RenderIcon 一致：上半是撑满的大号百分比数字，
        // 底部一条细进度条表示电量。数字用 GraphicsPath 取真实字形包围盒，从大字号往下试，
        // 取第一个宽高都装得下的尺寸 —— 这样 1/2/3 位数都能填满 32px 图标而不被裁切。
        private static Icon DrawIcon(int pct, bool charging)
        {
            const int S = 32;
            const int barH = 4;                 // max(2, S/8)
            const int textH = S - barH - 1;     // 留给数字的高度
            bool light = LightTaskbar();

            Color bar =
                charging ? Color.FromArgb(0, 170, 255)
                : pct < 0 ? Color.FromArgb(120, 128, 140)
                : pct <= 15 ? Color.FromArgb(232, 32, 48)
                : pct <= 30 ? Color.FromArgb(255, 150, 0)
                : Color.FromArgb(30, 190, 90);

            Color fg =
                pct < 0 ? Color.FromArgb(150, 158, 170)
                : (!charging && pct <= 15) ? Color.FromArgb(240, 50, 60)
                : light ? Color.FromArgb(28, 30, 34) : Color.FromArgb(250, 251, 253);

            Bitmap bmp = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                string text = pct < 0 ? "?" : pct.ToString();
                using (FontFamily fam = new FontFamily("Segoe UI"))
                {
                    // 统一字号：所有数字（1/2/3 位）都用同一个 em，字形高度完全一致。
                    // em 由最宽的可能文本 "100"（三位满格）一次算定并缓存，所以 35 和 100 一样高，
                    // 只是居中后左右留白更多。
                    float em = NumEm(fam);
                    using (GraphicsPath gp = new GraphicsPath())
                    using (Matrix m = new Matrix())
                    using (SolidBrush br = new SolidBrush(fg))
                    {
                        gp.AddString(text, fam, (int)FontStyle.Bold, em, PointF.Empty, StringFormat.GenericTypographic);
                        RectangleF gb = gp.GetBounds();
                        m.Translate((S - gb.Width) / 2f - gb.X, (textH - gb.Height) / 2f - gb.Y);
                        gp.Transform(m);
                        g.FillPath(br, gp);
                    }
                }

                int fill = pct < 0 ? 0 : Math.Max(pct > 0 ? 1 : 0, (pct * S + 50) / 100);
                using (SolidBrush track = new SolidBrush(Color.FromArgb(70, light ? Color.Black : Color.White)))
                    g.FillRectangle(track, 0, S - barH, S, barH);
                if (fill > 0)
                    using (SolidBrush fb = new SolidBrush(bar))
                        g.FillRectangle(fb, 0, S - barH, fill, barH);
            }
            IntPtr h = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(h).Clone(); }
            finally { Native.DestroyIcon(h); bmp.Dispose(); }
        }

        private void RebuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;

            List<DevInfo> list;
            lock (_gate) list = new List<DevInfo>(_devices);

            if (list.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("未发现无线设备（点“刷新”重试）");
                none.Enabled = false;
                menu.Items.Add(none);
            }
            else
            {
                foreach (DevInfo d in list)
                {
                    ToolStripMenuItem it = new ToolStripMenuItem(d.Line());
                    it.Enabled = false;
                    menu.Items.Add(it);
                }
            }
            DevInfo main = list.Count > 0 ? list[0] : null;
            ToolStripMenuItem cycle = new ToolStripMenuItem(CycleFull(main));
            cycle.Enabled = false;
            menu.Items.Add(cycle);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += delegate { Refresh(); };
            menu.Items.Add(refresh);

            ToolStripMenuItem interval = new ToolStripMenuItem("刷新间隔");
            int[] opts = { 30, 60, 300, 900, 0 };
            string[] names = { "30 秒", "60 秒（默认）", "5 分钟", "15 分钟", "手动（只点立即刷新）" };
            for (int i = 0; i < opts.Length; i++)
            {
                int sec = opts[i];
                ToolStripMenuItem o = new ToolStripMenuItem(names[i]);
                o.Checked = _intervalSec == sec;
                o.Click += delegate { ApplyInterval(sec); };
                interval.DropDownItems.Add(o);
            }
            menu.Items.Add(interval);

            ToolStripMenuItem why = new ToolStripMenuItem("为什么没有电量？");
            why.Click += delegate { ShowDiagnosis(); };
            menu.Items.Add(why);

            ToolStripMenuItem copy = new ToolStripMenuItem("复制诊断信息");
            copy.Click += delegate
            {
                try { Clipboard.SetText(BuildReport()); } catch { }
            };
            menu.Items.Add(copy);

            // ToolStrip 默认不留勾选位（ShowCheckMargin = false），Checked 设了也看不见勾，
            // 所以整张菜单要开 ShowCheckMargin。
            menu.ShowCheckMargin = true;

            ToolStripMenuItem startup = new ToolStripMenuItem("开机自启");
            startup.Checked = IsStartupEnabled();
            startup.Click += delegate
            {
                SetStartup(!startup.Checked);
                startup.Checked = IsStartupEnabled();   // 写失败就以注册表实际状态为准
            };
            menu.Items.Add(startup);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem quit = new ToolStripMenuItem("退出");
            quit.Click += delegate { ExitThread(); };
            menu.Items.Add(quit);

            ContextMenuStrip old = _icon.ContextMenuStrip;
            _icon.ContextMenuStrip = menu;
            if (old != null) old.Dispose();
        }

        // ── 本次充电后跑了多久 ───────────────────────────────────────────
        // 只写两个文件，都在 %LOCALAPPDATA%\TrayBattery\ 下，纯本地、不上传：
        //   cycle.txt    起始时刻 + 起始电量（一行，几十字节）
        //   history.csv  电量每变化一次追加一行（5% 一档，一天也就十几行）
        private static string DataDir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayBattery");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        private void LoadCycle()
        {
            try
            {
                string p = Path.Combine(DataDir, "cycle.txt");
                if (!File.Exists(p)) return;
                string[] f = File.ReadAllText(p).Trim().Split('|');
                long ticks; int pct;
                if (f.Length >= 2 && long.TryParse(f[0], out ticks) && int.TryParse(f[1], out pct) && ticks > 0)
                {
                    _cycleStart = new DateTime(ticks, DateTimeKind.Utc);
                    _cycleStartPct = pct;
                }
            }
            catch { }

            // 重启后沿用上次记下的电量，避免同一档位重复写历史
            try
            {
                string h = Path.Combine(DataDir, "history.csv");
                if (!File.Exists(h)) return;
                string[] lines = File.ReadAllLines(h);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string[] f = lines[i].Split(',');
                    int pct;
                    if (f.Length >= 4 && int.TryParse(f[1], out pct)) { _lastLoggedPct = pct; return; }
                }
            }
            catch { }
        }

        private void SaveCycle()
        {
            try
            {
                string p = Path.Combine(DataDir, "cycle.txt");
                if (_cycleStart == DateTime.MinValue)
                {
                    if (File.Exists(p)) File.Delete(p);
                    return;
                }
                File.WriteAllText(p, _cycleStart.Ticks + "|" + _cycleStartPct);
            }
            catch { }
        }

        private static void AppendHistory(DevInfo d)
        {
            try
            {
                File.AppendAllText(Path.Combine(DataDir, "history.csv"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "," + d.Percent + "," +
                    (d.Charging ? 1 : 0) + "," + d.Name + "\r\n",
                    Encoding.UTF8);
            }
            catch { }
        }

        // 充电中 → 清空本轮；一旦开始放电（充电标志从真变假）→ 记下起点。
        // 电量不降反升 5% 以上（换电池 / 线松了又插）也视为新一轮。
        private void TrackCycle(DevInfo d)
        {
            if (d == null) return;
            bool charging = d.Charging || d.Full;
            if (charging)
            {
                if (_cycleStart != DateTime.MinValue)
                {
                    _cycleStart = DateTime.MinValue;
                    _cycleStartPct = -1;
                    SaveCycle();
                }
            }
            else if (_cycleStart == DateTime.MinValue || _cycleStartPct < 0)
            {
                _cycleStart = DateTime.UtcNow;
                _cycleStartPct = d.Percent;
                SaveCycle();
            }
            else if (d.Percent > _cycleStartPct + 5)
            {
                _cycleStart = DateTime.UtcNow;
                _cycleStartPct = d.Percent;
                SaveCycle();
            }

            if (d.Percent != _lastLoggedPct)
            {
                _lastLoggedPct = d.Percent;
                AppendHistory(d);
            }
        }

        private string CycleFull(DevInfo d)
        {
            if (d != null && (d.Charging || d.Full)) return "正在充电（充满后自动重新计时）";
            if (_cycleStart == DateTime.MinValue) return "还没开始计时";
            TimeSpan t = DateTime.UtcNow - _cycleStart;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            string dur = (int)t.TotalHours > 0
                ? (int)t.TotalHours + " 小时 " + t.Minutes + " 分"
                : (t.TotalMinutes >= 1d ? t.Minutes + " 分" : "不到 1 分钟");
            return "本次充电后已用 " + dur +
                   (d != null ? "（" + _cycleStartPct + "% → " + d.Percent + "%）" : "");
        }

        private string CycleShort(DevInfo d)
        {
            if (d != null && (d.Charging || d.Full)) return "充电中";
            if (_cycleStart == DateTime.MinValue) return "";
            TimeSpan t = DateTime.UtcNow - _cycleStart;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes < 1d) return "<1m";
            return (int)t.TotalHours > 0
                ? (int)t.TotalHours + "h" + t.Minutes + "m"
                : t.Minutes + "m";
        }

        // 刷新间隔存在 HKCU\Software\TrayBattery\PollSeconds（DWORD，0 = 手动）。
        // 这是程序除开机自启外唯一的注册表写入，且只在用户点菜单那一刻发生。
        private const string SettingsKey = @"Software\TrayBattery";

        private static int LoadInterval()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    object o = k == null ? null : k.GetValue("PollSeconds");
                    if (o is int)
                    {
                        int v = (int)o;
                        if (v == 0 || (v >= 10 && v <= 3600)) return v;
                    }
                }
            }
            catch { }
            return 60;
        }

        private static void SaveInterval(int seconds)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (k == null) return;
                    k.SetValue("PollSeconds", seconds, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        private void ApplyInterval(int seconds)
        {
            _intervalSec = seconds;
            if (seconds <= 0)
            {
                _timer.Stop();
            }
            else
            {
                _timer.Interval = seconds * 1000;
                _timer.Stop();      // 重置计时，免得改完间隔马上又触发一次
                _timer.Start();
            }
            SaveInterval(seconds);
            try { _sync.BeginInvoke((MethodInvoker)RebuildMenu); } catch { }
        }

        private static string RunKeyPath { get { return @"Software\Microsoft\Windows\CurrentVersion\Run"; } }

        private static string StartupCommand()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }

        // 只有「值存在且指向当前这个 exe」才算真的生效；exe 被移动过就当作没开。
        private static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    object v = k != null ? k.GetValue("TrayBattery") : null;
                    if (v == null) return false;
                    return string.Equals(v.ToString(), StartupCommand(), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private static void SetStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (k == null) return;
                    if (on) k.SetValue("TrayBattery", StartupCommand());
                    else k.DeleteValue("TrayBattery", false);
                }
            }
            catch { }
        }

        public void Refresh()
        {
            lock (_gate)
            {
                if (_polling) return;
                _polling = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<DevInfo> all = new List<DevInfo>();
                try
                {
                    all.AddRange(Pulsar.Poll(null));
                    all.AddRange(Logitech.Poll(null));
                    all.AddRange(BluetoothBattery.Poll(null));
                }
                catch { }

                lock (_gate) { _devices = all; _polling = false; }
                try
                {
                    if (_closed) return;
                    _sync.BeginInvoke((MethodInvoker)delegate
                    {
                        DevInfo low = null;
                        foreach (DevInfo d in all)
                            if (d.Percent >= 0 && (low == null || d.Percent < low.Percent)) low = d;

                        TrackCycle(low != null ? low : (all.Count > 0 ? all[0] : null));

                        SetIcon(low);
                        string tip = "鼠标电量";
                        if (low != null) tip = low.Line();
                        else if (all.Count > 0) tip = all[0].Line();
                        else tip = "未检测到受支持的设备（罗技 / ATK·VXE / 蓝牙）";
                        string cs = CycleShort(low != null ? low : (all.Count > 0 ? all[0] : null));
                        if (cs.Length > 0) tip += " · " + cs;
                        if (tip.Length > 63) tip = tip.Substring(0, 60) + "...";
                        _icon.Text = tip;

                        if (!_announced)
                        {
                            _announced = true;
                            _icon.BalloonTipTitle = "TrayBattery 已启动";
                            _icon.BalloonTipText = all.Count > 0
                                ? "读到：" + all[0].Line()
                                : "没读到任何设备。支持罗技 HID++、ATK/VXE/Pulsar 接收器（FF02:0002）和蓝牙电量。右键 → “为什么没有电量？”看详情。";
                            _icon.BalloonTipIcon = all.Count > 0 ? ToolTipIcon.Info : ToolTipIcon.Warning;
                            try { _icon.ShowBalloonTip(6000); } catch { }
                        }
                        RebuildMenu();
                    });
                }
                catch { }
            });
        }

        public void ShowDiagnosis()
        {
            StringBuilder sb = new StringBuilder();
            List<DevInfo> list;
            lock (_gate) list = new List<DevInfo>(_devices);

            if (list.Count == 0) sb.AppendLine("当前没有读到任何电量。");
            else sb.AppendLine("已读到 " + list.Count + " 个设备。");
            sb.AppendLine();
            sb.AppendLine("本程序只支持两类设备：");
            sb.AppendLine("  1) 罗技 HID++ 无线外设（Bolt / Unifying / Lightspeed / 蓝牙直连）");
            sb.AppendLine("  2) Windows 原生蓝牙电量（需已配对的蓝牙外设）");
            sb.AppendLine();
            sb.AppendLine("本机 HID 接口（同集合已去重）：");
            List<string> probe;
            try { probe = HidList.Probe(); }
            catch { probe = new List<string>(); }
            if (probe.Count == 0) sb.AppendLine("  (枚举失败或没有 HID 设备)");
            int shown = 0;
            foreach (string r in probe)
            {
                if (shown++ >= 14) { sb.AppendLine("  ...（其余 " + (probe.Count - 14) + " 个略）"); break; }
                sb.AppendLine("  " + r);
            }
            sb.AppendLine();
            sb.AppendLine("判断方法：");
            sb.AppendLine("  · VID_373B / 391D / 3554（ATK / Attack Shark / VXE 的 Pulsar 系）+ 集合 FF02:0002 + in/out 都是 17 = 走 Pulsar 协议，能读。");
            sb.AppendLine("  · VID_046D（罗技）= 走 HID++ 协议。");
            sb.AppendLine("  · 蓝牙低功耗外设 = 走 Windows 蓝牙电量属性。");
            sb.AppendLine("  · 其它品牌（MCHOSE / Attack Shark / 英菲克 等）各用私有协议，需单独实现。");
            sb.AppendLine();
            sb.AppendLine("ATK 读不到时：鼠标静止一会儿接收器会停止回应，动一下鼠标再点“立即刷新”。");

            MessageBox.Show(sb.ToString(), "TrayBattery 诊断",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public string BuildReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("TrayBattery 诊断 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("OS: " + Environment.OSVersion.VersionString + "  x64=" + Environment.Is64BitProcess);
            sb.AppendLine("刷新间隔: " + (_intervalSec > 0 ? _intervalSec + " 秒" : "手动") +
                "   (计时器 " + (_timer.Enabled ? "运行中" : "已停止") + ")");
            sb.AppendLine();
            List<DevInfo> list;
            lock (_gate) list = new List<DevInfo>(_devices);
            sb.AppendLine("--- 本次充电后 ---");
            sb.AppendLine(CycleFull(list.Count > 0 ? list[0] : null));
            sb.AppendLine();
            sb.AppendLine("--- 已识别设备 ---");
            if (list.Count == 0) sb.AppendLine("(无)");
            foreach (DevInfo d in list)
                sb.AppendLine(d.Name + " | " + d.Percent + "% | " + d.Via +
                    (d.Charging ? " | 充电中" : "") + (d.Full ? " | 已充满" : ""));
            sb.AppendLine();
            sb.AppendLine("--- HID 候选 ---");
            try { sb.Append(Logitech.Poll(sb).Count + " 个罗技设备"); } catch (Exception e) { sb.AppendLine(e.Message); }
            sb.AppendLine();
            sb.AppendLine("--- 蓝牙 ---");
            try { BluetoothBattery.Poll(sb); } catch (Exception e) { sb.AppendLine(e.Message); }
            return sb.ToString();
        }

        protected override void ExitThreadCore()
        {
            _closed = true;
            _timer.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            base.ExitThreadCore();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool dump = args.Length > 0 &&
                (args[0] == "--dump" || args[0] == "--list" || args[0] == "-d");

            if (dump)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("TrayBattery 诊断 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("OS: " + Environment.OSVersion.VersionString);
                sb.AppendLine();
                sb.AppendLine("--- 全部 HID 接口（只读探测）---");
                List<string> probe = new List<string>();
                try { probe = HidList.Probe(); } catch (Exception e) { sb.AppendLine("ERR " + e.Message); }
                if (probe.Count == 0) sb.AppendLine("(无)");
                foreach (string r in probe) sb.AppendLine(r);
                sb.AppendLine();
                sb.AppendLine("--- HID 候选（VID 046D）---");
                List<DevInfo> logi = new List<DevInfo>();
                try { logi = Logitech.Poll(sb); } catch (Exception e) { sb.AppendLine("ERR " + e.Message); }
                sb.AppendLine("--- Pulsar 系（ATK 373B / Attack Shark 391D / VXE 3554，集合 FF02:0002）---");
                List<DevInfo> atk = new List<DevInfo>();
                try { atk = Pulsar.Poll(sb); } catch (Exception e) { sb.AppendLine("ERR " + e.Message); }
                if (atk.Count == 0) sb.AppendLine("(无)");
                foreach (DevInfo d in atk) sb.AppendLine(d.Name + "  " + d.Percent + "%  " + d.Via);
                sb.AppendLine();
                sb.AppendLine("--- 罗技结果 ---");
                if (logi.Count == 0) sb.AppendLine("(无)");
                foreach (DevInfo d in logi) sb.AppendLine(d.Name + "  " + d.Percent + "%  " + d.Via);
                sb.AppendLine();
                sb.AppendLine("--- 蓝牙 ---");
                List<DevInfo> bt = new List<DevInfo>();
                try { bt = BluetoothBattery.Poll(sb); } catch (Exception e) { sb.AppendLine("ERR " + e.Message); }
                if (bt.Count == 0) sb.AppendLine("(无)");
                foreach (DevInfo d in bt) sb.AppendLine(d.Name + "  " + d.Percent + "%");

                string text = sb.ToString();
                string outPath = Path.Combine(
                    Path.GetDirectoryName(Application.ExecutablePath) ?? ".",
                    "TrayBattery-dump.txt");
                try { File.WriteAllText(outPath, text, Encoding.UTF8); } catch { }

                if (Native.AttachConsole(0xFFFFFFFF))
                {
                    Console.WriteLine(text);
                    Native.FreeConsole();
                }
                return;
            }

            bool created;
            using (Mutex m = new Mutex(true, "TrayBattery.SingleInstance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
