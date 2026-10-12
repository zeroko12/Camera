using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace camera.Infrastructure
{
    public class CameraDeviceInfo
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string DevicePath { get; set; }
        public List<string> SupportedResolutions { get; set; } = new();
    }

    public static class CameraDeviceEnumerator
    {
        private static readonly Guid CLSID_SystemDeviceEnum = new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
        private static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");

        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum
        {
            [PreserveSig]
            int CreateClassEnumerator([In, MarshalAs(UnmanagedType.LPStruct)] Guid pclsidDeviceClass, out IEnumMoniker ppEnumMoniker, [In] int dwFlags);
        }

        public static List<CameraDeviceInfo> EnumerateCameras()
        {
            var cameras = new List<CameraDeviceInfo>();
            object enumObj = null;
            IEnumMoniker enumMoniker = null;
            IMoniker[] monikers = new IMoniker[1];

            try
            {
                enumObj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum));
                var devEnum = (ICreateDevEnum)enumObj;

                int hr = devEnum.CreateClassEnumerator(CLSID_VideoInputDeviceCategory, out enumMoniker, 0);
                if (hr != 0 || enumMoniker == null)
                {
                    return cameras;
                }

                int index = 0;
                IntPtr fetched = IntPtr.Zero;
                while (enumMoniker.Next(1, monikers, fetched) == 0)
                {
                    IMoniker moniker = monikers[0];
                    try
                    {
                        string name = GetFriendlyName(moniker);
                        string path = GetDevicePath(moniker);

                        var resList = GetSupportedResolutions(moniker);
                        cameras.Add(new CameraDeviceInfo
                        {
                            Index = index,
                            Name = name ?? $"USB Camera #{index}",
                            DevicePath = path ?? name ?? index.ToString(),
                            SupportedResolutions = resList
                        });
                        index++;
                    }
                    finally
                    {
                        if (moniker != null)
                        {
                            Marshal.ReleaseComObject(moniker);
                            monikers[0] = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("枚举摄像头设备时发生异常", ex);
            }
            finally
            {
                if (enumMoniker != null) Marshal.ReleaseComObject(enumMoniker);
                if (enumObj != null) Marshal.ReleaseComObject(enumObj);
            }

            return cameras;
        }

        private static string GetFriendlyName(IMoniker moniker)
        {
            object bagObj = null;
            try
            {
                Guid bagId = typeof(IPropertyBag).GUID;
                moniker.BindToStorage(null, null, ref bagId, out bagObj);
                var bag = (IPropertyBag)bagObj;
                object val;
                int hr = bag.Read("FriendlyName", out val, IntPtr.Zero);
                if (hr == 0 && val is string name)
                {
                    return name;
                }
            }
            catch { }
            finally
            {
                if (bagObj != null) Marshal.ReleaseComObject(bagObj);
            }
            return null;
        }

        private static string GetDevicePath(IMoniker moniker)
        {
            try
            {
                moniker.GetDisplayName(null, null, out string displayName);
                return displayName;
            }
            catch { }
            return null;
        }

        private static readonly Guid FORMAT_VideoInfo = new Guid("05589F80-C356-11CE-BF01-00AA0055595A");
        private static readonly Guid FORMAT_VideoInfo2 = new Guid("F72A76F0-EB0A-11D0-ACE4-0000C0CC16BA");

        private static List<string> GetSupportedResolutions(IMoniker moniker)
        {
            var result = new List<string>();
            if (moniker == null) return result;

            object filterObj = null;
            IEnumPins enumPins = null;
            try
            {
                Guid filterId = typeof(IBaseFilter).GUID;
                moniker.BindToObject(null, null, ref filterId, out filterObj);
                if (filterObj is IBaseFilter filter)
                {
                    if (filter.EnumPins(out enumPins) == 0 && enumPins != null)
                    {
                        var resDict = new Dictionary<string, (int W, int H)>();
                        while (enumPins.Next(1, out IPin pin, IntPtr.Zero) == 0 && pin != null)
                        {
                            try
                            {
                                pin.QueryDirection(out int dir);
                                if (dir == 1 && pin is IAMStreamConfig streamConfig) // PINDIR_OUTPUT = 1
                                {
                                    if (streamConfig.GetNumberOfCapabilities(out int count, out int size) == 0 && size > 0 && count > 0)
                                    {
                                        IntPtr pSCC = Marshal.AllocCoTaskMem(size);
                                        try
                                        {
                                            for (int i = 0; i < count; i++)
                                            {
                                                if (streamConfig.GetStreamCaps(i, out IntPtr ppmt, pSCC) == 0 && ppmt != IntPtr.Zero)
                                                {
                                                    try
                                                    {
                                                        var mt = Marshal.PtrToStructure<AM_MEDIA_TYPE>(ppmt);
                                                        if (mt.pbFormat != IntPtr.Zero)
                                                        {
                                                            int w = 0, h = 0;
                                                            if (mt.formattype == FORMAT_VideoInfo)
                                                            {
                                                                w = Marshal.ReadInt32(mt.pbFormat, 48 + 4);
                                                                h = Marshal.ReadInt32(mt.pbFormat, 48 + 8);
                                                            }
                                                            else if (mt.formattype == FORMAT_VideoInfo2)
                                                            {
                                                                w = Marshal.ReadInt32(mt.pbFormat, 72 + 4);
                                                                h = Marshal.ReadInt32(mt.pbFormat, 72 + 8);
                                                            }

                                                            if (w > 0 && h > 0)
                                                            {
                                                                string resStr = $"{w}x{h}";
                                                                if (!resDict.ContainsKey(resStr))
                                                                    resDict[resStr] = (w, h);
                                                            }
                                                        }
                                                    }
                                                    finally
                                                    {
                                                        FreeMediaType(ppmt);
                                                    }
                                                }
                                            }
                                        }
                                        finally
                                        {
                                            Marshal.FreeCoTaskMem(pSCC);
                                        }
                                    }
                                    break; // 已获取到输出 Pin 的格式能力
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(pin);
                            }
                        }

                        // 按像素面积降序排列（4K -> 2K -> 1080p -> 720p -> 480p）
                        var sorted = new List<(int W, int H)>(resDict.Values);
                        sorted.Sort((a, b) => (b.W * b.H).CompareTo(a.W * a.H));
                        foreach (var item in sorted)
                        {
                            result.Add($"{item.W}x{item.H}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[DirectShow] 获取硬件原生分辨率能力失败，将使用通用分辨率保底: {ex.Message}");
            }
            finally
            {
                if (enumPins != null) Marshal.ReleaseComObject(enumPins);
                if (filterObj != null) Marshal.ReleaseComObject(filterObj);
            }

            return result;
        }

        private static void FreeMediaType(IntPtr pmt)
        {
            if (pmt == IntPtr.Zero) return;
            try
            {
                var mt = Marshal.PtrToStructure<AM_MEDIA_TYPE>(pmt);
                if (mt.cbFormat != 0 && mt.pbFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mt.pbFormat);
                if (mt.pUnk != IntPtr.Zero) Marshal.Release(mt.pUnk);
                Marshal.FreeCoTaskMem(pmt);
            }
            catch { }
        }

        [ComImport, Guid("56A86899-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IBaseFilter
        {
            [PreserveSig] int GetClassID(out Guid pClassID);
            [PreserveSig] int Stop();
            [PreserveSig] int Pause();
            [PreserveSig] int Run(long tStart);
            [PreserveSig] int GetState(int dwMilliSecsTimeout, out int filtState);
            [PreserveSig] int SetSyncSource(IntPtr pClock);
            [PreserveSig] int GetSyncSource(out IntPtr pClock);
            [PreserveSig] int EnumPins(out IEnumPins ppEnum);
            [PreserveSig] int FindPin([MarshalAs(UnmanagedType.LPWStr)] string Id, out IntPtr ppPin);
            [PreserveSig] int QueryFilterInfo(IntPtr pInfo);
            [PreserveSig] int JoinFilterGraph(IntPtr pGraph, [MarshalAs(UnmanagedType.LPWStr)] string pName);
            [PreserveSig] int QueryVendorInfo([MarshalAs(UnmanagedType.LPWStr)] out string pVendorInfo);
        }

        [ComImport, Guid("56A86892-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumPins
        {
            [PreserveSig] int Next(int cPins, out IPin ppPins, IntPtr pcFetched);
            [PreserveSig] int Skip(int cPins);
            [PreserveSig] int Reset();
            [PreserveSig] int Clone(out IEnumPins ppEnum);
        }

        [ComImport, Guid("56A86891-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPin
        {
            [PreserveSig] int Connect(IntPtr pReceivePin, IntPtr pmt);
            [PreserveSig] int ReceiveConnection(IntPtr pConnector, IntPtr pmt);
            [PreserveSig] int Disconnect();
            [PreserveSig] int ConnectedTo(out IntPtr pPin);
            [PreserveSig] int ConnectionMediaType(IntPtr pmt);
            [PreserveSig] int QueryPinInfo(IntPtr pInfo);
            [PreserveSig] int QueryDirection(out int pPinDir);
        }

        [ComImport, Guid("C6E13340-30AC-11D0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAMStreamConfig
        {
            [PreserveSig] int SetFormat(IntPtr pmt);
            [PreserveSig] int GetFormat(out IntPtr ppmt);
            [PreserveSig] int GetNumberOfCapabilities(out int piCount, out int piSize);
            [PreserveSig] int GetStreamCaps(int iIndex, out IntPtr ppmt, IntPtr pSCC);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AM_MEDIA_TYPE
        {
            public Guid majortype;
            public Guid subtype;
            public bool bFixedSizeSamples;
            public bool bTemporalCompression;
            public uint lSampleSize;
            public Guid formattype;
            public IntPtr pUnk;
            public uint cbFormat;
            public IntPtr pbFormat;
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag
        {
            [PreserveSig]
            int Read([MarshalAs(UnmanagedType.LPWStr)] string pszPropName, out object pVar, IntPtr pErrorLog);
            [PreserveSig]
            int Write([MarshalAs(UnmanagedType.LPWStr)] string pszPropName, ref object pVar);
        }
    }
}
