#!/usr/bin/env dotnet-script
// gen_icon.csx — generates proper multi-size ICO from icon.png
#r "nuget: System.Drawing.Common, 8.0.0"

using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

string src = @"G:\Lanweilig\Heimlich\Karikatur\MangaView\icon.png";
string dst = @"G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\AppIcon.ico";

var img = Image.FromFile(src);
Console.WriteLine($"Source: {img.Width}x{img.Height}");

int[] sizes = { 256, 64, 48, 32, 16 };
var bmps = new Bitmap[sizes.Length];
for (int i = 0; i < sizes.Length; i++) {
    double scale = Math.Min(sizes[i] / Math.Max(img.Width, img.Height), 1.0);
    int nw = Math.Max(1, (int)Math.Round(img.Width * scale));
    int nh = Math.Max(1, (int)Math.Round(img.Height * scale));
    bmps[i] = new Bitmap(img, new Size(nw, nh));
    Console.WriteLine($"Frame {i}: {nw}x{nh}");
}

Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

// Build ICO file manually
using var fs = new FileStream(dst, FileMode.Create);
using var bw = new BinaryWriter(fs);

// Header
bw.Write((short)0);   // reserved
bw.Write((short)1);   // ICON type
bw.Write((ushort)bmps.Length);  // count

// First pass: collect entries and pixel data
long dirStart = 6 + bmps.Length * 16;
long curOff = dirStart;

struct Entry { byte w, h, cc, rc; ushort planes, bpp; uint sz, off; }

var entries = new Entry[bmps.Length];

for (int i = 0; i < bmps.Length; i++) {
    var bmp = bmps[i];
    int w = bmp.Width, h = bmp.Height;

    // Lock bits
    var rect = new Rectangle(0, 0, w, h);
    var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    int stride = data.Stride;
    int totalPxSize = stride * h;
    var rowBuf = new byte[totalPxSize];
    Marshal.Copy(data.Scan0, rowBuf, 0, totalPxSize);
    bmp.UnlockBits(data);

    // Pack as BGRA bottom-up
    int lineStride = (w * 4 + 3) & ~3;
    int pxDataSize = lineStride * h;
    var pxData = new byte[pxDataSize];
    for (int y = h - 1; y >= 0; y--) {
        int srcOff = y * stride;
        int dstOff = (h - 1 - y) * lineStride;
        for (int x = 0; x < w; x++) {
            int sIdx = srcOff + x * 4;
            int dIdx = dstOff + x * 4;
            pxData[dIdx + 0] = rowBuf[sIdx + 2]; // B
            pxData[dIdx + 1] = rowBuf[sIdx + 1]; // G
            pxData[dIdx + 2] = rowBuf[sIdx + 0]; // R
            pxData[dIdx + 3] = rowBuf[sIdx + 3]; // A
        }
        // Padding at end of row
        int pad = lineStride - w * 4;
        for (int p = 0; p < pad; p++) {
            pxData[dstOff + w * 4 + p] = 0;
        }
    }

    // BITMAPINFOHEADER
    int dibHeaderSz = 40;
    int entryTotalSz = dibHeaderSz + pxDataSize;

    entries[i] = new Entry {
        w = (byte)(w >= 256 ? 0 : w),
        h = (byte)(h >= 256 ? 0 : h),
        cc = 0, rc = 0,
        planes = 1,
        bpp = 32,
        sz = (uint)entryTotalSz,
        off = (uint)curOff
    };

    // Write DIB header immediately
    bw.Write((uint)dibHeaderSz);     // biSize
    bw.Write(w);                     // biWidth
    bw.Write(h * 2);                 // biHeight doubled
    bw.Write((ushort)1);             // biPlanes
    bw.Write((ushort)32);            // biBitCount
    bw.Write(0);                     // BI_RGB
    bw.Write(pxDataSize);            // biSizeImage
    bw.Write(0); bw.Write(0);        // meters
    bw.Write(0); bw.Write(0);        // colors

    // Write pixel data
    bw.Write(pxData);

    curOff += entryTotalSz;
    bmp.Dispose();
}

// Now seek back and write directory entries
fs.Seek(0, SeekOrigin.Begin);
bw.Write((short)0); bw.Write((short)1); bw.Write((ushort)bmps.Length);

foreach (var e in entries) {
    bw.Write(e.w); bw.Write(e.h);
    bw.Write((byte)e.cc); bw.Write((byte)e.rc);
    bw.Write(e.planes); bw.Write(e.bpp);
    bw.Write(e.sz); bw.Write(e.off);
}

var finalSize = fs.Length;
Console.WriteLine($"Wrote {dst} ({finalSize} bytes)");
