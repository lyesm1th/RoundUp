#!/usr/bin/env python3
"""Generate a scalable RoundUp Windows icon without third-party dependencies."""
from __future__ import annotations
import math
import pathlib
import struct
import zlib


def chunk(tag: bytes, payload: bytes) -> bytes:
    return struct.pack(">I", len(payload)) + tag + payload + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)


def scene(x: float, y: float) -> tuple[int, int, int, int]:
    # Rounded deep-teal tile; everything outside the tile is transparent.
    r = 0.19
    qx = max(abs(x - 0.5) - (0.5 - r), 0)
    qy = max(abs(y - 0.5) - (0.5 - r), 0)
    if qx * qx + qy * qy > r * r:
        return (0, 0, 0, 0)

    top = max(0.0, min(1.0, y))
    color = (int(24 + 4 * top), int(78 - 21 * top), int(77 - 11 * top), 255)

    # A map marker: a circular head and a pointed tail.
    outer = (x - .50) ** 2 + (y - .38) ** 2 <= .235 ** 2
    left = .5 - .18 * max(0.0, 1.0 - (y - .39) / .43)
    right = .5 + .18 * max(0.0, 1.0 - (y - .39) / .43)
    tail = .40 <= y <= .82 and left <= x <= right
    if outer or tail:
        color = (111, 238, 182, 255)
    if (x - .50) ** 2 + (y - .37) ** 2 <= .104 ** 2:
        color = (16, 75, 69, 255)
    if (x - .476) ** 2 + (y - .337) ** 2 <= .028 ** 2:
        color = (231, 255, 244, 255)
    return color


def render(size: int) -> bytes:
    pixels = bytearray()
    samples = 4
    for py in range(size):
        pixels.append(0)  # PNG filter type None
        for px in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(samples):
                for sx in range(samples):
                    sample = scene((px + (sx + .5) / samples) / size,
                                   (py + (sy + .5) / samples) / size)
                    for idx in range(4):
                        acc[idx] += sample[idx]
            rgba = [int(v / (samples * samples) + .5) for v in acc]
            pixels.extend(rgba)
    raw = bytes(pixels)
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" +
            chunk(b"IHDR", header) +
            chunk(b"IDAT", zlib.compress(raw, 9)) +
            chunk(b"IEND", b""))


def main() -> None:
    sizes = (16, 32, 48, 64, 128, 256)
    images = [render(size) for size in sizes]
    header = struct.pack("<HHH", 0, 1, len(images))
    cursor = len(header) + len(images) * 16
    entries = []
    for size, data in zip(sizes, images):
        dim = size if size < 256 else 0
        entries.append(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), cursor))
        cursor += len(data)
    output = pathlib.Path("assets") / "RoundUp.ico"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(header + b"".join(entries) + b"".join(images))
    print(f"Generated {output} ({output.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
