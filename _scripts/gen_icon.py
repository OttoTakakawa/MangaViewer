"""Generate AppIcon.ico from icon.png (32-bit BGRA multi-frame)."""
import struct
import os
import sys

from PIL import Image as _PILImage

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SRC = os.path.join(ROOT, "icon.png")
DST = os.path.join(ROOT, "MangaReader.Native", "AppIcon.ico")
SIZES = [256, 64, 48, 32, 16]


def load_png(path):
    img = _PILImage.open(path).convert("RGBA")
    ow, oh = img.size
    return img, ow, oh


def build_frame(img, nw, nh):
    """Return (dib_header_bytes, pixel_bytes) for a single frame."""
    px_list = list(img.getdata())  # [(R,G,B,A), ...]
    stride = (nw * 4 + 3) & ~3

    px_parts = []
    for y in range(nh - 1, -1, -1):  # bottom-up (BMP convention)
        base = y * nw
        for x in range(nw):
            p = px_list[base + x]
            px_parts.extend([p[2], p[1], p[0], p[3]])  # B G R A
        pad = stride - nw * 4
        if pad > 0:
            px_parts.extend(b'\x00' * pad)

    px_bytes = bytes(px_parts)

    # BITMAPINFOHEADER (40 bytes exactly)
    dib = bytearray()
    dib.extend(struct.pack('<I', 40))           # biSize
    dib.extend(struct.pack('<i', nw))            # biWidth
    dib.extend(struct.pack('<i', nh * 2))        # biHeight (doubled for XOR+AND masks)
    dib.extend(struct.pack('<H', 1))             # biPlanes
    dib.extend(struct.pack('<H', 32))            # biBitCount
    dib.extend(struct.pack('<I', 0))             # biCompression = BI_RGB
    dib.extend(struct.pack('<I', len(px_bytes))) # biSizeImage
    dib.extend(struct.pack('<i', 0))             # biXPelsPerMeter
    dib.extend(struct.pack('<i', 0))             # biYPelsPerMeter
    dib.extend(struct.pack('<I', 0))             # biClrUsed
    dib.extend(struct.pack('<I', 0))             # biClrImportant
    assert len(dib) == 40

    return bytes(dib), px_bytes


def main():
    try:
        img, ow, oh = load_png(SRC)
    except Exception as e:
        print(f"ERROR: Cannot load {SRC}: {e}", file=sys.stderr)
        sys.exit(1)

    print(f"Source: {ow}x{oh}")

    # Generate frames
    frames_data = []
    for sz in SIZES:
        scale = min(sz / max(ow, oh), 1)
        nw = max(1, round(ow * scale))
        nh = max(1, round(oh * scale))
        resized = img.resize((nw, nh), _PILImage.LANCZOS)
        dib, px = build_frame(resized, nw, nh)
        frames_data.append((nw, nh, dib, px))
        print(f"  Frame: {nw}x{nh}, px={len(px)} bytes")

    # Calculate file layout
    ico_hdr_sz = 6
    num_entries = len(frames_data)
    dir_total = num_entries * 16
    image_start = ico_hdr_sz + dir_total
    total_size = image_start
    for _, _, dib, px in frames_data:
        total_size += 40 + len(px)

    # Build entire file as bytearray
    buf = bytearray(total_size)

    # ICO Header
    buf[0] = 0;       buf[1] = 0          # reserved
    buf[2] = 1;       buf[3] = 0          # type = ICON
    buf[4] = num_entries & 0xFF
    buf[5] = (num_entries >> 8) & 0xFF

    # Write each frame
    cur_img_off = image_start
    for idx, (nw, nh, dib, px) in enumerate(frames_data):
        eo = 6 + idx * 16

        ew = nw if nw < 256 else 0
        eh = nh if nh < 256 else 0
        esz = 40 + len(px)

        # Directory entry (16 bytes, little-endian throughout)
        buf[eo] = ew                            # bWidth
        buf[eo + 1] = eh                        # bHeight
        buf[eo + 2] = 0                         # bColorCount
        buf[eo + 3] = 0                         # bReserved
        buf[eo + 4] = 1;     buf[eo + 5] = 0   # wPlanes = 1
        buf[eo + 6] = 32;    buf[eo + 7] = 0   # BitCount = 32
        # dwBytesInRes
        buf[eo + 8] = esz & 0xFF
        buf[eo + 9] = (esz >> 8) & 0xFF
        buf[eo + 10] = (esz >> 16) & 0xFF
        buf[eo + 11] = (esz >> 24) & 0xFF
        # dwImageOffset
        buf[eo + 12] = cur_img_off & 0xFF
        buf[eo + 13] = (cur_img_off >> 8) & 0xFF
        buf[eo + 14] = (cur_img_off >> 16) & 0xFF
        buf[eo + 15] = (cur_img_off >> 24) & 0xFF

        # Copy image data (DIB header + pixels)
        pos = cur_img_off
        buf[pos:pos + 40] = dib[:40]
        buf[pos + 40:pos + 40 + len(px)] = px

        cur_img_off += esz

    # Write file
    os.makedirs(os.path.dirname(DST), exist_ok=True)
    with open(DST, 'wb') as f:
        f.write(buf)

    final_sz = os.path.getsize(DST)
    print(f"Wrote {final_sz} bytes -> {DST}")


if __name__ == '__main__':
    main()
