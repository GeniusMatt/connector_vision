using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace Connector_Vision.Helpers
{
    public static class BitmapHelper
    {
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        public static void UpdateWriteableBitmap(WriteableBitmap wb, Mat mat)
        {
            if (wb == null || mat == null || mat.Empty())
                return;

            if (mat.Channels() == 3
                && mat.Width == wb.PixelWidth
                && mat.Height == wb.PixelHeight
                && mat.IsContinuous())
            {
                int matStride = mat.Width * 3;
                int wbStride = wb.BackBufferStride;

                wb.Lock();
                try
                {
                    if (matStride == wbStride)
                    {
                        CopyMemory(wb.BackBuffer, mat.Data, (uint)(matStride * mat.Height));
                    }
                    else
                    {
                        int copyBytes = Math.Min(matStride, wbStride);
                        for (int y = 0; y < mat.Height; y++)
                        {
                            IntPtr src = mat.Data + y * matStride;
                            IntPtr dst = wb.BackBuffer + y * wbStride;
                            CopyMemory(dst, src, (uint)copyBytes);
                        }
                    }
                    wb.AddDirtyRect(new Int32Rect(0, 0, wb.PixelWidth, wb.PixelHeight));
                }
                finally
                {
                    wb.Unlock();
                }
                return;
            }

            Mat source = mat;
            bool needsDispose = false;

            if (mat.Channels() == 1)
            {
                source = new Mat();
                Cv2.CvtColor(mat, source, ColorConversionCodes.GRAY2BGR);
                needsDispose = true;
            }
            else if (mat.Channels() == 4)
            {
                source = new Mat();
                Cv2.CvtColor(mat, source, ColorConversionCodes.BGRA2BGR);
                needsDispose = true;
            }

            if (source.Width != wb.PixelWidth || source.Height != wb.PixelHeight)
            {
                var resized = new Mat();
                Cv2.Resize(source, resized, new OpenCvSharp.Size(wb.PixelWidth, wb.PixelHeight));
                if (needsDispose) source.Dispose();
                source = resized;
                needsDispose = true;
            }

            wb.Lock();
            try
            {
                int stride = source.Width * 3;
                CopyMemory(wb.BackBuffer, source.Data, (uint)(stride * source.Height));
                wb.AddDirtyRect(new Int32Rect(0, 0, wb.PixelWidth, wb.PixelHeight));
            }
            finally
            {
                wb.Unlock();
                if (needsDispose) source.Dispose();
            }
        }

        public static WriteableBitmap CreateWriteableBitmap(int width, int height)
        {
            return new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
        }

        public static BitmapSource MatToBitmapSource(Mat mat)
        {
            if (mat == null || mat.Empty())
                return null;

            Mat source = mat;
            bool needsDispose = false;

            if (mat.Channels() == 1)
            {
                source = new Mat();
                Cv2.CvtColor(mat, source, ColorConversionCodes.GRAY2BGR);
                needsDispose = true;
            }
            else if (mat.Channels() == 4)
            {
                source = new Mat();
                Cv2.CvtColor(mat, source, ColorConversionCodes.BGRA2BGR);
                needsDispose = true;
            }

            if (!source.IsContinuous())
            {
                var clone = source.Clone();
                if (needsDispose) source.Dispose();
                source = clone;
                needsDispose = true;
            }

            int stride = source.Width * 3;
            var bmp = BitmapSource.Create(
                source.Width, source.Height,
                96, 96,
                PixelFormats.Bgr24,
                null,
                source.Data,
                stride * source.Height,
                stride);
            bmp.Freeze();

            if (needsDispose) source.Dispose();
            return bmp;
        }
    }
}
