#!/usr/bin/env python3
"""Inspect embedded web resource slots in the original Go Windows program."""
from pathlib import Path
from zipfile import ZipFile

archive = Path("legacy/GeoZamir_5.5_GUI_Launcher_Windows.zip")
with ZipFile(archive) as z:
    names = [n for n in z.namelist() if n.endswith("/GeoZamir.exe")]
    if len(names) != 1:
        raise RuntimeError(f"Expected exactly one executable; got {names}")
    binary = z.read(names[0])
print(f"GeoZamir original executable: {len(binary):,} bytes")
for name in ("index.html", "app.js", "style.css", "favicon.svg", "vendor/jszip.min.js"):
    path = Path("legacy/extracted_runtime") / name
    if not path.exists():
        continue
    data = path.read_bytes()
    print(f"RESOURCE {name}: extracted {len(data):,} bytes")
    for cutoff in (len(data), 256, 64, 24):
        if len(data) < cutoff:
            continue
        key = data[:cutoff]
        positions = []
        cursor = 0
        while len(positions) < 4:
            at = binary.find(key, cursor)
            if at < 0:
                break
            positions.append(at)
            cursor = at + 1
        if positions:
            print(f"  FOUND prefix={cutoff} at offsets={positions}")
            match = positions[0]
            comparison = min(len(data),len(binary)-match)
            count = 0
            for a,b in zip(data[:comparison],binary[match:match+comparison]):
                if a != b:
                    break
                count += 1
            print(f"  Contiguous byte match: {count:,} / {len(data):,}")
            break
    else:
        print("  Not found as uncompressed plain text")
