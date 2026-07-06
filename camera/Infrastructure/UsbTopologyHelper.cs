using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace camera.Infrastructure
{
    public enum UsbSpeedType
    {
        Unknown,
        Usb2_HighSpeed,   // USB 2.0 (~40MB/s usable)
        Usb3_SuperSpeed   // USB 3.0/3.1 (~350MB/s usable)
    }

    public class UsbTopologyInfo
    {
        public UsbSpeedType SpeedType { get; set; } = UsbSpeedType.Unknown;
        public string SpeedDescription => SpeedType switch
        {
            UsbSpeedType.Usb2_HighSpeed => "USB 2.0 (High-Speed)",
            UsbSpeedType.Usb3_SuperSpeed => "USB 3.0 (SuperSpeed)",
            _ => "USB 通用总线"
        };
        public string ControllerId { get; set; } = "";
        public bool IsUsb2 => SpeedType == UsbSpeedType.Usb2_HighSpeed;
    }

    public static class UsbTopologyHelper
    {
        #region Native P/Invoke for Hardware USB Speed Query (cfgmgr32.dll)

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;

            public DEVPROPKEY(Guid fmtid, uint pid)
            {
                this.fmtid = fmtid;
                this.pid = pid;
            }
        }

        // DEVPKEY_Device_UsbDeviceSpeed: {c8c0c196-d589-49f4-92bd-5ce04746487f}, 9
        private static readonly DEVPROPKEY DEVPKEY_Device_UsbDeviceSpeed =
            new DEVPROPKEY(new Guid("c8c0c196-d589-49f4-92bd-5ce04746487f"), 9);

        // DEVPKEY_Device_LocationPaths: {a45c254e-df1c-4efd-8020-67d146a850e0}, 37
        private static readonly DEVPROPKEY DEVPKEY_Device_LocationPaths =
            new DEVPROPKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 37);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(
            uint dnDevInst,
            ref DEVPROPKEY PropertyKey,
            out uint PropertyType,
            byte[] PropertyBuffer,
            ref uint PropertyBufferSize,
            uint ulFlags);

        #endregion

        private static readonly Dictionary<string, UsbTopologyInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new();

        /// <summary>
        /// 根据设备路径或设备名称获取 USB 拓扑信息（协议规格 USB 2.0 / 3.0 及主控信息）
        /// </summary>
        public static UsbTopologyInfo GetTopologyInfo(string devicePath, string deviceName = null)
        {
            if (string.IsNullOrEmpty(devicePath))
            {
                return new UsbTopologyInfo { SpeedType = UsbSpeedType.Unknown };
            }

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(devicePath, out var cached))
                {
                    return cached;
                }
            }

            var info = DetectUsbTopology(devicePath, deviceName);

            lock (_cacheLock)
            {
                _cache[devicePath] = info;
            }

            return info;
        }

        private static UsbTopologyInfo DetectUsbTopology(string devicePath, string deviceName)
        {
            var info = new UsbTopologyInfo();

            try
            {
                // 1. 优先使用 Windows SetupAPI / ConfigMgr 查询底层的真实物理硬件速率 (LocationPaths / ACPI 拓扑)
                var hwSpeed = QueryHardwareUsbSpeed(devicePath);
                if (hwSpeed != UsbSpeedType.Unknown)
                {
                    info.SpeedType = hwSpeed;
                }
                else
                {
                    // 2. 若硬件属性查询未命中/不适用，回退至基于 Path 和 Name 的静态特征识别
                    string lowerPath = (devicePath ?? "").ToLower();
                    string lowerName = (deviceName ?? "").ToLower();

                    if (lowerPath.Contains("usb3") || lowerPath.Contains("xhci") || lowerPath.Contains("superspeed") ||
                        lowerName.Contains("usb3") || lowerName.Contains("superspeed") || lowerName.Contains("3.0"))
                    {
                        info.SpeedType = UsbSpeedType.Usb3_SuperSpeed;
                    }
                    else if (lowerPath.Contains("ehci") || lowerPath.Contains("usb2") || lowerName.Contains("usb2") || lowerName.Contains("2.0"))
                    {
                        info.SpeedType = UsbSpeedType.Usb2_HighSpeed;
                    }
                    else
                    {
                        // 默认当作 USB 3.0 / 通用总线处理
                        info.SpeedType = UsbSpeedType.Usb3_SuperSpeed;
                    }
                }

                // 提取主控分组标识 (例: usb#vid_xxxx&pid_xxxx#...)
                var match = Regex.Match(devicePath ?? "", @"usb#vid_[0-9a-f]+&pid_[0-9a-f]+#([^#{}]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    info.ControllerId = match.Groups[1].Value;
                }
                else
                {
                    info.ControllerId = devicePath ?? "";
                }
            }
            catch
            {
                info.SpeedType = UsbSpeedType.Unknown;
            }

            return info;
        }

        /// <summary>
        /// 从 DirectShow devicePath 提取 Windows PnP Device Instance ID 并查询实际物理连接速率
        /// </summary>
        private static UsbSpeedType QueryHardwareUsbSpeed(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return UsbSpeedType.Unknown;

            try
            {
                string instanceId = ExtractDeviceInstanceId(devicePath);
                if (string.IsNullOrEmpty(instanceId)) return UsbSpeedType.Unknown;

                int cr = CM_Locate_DevNodeW(out uint devInst, instanceId, 0);
                if (cr != 0 && instanceId.Contains("\\"))
                {
                    string truncated = instanceId.Substring(0, instanceId.LastIndexOf('\\'));
                    cr = CM_Locate_DevNodeW(out devInst, truncated, 0);
                }

                if (cr != 0) return UsbSpeedType.Unknown;

                // 向上递归查询当前设备节点或其父节点（USB 复合设备/集线器节点）
                uint currentDevInst = devInst;
                for (int depth = 0; depth < 4; depth++)
                {
                    byte[] buffer = new byte[1024];
                    uint bufferSize = (uint)buffer.Length;

                    // 1. 优先从 LocationPaths 查询 ACPI 物理端口路由 (HSxx 表示 USB 2.0 High-Speed, SSxx 表示 USB 3.0 SuperSpeed)
                    var keyLoc = DEVPKEY_Device_LocationPaths;
                    if (CM_Get_DevNode_PropertyW(currentDevInst, ref keyLoc, out uint _, buffer, ref bufferSize, 0) == 0 && bufferSize > 0)
                    {
                        string loc = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)bufferSize).TrimEnd('\0').ToUpper();
                        if (loc.Contains("#ACPI(SS") || loc.Contains("#SS"))
                        {
                            return UsbSpeedType.Usb3_SuperSpeed;
                        }
                        if (loc.Contains("#ACPI(HS") || loc.Contains("#HS") || loc.Contains("EHCI") || loc.Contains("USB2"))
                        {
                            return UsbSpeedType.Usb2_HighSpeed;
                        }
                    }

                    // 2. 备用检测：查询 UsbDeviceSpeed 属性
                    bufferSize = (uint)buffer.Length;
                    var keySpeed = DEVPKEY_Device_UsbDeviceSpeed;
                    if (CM_Get_DevNode_PropertyW(currentDevInst, ref keySpeed, out uint _, buffer, ref bufferSize, 0) == 0 && bufferSize > 0)
                    {
                        byte speedVal = buffer[0];
                        if (speedVal == 2 || speedVal == 1 || speedVal == 0)
                        {
                            return UsbSpeedType.Usb2_HighSpeed;
                        }
                        else if (speedVal >= 3)
                        {
                            return UsbSpeedType.Usb3_SuperSpeed;
                        }
                    }

                    if (CM_Get_Parent(out uint parentInst, currentDevInst, 0) != 0 || parentInst == 0)
                    {
                        break;
                    }
                    currentDevInst = parentInst;
                }
            }
            catch
            {
                // 忽略 P/Invoke 异常，安全回退
            }

            return UsbSpeedType.Unknown;
        }

        private static string ExtractDeviceInstanceId(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;

            string id = devicePath;

            // 剥离 DirectShow 前缀: @device:pnp:\\?\ 或 \\?\
            int prefixIdx = id.IndexOf(@"\\?\", StringComparison.Ordinal);
            if (prefixIdx >= 0)
            {
                id = id.Substring(prefixIdx + 4);
            }
            else if (id.StartsWith("@device:pnp:", StringComparison.OrdinalIgnoreCase))
            {
                id = id.Substring("@device:pnp:".Length);
            }

            // 剥离 GUID 后缀: #{65e8773d-8f56-11d0-a3b9-00a0c9223196}
            int suffixIdx = id.LastIndexOf('#');
            if (suffixIdx > 0 && id.IndexOf('{', suffixIdx) > 0)
            {
                id = id.Substring(0, suffixIdx);
            }

            // 将 '#' 转换为 '\' 形成标准 PnP Instance ID: USB\VID_xxxx&PID_xxxx...\...
            id = id.Replace('#', '\\');

            return id.Trim();
        }

        /// <summary>
        /// 估算单通道数据吞吐量 (MB/s)
        /// </summary>
        public static double CalculateEstimatedMbPerSec(int width, int height, double fps, string format = "MJPG")
        {
            if (width <= 0 || height <= 0 || fps <= 0) return 0;

            double bpp = 1.8; // 默认 MJPG 压缩平均 1.8 bits per pixel
            string fmt = format?.ToUpper() ?? "MJPG";
            if (fmt == "YUY2") bpp = 16.0;
            else if (fmt == "NV12" || fmt == "I420") bpp = 12.0;

            double bytesPerFrame = (width * height * bpp) / 8.0;
            double bytesPerSec = bytesPerFrame * fps;
            return Math.Round(bytesPerSec / (1024.0 * 1024.0), 1);
        }

        /// <summary>
        /// 预判分辨率调整是否存在高风险
        /// </summary>
        public static bool PredictResolutionRisk(
            string devicePath,
            string currentRes,
            string targetRes,
            int activeCameraCount,
            double currentTotalMbPerSec,
            out string warningTitle,
            out string warningMessage)
        {
            warningTitle = null;
            warningMessage = null;

            if (string.IsNullOrEmpty(targetRes) || targetRes == currentRes)
                return false;

            var parts = targetRes.Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int targetW) || !int.TryParse(parts[1], out int targetH))
                return false;

            var topo = GetTopologyInfo(devicePath);

            // 计算该摄像头调整后的预估单通道吞吐量 (按 30fps MJPG 估算)
            double targetSingleMb = CalculateEstimatedMbPerSec(targetW, targetH, 30, "MJPG");

            // 场景 1: 在 USB 2.0 接口上试图开启 4K (3840x2160) 分辨率
            if (topo.IsUsb2 && targetW >= 3840)
            {
                warningTitle = "⚠️ USB 2.0 带宽超限预警";
                warningMessage = $"当前通道连接在【USB 2.0 (High-Speed)】接口上。\n\n" +
                                 $"将分辨率调高至 [{targetRes}] 预估需要约 {targetSingleMb} MB/s 吞吐量，" +
                                 $"已超出 USB 2.0 的物理传输上限（~35 MB/s）。\n\n" +
                                 $"强行设置可能会导致本通道或其它通道断开，并提示 Device Occupied。\n\n" +
                                 $"建议：请将该摄像头插至蓝色的 USB 3.0 接口，或选择 1080p 及以下分辨率。\n\n" +
                                 $"您确定要继续应用 [{targetRes}] 吗？";
                return true;
            }

            // 场景 2: 多路高分辨率并发导致总吞吐量过高 (> 75 MB/s)
            double projectedTotalMb = currentTotalMbPerSec + targetSingleMb - 15.0;
            if (targetW >= 3840 && activeCameraCount >= 3 && projectedTotalMb > 75.0)
            {
                warningTitle = "⚠️ 多路 USB 总线负载预警";
                warningMessage = $"当前已有 {activeCameraCount} 路摄像头在播。\n\n" +
                                 $"将本通道分辨率调高至 [{targetRes}] 后，预估全系统 USB 吞吐量将达到约 {projectedTotalMb:F1} MB/s。\n\n" +
                                 $"若多台摄像头插在同一个物理 USB 集线器/扩展坞上，可能会因总线争用导致个别通道显示 Device Occupied。\n\n" +
                                 $"您确定要继续调整吗？";
                return true;
            }

            return false;
        }
    }
}
