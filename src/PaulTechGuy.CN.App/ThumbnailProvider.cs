// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaulTechGuy.CN.Domain;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace PaulTechGuy.CN.App;

/// <summary>
/// The picture Explorer would show, in two tiers.
///
/// The tiers exist because of what a spike measured on real files rather than because they
/// looked tidy:
///
///   icon, cached per extension    ~2.5 ms once, then nothing
///   thumbnail already cached      4 - 15 ms
///   thumbnail to be generated     13 - 203 ms  (the 203 was a phone video)
///
/// A frame is 16 ms, so even a CACHED thumbnail is too slow to fetch on the UI thread, and
/// a screenful of twenty uncached rows measured 1.26 seconds. That is the whole argument
/// for doing it this way: an icon lands immediately from a dictionary, and the real
/// thumbnail replaces it when it arrives from a background queue.
///
/// Windows picks the video frame, which is the part worth having - its media handler
/// already skips the blank opening that makes a home-made "grab frame zero" useless.
///
/// A cloud-only file is never hydrated to draw a picture. The app refuses to pull
/// gigabytes out of OneDrive to read a date; doing it for a thumbnail would be worse.
/// </summary>
internal static class ThumbnailProvider
{
    /// <summary>
    /// 96 is one of the sizes the Shell thumbnail cache already keeps, so asking for it
    /// avoids a resize that gains nothing. Displayed smaller, which keeps it crisp when
    /// Windows is scaled.
    /// </summary>
    private const int RequestedSize = 96;

    /// <summary>
    /// How many Shell calls may be in flight. Scrolling fast through thousands of rows
    /// would otherwise queue a request per row, and the ones still running would all be for
    /// rows that left the screen long ago.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(3, 3);

    /// <summary>One icon per extension, which is what makes the immediate tier free.</summary>
    private static readonly ConcurrentDictionary<string, ImageSource?> IconsByExtension =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Thumbnails already produced, so scrolling back up does not pay for them twice.
    ///
    /// Bounded, because a 50,000 file run would otherwise hold 50,000 bitmaps. When it
    /// fills it is cleared rather than evicted one at a time: this is a convenience cache
    /// in front of the Shell's own, and the cost of being wrong is one re-fetch.
    /// </summary>
    private const int MaxCachedThumbnails = 512;

    private static readonly ConcurrentDictionary<string, ImageSource> ThumbnailsByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The icon for this kind of file, immediately.
    ///
    /// Synchronous on purpose: after the first file of a given extension this is a
    /// dictionary lookup, so a row can be filled during layout without waiting for
    /// anything. Returns null only for the very first call for an extension whose icon the
    /// Shell will not produce.
    /// </summary>
    public static ImageSource? IconFor(ScannedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        string extension = file.IsDirectory ? "<folder>" : Path.GetExtension(file.FullPath);

        if (IconsByExtension.TryGetValue(extension, out ImageSource? cached))
        {
            return cached;
        }

        // Built from THIS file, then kept under its extension. The Shell needs a real path
        // to resolve an association, and every other file of the same type will match it.
        ImageSource? icon = FromHandle(Extract(file.FullPath, SIIGBF.IconOnly));

        IconsByExtension[extension] = icon;
        return icon;
    }

    /// <summary>Whether a real thumbnail is already in hand, so no work needs scheduling.</summary>
    public static bool TryGetCached(string path, out ImageSource? image) =>
        ThumbnailsByPath.TryGetValue(path, out image);

    /// <summary>
    /// The real thumbnail, off the UI thread.
    ///
    /// Returns null when there is nothing better than the icon already showing, which is
    /// the normal outcome for a text file and for a cloud-only file with nothing cached.
    /// </summary>
    public static async Task<ImageSource?> LoadAsync(ScannedFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.IsDirectory)
        {
            return null;
        }

        string path = file.FullPath;

        if (ThumbnailsByPath.TryGetValue(path, out ImageSource? done))
        {
            return done;
        }

        // A cloud-only file may only offer what Windows has already cached. Without this
        // the Shell would download it to generate one.
        SIIGBF flags = file.Traits.HasFlag(FileTraits.CloudDehydrated)
            ? SIIGBF.InCacheOnly
            : SIIGBF.ThumbnailOnly;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(true);

        Pixels? pixels;

        try
        {
            pixels = await Task.Run(() => ReadBitmapFrom(Extract(path, flags)), cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _ = Gate.Release();
        }

        if (pixels is not { } bits || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        ImageSource image = await ToImageAsync(bits).ConfigureAwait(true);

        if (ThumbnailsByPath.Count >= MaxCachedThumbnails)
        {
            ThumbnailsByPath.Clear();
        }

        ThumbnailsByPath[path] = image;
        return image;
    }

    /// <summary>Dropped when the list is replaced, so a stale path cannot show a stale picture.</summary>
    public static void Forget() => ThumbnailsByPath.Clear();

    private static ImageSource? FromHandle(nint bitmap)
    {
        Pixels? pixels = ReadBitmapFrom(bitmap);

        return pixels is { } bits ? ToImageAsync(bits).GetAwaiter().GetResult() : null;
    }

    private static async Task<ImageSource> ToImageAsync(Pixels bits)
    {
        // CryptographicBuffer rather than IBuffer.AsStream(): that extension lived in
        // System.Runtime.WindowsRuntime, which .NET 5 removed.
        IBuffer buffer = CryptographicBuffer.CreateFromByteArray(bits.Data);

        // The Shell hands back premultiplied BGRA, which is what the compositor wants, so
        // nothing is converted here.
        using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            buffer, BitmapPixelFormat.Bgra8, bits.Width, bits.Height, BitmapAlphaMode.Premultiplied);

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);

        return source;
    }

    private readonly record struct Pixels(int Width, int Height, byte[] Data);

    private static nint Extract(string path, SIIGBF flags)
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
            // No handler, nothing cached, or a file the Shell will not touch. All of them
            // mean the same thing here: there is no picture, keep the icon.
            return 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    private static Pixels? ReadBitmapFrom(nint bitmap)
    {
        if (bitmap == 0)
        {
            return null;
        }

        try
        {
            if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out BITMAP info) == 0
                || info.bmWidth <= 0 || info.bmHeight <= 0)
            {
                return null;
            }

            int width = info.bmWidth;
            int height = info.bmHeight;

            // Top-down via a negative height: a DIB is bottom-up by default and the picture
            // would arrive upside down.
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
        finally
        {
            _ = DeleteObject(bitmap);
        }
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,

        /// <summary>The file-type icon rather than a preview of the contents.</summary>
        IconOnly = 0x04,

        /// <summary>A real thumbnail or nothing - never the icon dressed up as one.</summary>
        ThumbnailOnly = 0x08,

        /// <summary>Only what the cache already holds. Never generates, never downloads.</summary>
        InCacheOnly = 0x10,
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
