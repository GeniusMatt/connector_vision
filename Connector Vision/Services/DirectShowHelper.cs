using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DirectShowLib;

namespace Connector_Vision.Services
{
    public static class DirectShowHelper
    {
        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int OleCreatePropertyFrame(
            IntPtr hwndOwner,
            int x,
            int y,
            [MarshalAs(UnmanagedType.LPWStr)] string caption,
            int cObjects,
            [MarshalAs(UnmanagedType.Interface)] ref object ppUnk,
            int cPages,
            IntPtr pPageClsID,
            int lcid,
            int dwReserved,
            IntPtr pvReserved);

        public static List<string> GetVideoDeviceNames()
        {
            var names = new List<string>();
            var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            foreach (var d in devices)
            {
                names.Add(d.Name);
            }
            return names;
        }

        public static void ShowPropertyPage(IntPtr ownerHwnd, int deviceIndex)
        {
            var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            if (deviceIndex < 0 || deviceIndex >= devices.Length)
                return;

            var device = devices[deviceIndex];
            object source = null;

            try
            {
                Guid iid = typeof(IBaseFilter).GUID;
                device.Mon.BindToObject(null, null, ref iid, out source);

                var psp = source as ISpecifyPropertyPages;
                if (psp != null)
                {
                    DsCAUUID caGUID;
                    int hr = psp.GetPages(out caGUID);
                    if (hr == 0 && caGUID.cElems > 0)
                    {
                        OleCreatePropertyFrame(
                            ownerHwnd, 0, 0,
                            device.Name,
                            1, ref source,
                            caGUID.cElems, caGUID.pElems,
                            0, 0, IntPtr.Zero);

                        Marshal.FreeCoTaskMem(caGUID.pElems);
                    }
                }
            }
            finally
            {
                if (source != null)
                    Marshal.ReleaseComObject(source);
            }
        }
    }
}
