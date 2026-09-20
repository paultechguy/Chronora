// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaulTechGuy.CN.Domain;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace PaulTechGuy.CN.App;

/// <summary>
/// The picture Explorer would show for a file.
///
/// Deliberately the Shell's own thumbnail rather than anything this app decodes itself.
/// One call covers photos, HEIC, raw where a codec is installed, and video - and for video
/// it is Windows that picks the frame, which is the part worth having: its media handler
/// already avoids the blank opening frame that makes a home-made "grab frame zero" useless.
/// It also reads the system thumbnail cache, so the second look at a file is instant and
/// nothing is decoded twice.
///
/// The one rule that is not cosmetic: a cloud-only file is never hydrated to draw a
/// picture. The app refuses to pull gigabytes out of OneDrive to read a date; doing it for
/// a 64-pixel image would be worse.
/// </summary>
internal static class ThumbnailProvider
{
    /// <summary>
    /// Asked of the Shell. Larger than the 64 it is displayed at so it stays crisp when
    /// Windows is scaled to 125% or 150%, and 96 is one of the sizes the thumbnail cache
    /// already keeps - asking for an off-size forces a resize that gains nothing.
    /// </summary>
    private const int RequestedSize = 96;

    /// <summary>
    /// Loads a thumbnail, or the file-type icon when there is no thumbnail to be had.
    ///
    /// Runs the Shell call on a background thread because extracting a video frame can
    /// take a moment, and returns the pixels rather than an image: a WriteableBitmap has
    /// to be created on the UI thread.
    /// </summary>
    public static async Task<ImageSource?> LoadAsync(ScannedFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        // A folder has no thumbnail worth the round trip, and asking for one on a drive
        // root can be slow enough to notice.
        if (file.IsDirectory)
        {
            return null;
        }

        bool cloudOnly = file.Traits.HasFlag(FileTraits.CloudDehydrated);
        string path = file.FullPath;

        Pixels? pixels = await Task.Run(() => Extract(path, cloudOnly), cancellationToken).ConfigureAwait(true);

        if (pixels is not { } bits || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        // CryptographicBuffer rather than IBuffer.AsStream(): that extension lived in
        // System.Runtime.WindowsRuntime, which .NET 5 removed. This is the route that
        // still exists, and it keeps the pixels in one copy rather than streaming them.
        IBuffer buffer = CryptographicBuffer.CreateFromByteArray(bits.Data);

        // The Shell hands back premultiplied BGRA, which is exactly what the XAML
        // compositor wants - so there is no conversion here and none is needed.
        using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            buffer, BitmapPixelFormat.Bgra8, bits.Width, bits.Height, BitmapAlphaMode.Premultiplied);

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);

        return source;
    }

    private readonly record struct Pixels(int Width, int Height, byte[] Data);

    private static Pixels? Extract(string path, bool cloudOnly)
    {
        // For a cloud-only file, only a thumbnail Windows has already cached is acceptable.
        // Without this flag the Shell would happily download the file to generate one.
        SIIGBF flags = cloudOnly ? SIIGBF.InCacheOnly : SIIGBF.ResizeToFit;

        nint bitmap = TryGetImage(path, flags);

        // A cloud file with nothing cached, or anything the Shell could not render, still
        // gets its file-type icon. Filling the slot means the pane does not reflow as you
        // move between a photo and a text file.
        if (bitmap == 0)
        {
            bitmap = TryGetImage(path, SIIGBF.IconOnly);
        }

        if (bitmap == 0)
        {
            return null;
        }

        try
        {
            return ReadBitmap(bitmap);
        }
        finally
        {
            _ = DeleteObject(bitmap);
        }
    }

    private static nint TryGetImage(string path, SIIGBF flags)
    {
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;

            if (SHCreateItemFromParsingName(path, 0, ref iid, out IShellItemImageFactory? factory) != 0
                || factory is null)
            {
                return 0;
            }

            try
            {
                factory.GetImage(new SIZE { cx = RequestedSize, cy = RequestedSize }, flags, out nint bitmap);
                return bitmap;
            }
            finally
            {
                _ = Marshal.ReleaseComObject(factory);
            }
        }
        catch (COMException)
        {
            // No thumbnail handler, nothing cached, or a file the Shell will not touch.
            // All of them mean the same thing here: try the next fallback.
            return 0;
        }
        catch (ArgumentException)
        {
            // A path the Shell cannot parse, such as one already deleted.
            return 0;
        }
    }

    /// <summary>
    /// Copies an HBITMAP into plain BGRA bytes.
    ///
    /// Top-down (a negative height) so the rows arrive in the order a WriteableBitmap
    /// expects; a DIB is bottom-up by default and the picture would come out upside down.
    /// </summary>
    private static Pixels? ReadBitmap(nint bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out BITMAP info) == 0 || info.bmWidth <= 0 || info.bmHeight <= 0)
        {
            return null;
        }

        int width = info.bmWidth;
        int height = info.bmHeight;

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        byte[] data = new byte[width * height * 4];

        nint screen = GetDC(0);

        try
        {
            GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);

            try
            {
                int copied = GetDIBits(screen, bitmap, 0, (uint)height, pinned.AddrOfPinnedObject(), ref header, 0);
                return copied == 0 ? null : new Pixels(width, height, data);
            }
            finally
            {
                pinned.Free();
            }
        }
        finally
        {
            _ = ReleaseDC(0, screen);
        }
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,

        /// <summary>Never generate one: only return what the cache already holds.</summary>
        InCacheOnly = 0x10,

        /// <summary>The file-type icon rather than a preview of the contents.</summary>
        IconOnly = 0x04,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, SIIGBF flags, out nint phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        nint bindContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint handle, int size, out BITMAP info);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        nint dc, nint bitmap, uint start, uint lines, nint bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
}
