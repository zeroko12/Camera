using System;
using System.Collections.Generic;

namespace camera.Infrastructure
{
    /// <summary>
    /// 全局 USB 摄像头设备互斥锁与占用管理器（参考 iSpy 架构设计）
    /// 防止多个通道或多线程并发打开同一个物理硬件/路径导致驱动锁死 (Device Occupied)
    /// </summary>
    public static class DeviceLockManager
    {
        private static readonly HashSet<string> _activeDevicePaths = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<int> _activeIndices = new();
        private static readonly object _lock = new();

        /// <summary>
        /// 尝试占用设备。若设备已被占用，返回 false
        /// </summary>
        public static bool TryAcquireDevice(string devicePath, int index)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(devicePath) && _activeDevicePaths.Contains(devicePath))
                {
                    Logger.LogWarning($"[DeviceLockManager] 物理设备已被其它通道锁定 (DevicePath: {devicePath})");
                    return false;
                }

                if (index >= 0 && _activeIndices.Contains(index))
                {
                    Logger.LogWarning($"[DeviceLockManager] 物理索引 #{index} 已被其它通道锁定");
                    return false;
                }

                if (!string.IsNullOrEmpty(devicePath))
                {
                    _activeDevicePaths.Add(devicePath);
                }

                if (index >= 0)
                {
                    _activeIndices.Add(index);
                }

                return true;
            }
        }

        /// <summary>
        /// 释放设备占用锁
        /// </summary>
        public static void ReleaseDevice(string devicePath, int index)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(devicePath))
                {
                    _activeDevicePaths.Remove(devicePath);
                }

                if (index >= 0)
                {
                    _activeIndices.Remove(index);
                }
            }
        }

        /// <summary>
        /// 强制清空所有占用状态（仅在应用退出或全重置时调用）
        /// </summary>
        public static void ClearAll()
        {
            lock (_lock)
            {
                _activeDevicePaths.Clear();
                _activeIndices.Clear();
            }
        }
    }
}
