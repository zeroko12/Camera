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

                        cameras.Add(new CameraDeviceInfo
                        {
                            Index = index,
                            Name = name ?? $"USB Camera #{index}",
                            DevicePath = path ?? name ?? index.ToString()
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
