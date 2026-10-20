using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace camera.Streaming
{
    /// <summary>
    /// 局域网网络辅助类，用于探测本机在局域网中的有效 IPv4 地址。
    /// </summary>
    public static class NetworkHelper
    {
        /// <summary>
        /// 获取本机所有有效的局域网 IPv4 地址列表。
        /// 过滤掉回环、未启用以及常见的虚拟机/容器虚拟网卡（如 WSL、vEthernet、VMware、VirtualBox 等）。
        /// </summary>
        public static List<string> GetLocalIpAddresses()
        {
            var ipList = new List<string>();
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                               && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .OrderByDescending(nic => GetInterfacePriority(nic));

                foreach (var nic in interfaces)
                {
                    var props = nic.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ipStr = addr.Address.ToString();
                            // 排除回环地址和 APIPA 自动私有地址 (169.254.x.x)
                            if (!IPAddress.IsLoopback(addr.Address) && !ipStr.StartsWith("169.254."))
                            {
                                if (!ipList.Contains(ipStr))
                                {
                                    ipList.Add(ipStr);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[NetworkHelper] 获取本机局域网 IP 异常", ex);
            }

            if (ipList.Count == 0)
            {
                ipList.Add("127.0.0.1");
            }

            return ipList;
        }

        /// <summary>
        /// 获取最优先的主局域网 IP 地址（优先以太网和无线网卡，私有局域网网段）。
        /// </summary>
        public static string GetPrimaryLocalIpAddress()
        {
            var list = GetLocalIpAddresses();
            // 优先查找常见的家用/企业局域网 C类/A类/B类 IP：192.168.x.x > 10.x.x.x > 172.16-31.x.x
            var best = list.FirstOrDefault(ip => ip.StartsWith("192.168."))
                    ?? list.FirstOrDefault(ip => ip.StartsWith("10."))
                    ?? list.FirstOrDefault(ip => IsClassBPrivate(ip))
                    ?? list.FirstOrDefault();

            return best ?? "127.0.0.1";
        }

        private static bool IsClassBPrivate(string ip)
        {
            if (IPAddress.TryParse(ip, out var addr))
            {
                byte[] bytes = addr.GetAddressBytes();
                if (bytes.Length == 4 && bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                {
                    return true;
                }
            }
            return false;
        }

        private static int GetInterfacePriority(NetworkInterface nic)
        {
            int priority = 0;
            string name = (nic.Name + " " + nic.Description).ToLowerInvariant();

            // 扣分项：虚拟机或 Docker/WSL 虚拟网卡
            if (name.Contains("virtual") || name.Contains("vmware") || name.Contains("hyper-v")
                || name.Contains("vethernet") || name.Contains("wsl") || name.Contains("docker")
                || name.Contains("tailscale") || name.Contains("zerotier") || name.Contains("tap")
                || name.Contains("vpn"))
            {
                priority -= 100;
            }

            // 加分项：物理无线或物理以太网
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            {
                priority += 50;
            }
            else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                priority += 40;
            }

            return priority;
        }
    }
}
