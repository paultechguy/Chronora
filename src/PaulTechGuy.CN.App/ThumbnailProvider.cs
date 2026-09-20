// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaulTechGuy.CN.Domain;
using WinRT;

namespace PaulTechGuy.CN.App;

/// <summary>
/// The picture Explorer would show, in two tiers.
///
/// The tiers come from what a spike measured on real files:
///
///   icon, cached per extension    ~2.5 ms once, then nothing
///   thumbnail already cached      4 - 15 ms
///   thumbnail to be generated     13 - 203 ms  (the 203 was a phone video)
///
/// A frame is 16 ms and a screenful of twenty uncached rows measured 1.26 seconds, so the
/// Shell is only ever called off the UI thread, with an icon standing in until the real
/// thumbnail arrives.
///
/// PIXELS are cached, never ImageSource objects, and that is the load-bearing part rather
/// than an optimisation. Caching the ImageSource meant handing ONE instance to many Image
/// controls at once; a SoftwareBitmapSource owns a disposable composition surface, so
/// recycling a row released a surface other rows were still showing. That took the process
/// down with STATUS_STOWED_EXCEPTION - a COM failure with no managed stack - on the second
/// drop, which is the first drop that recycles containers.
///
/// Every row now gets its own WriteableBitmap over shared bytes. Building one is a 37 KB
/// memcpy needing no await, which also lets the immediate tier be synchronous without the
/// UI-thread deadlock an earlier version had.
///
/// Windows picks the video frame, which is the part worth having: its media handler
/// already skips the blank opening that makes a home-made "grab frame zero" useless.
///
/// A cloud-only file is never hydrated to draw a picture.
/// </summary>
internal static class ThumbnailProvider
{
    /// <summary>
    /// 96 is one of the sizes the Shell thumbnail cache already keeps, so asking for it
    /// avoids a resize that gains nothing.
    /// </summary>
    private const int RequestedSize = 96;

    /// <summary>
    /// How many Shell calls may be in flight. Scrolling fast would otherwise queue one per
    /// row, and the ones still running would all be for rows that had left the screen.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(3, 3);

    /// <summary>Raw pixels per extension. Safe to share, because bytes have no owner.</summary>
    private static readonly ConcurrentDictionary<string, Pixels?> IconPixels =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raw pixels per file. Bounded, or 50,000 files would be held forever; cleared
    /// wholesale rather than evicted one at a time, since the cost of being wrong is one
    /// re-fetch in front of the Shell's own cache.
    /// </summary>
    private const int MaxCachedThumbnails = 512;

    private static readonly ConcurrentDictionary<string, Pixels> ThumbnailPixels =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A fresh image of this file type's icon, when the pixels are already known.
    ///
    /// Synchronous and safe during layout: after the first file of an extension it is a
    /// dictionary lookup plus a small memcpy. No await, so no UI-thread deadlock, and a
    /// new bitmap every time, so no two rows share one.
    /// </summary>
    public static bool TryGetIcon(ScannedFile file, out ImageSource? icon)
    {
        ArgumentNullException.ThrowIfNull(file);

        icon = null;

        if (!IconPixels.TryGetValue(ExtensionOf(file), out Pixels? pixels))
        {
            return false;
        }

        icon = pixels is { } bits ? ToImage(bits) : null;
        return true;
    }

    /// <summary>A fresh image of this file's thumbnail, when one has already been fetched.</summary>
    public static bool TryGetThumbnail(string path, out ImageSource? image)
    {
        image = ThumbnailPixels.TryGetValue(path, out Pixels bits) ? ToImage(bits) : null;
        return image is not null;
    }

    /// <summary>
    /// Fetches this extension's icon if it is not already known. About 2.5 ms, paid once
    /// per extension for the whole session.
    /// </summary>
    public static async Task<ImageSource?> EnsureIconAsync(ScannedFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        string extension = ExtensionOf(file);

        if (!IconPixels.TryGetValue(extension, out Pixels? cached))
        {
            // Built from THIS file, then kept under its extension: the Shell needs a real
            // path to resolve an association, and every file of that type will match it.
            cached = await Task.Run(
                    () => ReadBitmapFrom(Extract(file.FullPath, SIIGBF.IconOnly)), cancellationToken)
                .ConfigureAwait(true);

            IconPixels[extension] = cached;
        }

        return cached is { } bits ? ToImage(bits) : null;
    }

    /// <summary>
    /// The real thumbnail, fetched off the UI thread.
    ///
    /// Null means there is nothing better than the icon already showing, which is the
    /// normal answer for a text file and for a cloud-only file with nothing cached.
    /// </summary>
    public static async Task<ImageSource?> LoadAsync(ScannedFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.IsDirectory)
        {
            return null;
        }

        string path = file.FullPath;

        if (ThumbnailPixels.TryGetValue(path, out Pixels done))
        {
            return ToImage(done);
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
            pixels = await Task.Run(() => ReadBitmapFrom(Extract(path, flags)), cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            _ = Gate.Release();
        }

        if (pixels is not { } bits || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (ThumbnailPixels.Count >= MaxCachedThumbnails)
        {
            ThumbnailPixels.Clear();
        }

        ThumbnailPixels[path] = bits;
        return ToImage(bits);
    }

    /// <summary>Dropped when the list is replaced, so a stale path cannot show a stale picture.</summary>
    public static void Forget() => ThumbnailPixels.Clear();

    private static string ExtensionOf(ScannedFile file) =>
        file.IsDirectory ? "<folder>" : Path.GetExtension(file.FullPath);

    /// <summary>
    /// A new bitmap over the cached bytes, for one Image and no other.
    ///
    /// WriteableBitmap rather than SoftwareBitmapSource: it is created synchronously, owns
    /// no disposable composition surface, and writing to it needs no await. All three
    /// matter here - the async version deadlocked the UI thread and the shared version
    /// killed the process when a row was recycled.
    ///
    /// The buffer is reached through IBufferByteAccess because IBuffer.AsStream lived in
    /// System.Runtime.WindowsRuntime, which .NET 5 removed.
    /// </summary>
    private static WriteableBitmap ToImage(Pixels bits)
    {
        var bitmap = new WriteableBitmap(bits.Width, bits.Height);

        IBufferByteAccess access = bitmap.PixelBuffer.As<IBufferByteAccess>();
        access.Buffer(out nint destination);

        Marshal.Copy(bits.Data, 0, destination, bits.Data.Length);
        bitmap.Invalidate();

        return bitmap;
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

    /// <summary>Direct access to a WinRT buffer's bytes, which has no managed equivalent.</summary>
    [ComImport]
    [Guid("905a0fef-bc53-11df-8c49-001e4fc686da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBufferByteAccess
    {
        void Buffer(out nint buffer);
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
