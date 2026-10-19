using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace DROPME.Utils
{
    /// <summary>
    /// Получает системное Windows-превью файла через Shell thumbnail provider.
    /// Поддерживаемые форматы определяются установленными в Windows image/video codecs.
    /// </summary>
    internal static class ShellThumbnailService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [Flags]
        private enum ShellImageFlags
        {
            ResizeToFit = 0x00,
            BiggerSizeOk = 0x01,
            MemoryOnly = 0x02,
            IconOnly = 0x04,
            ThumbnailOnly = 0x08,
            InCacheOnly = 0x10
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        private interface IShellItemImageFactory
        {
            void GetImage(
                NativeSize size,
                ShellImageFlags flags,
                out IntPtr bitmapHandle);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr bindContext,
            [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory shellItem);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr handle);

        public static byte[]? TryCreateJpeg(string path, int maxSize)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            int normalizedSize = Math.Clamp(maxSize, 64, 512);
            IntPtr bitmapHandle = IntPtr.Zero;
            IShellItemImageFactory? shellItem = null;
            try
            {
                Guid interfaceId = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, interfaceId, out shellItem);
                shellItem.GetImage(
                    new NativeSize { Width = normalizedSize, Height = normalizedSize },
                    ShellImageFlags.ThumbnailOnly | ShellImageFlags.BiggerSizeOk,
                    out bitmapHandle);
                if (bitmapHandle == IntPtr.Zero)
                {
                    return null;
                }

                using Bitmap source = Image.FromHbitmap(bitmapHandle);
                using MemoryStream output = new MemoryStream();
                source.Save(output, ImageFormat.Jpeg);
                return output.ToArray();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (bitmapHandle != IntPtr.Zero)
                {
                    DeleteObject(bitmapHandle);
                }
                if (shellItem != null && Marshal.IsComObject(shellItem))
                {
                    Marshal.FinalReleaseComObject(shellItem);
                }
            }
        }
    }
}
