"""Generates rpdashboard.ico.

The mark is four tiles in a two-by-two grid: four tools, one window. It has to survive being
drawn at 16 pixels in a taskbar and in the notification area, which is why it is four solid
blocks and not anything with detail in it.

Colours are the family palette -- tungsten on ink, the same pair Street Medic's App.xaml
established and the dashboard promoted to the host.

Kept in the repository so the icon can be rebuilt: there is no image editor in this toolchain
and no PIL, so the PNGs are written by hand with zlib and struct and packed into an ICO. Run
`python make-icon.py` from this folder after changing anything here.
"""

from __future__ import annotations

import struct
import zlib
from pathlib import Path

INK = (0x10, 0x0E, 0x0C, 0xFF)
TUNGSTEN = (0xE9, 0xA6, 0x4A, 0xFF)
BONE = (0xED, 0xE4, 0xD8, 0xFF)
CLEAR = (0, 0, 0, 0)

# Windows renders every one of these; 16 and 32 are the ones anybody actually looks at.
SIZES = (16, 24, 32, 48, 64, 128, 256)


def draw(size: int) -> list[list[tuple[int, int, int, int]]]:
    """One frame of the mark, as rows of RGBA pixels."""
    pixels = [[CLEAR] * size for _ in range(size)]

    # A rounded plate so the mark reads as an app icon rather than as floating squares.
    radius = max(1, round(size * 0.18))
    inset = max(0, round(size * 0.02))
    plate_end = size - inset - 1

    for y in range(inset, plate_end + 1):
        for x in range(inset, plate_end + 1):
            if _inside_rounded(x, y, inset, plate_end, radius):
                pixels[y][x] = INK

    # Four tiles. The gap has to stay at least one whole pixel at 16px or the grid closes up
    # into a single block and the mark stops meaning anything.
    margin = max(2, round(size * 0.20))
    gap = max(1, round(size * 0.09))
    tile = (size - margin * 2 - gap) // 2

    if tile < 1:
        return pixels

    # Centre on what the tiles actually occupy rather than on the nominal margin. Integer
    # division above loses up to a pixel, and at 16px that lands the whole grid visibly high.
    margin = (size - (tile * 2 + gap)) // 2

    # The first tile is bone rather than tungsten: an icon of four identical blocks is a
    # texture, and the odd one out is what makes it a shape you recognise at a glance.
    origins = ((0, 0, BONE), (1, 0, TUNGSTEN), (0, 1, TUNGSTEN), (1, 1, TUNGSTEN))

    for column, row, colour in origins:
        left = margin + column * (tile + gap)
        top = margin + row * (tile + gap)

        for y in range(top, top + tile):
            for x in range(left, left + tile):
                if 0 <= x < size and 0 <= y < size:
                    pixels[y][x] = colour

    return pixels


def _inside_rounded(x: int, y: int, low: int, high: int, radius: int) -> bool:
    """Square corners cut to a quarter circle of `radius`."""
    for corner_x, corner_y in ((low, low), (high, low), (low, high), (high, high)):
        near_x = abs(x - corner_x) < radius
        near_y = abs(y - corner_y) < radius
        if not (near_x and near_y):
            continue

        centre_x = corner_x + (radius if corner_x == low else -radius)
        centre_y = corner_y + (radius if corner_y == low else -radius)
        if (x - centre_x) ** 2 + (y - centre_y) ** 2 > radius * radius:
            return False

    return True


def png_bytes(pixels: list[list[tuple[int, int, int, int]]]) -> bytes:
    """A minimal RGBA PNG. Filter type 0 on every row, one zlib stream, three chunks."""
    height = len(pixels)
    width = len(pixels[0])

    raw = bytearray()
    for row in pixels:
        raw.append(0)  # filter: none
        for red, green, blue, alpha in row:
            raw += bytes((red, green, blue, alpha))

    def chunk(tag: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + tag
            + payload
            + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)
        )

    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)  # 8-bit, truecolour+alpha

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )


def ico_bytes(frames: dict[int, bytes]) -> bytes:
    """
    ICONDIR + one ICONDIRENTRY per frame + the PNG payloads.

    PNG-compressed entries are what Vista onwards expects, and .NET's
    Icon.ExtractAssociatedIcon reads them, which is how the tray icon picks this up at runtime.
    """
    count = len(frames)
    directory = struct.pack("<HHH", 0, 1, count)  # reserved, type 1 = icon, count

    offset = 6 + 16 * count
    entries = bytearray()
    payloads = bytearray()

    for size in sorted(frames):
        data = frames[size]
        entries += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,  # 0 means 256
            0 if size >= 256 else size,
            0,  # palette colours: 0 for truecolour
            0,  # reserved
            1,  # colour planes
            32,  # bits per pixel
            len(data),
            offset,
        )
        payloads += data
        offset += len(data)

    return directory + bytes(entries) + bytes(payloads)


def main() -> None:
    frames = {size: png_bytes(draw(size)) for size in SIZES}
    target = Path(__file__).resolve().parent / "rpdashboard.ico"
    target.write_bytes(ico_bytes(frames))
    print(f"wrote {target} ({target.stat().st_size} bytes, {len(frames)} sizes)")


if __name__ == "__main__":
    main()
