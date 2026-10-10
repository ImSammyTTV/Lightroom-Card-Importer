"""Joins PNG frames into one animated PNG (APNG), which GitHub shows animated in a README.

    python make-apng.py <frames-dir> <out.png>

<frames-dir> holds frame_000.png ... and frames.txt ("file delay-in-ms" per line), as written by
`CardImporter.exe --screenshot-frames <dir>`. Standard library only.
"""
import os
import struct
import sys
import zlib

SIG = b"\x89PNG\r\n\x1a\n"


def read_chunks(path):
    data = open(path, "rb").read()
    assert data[:8] == SIG, path + " is not a PNG"
    pos, out = 8, []
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        ctype = data[pos + 4:pos + 8]
        out.append((ctype, data[pos + 8:pos + 8 + length]))
        pos += 12 + length
    return out


def chunk(ctype, body):
    return struct.pack(">I", len(body)) + ctype + body + struct.pack(">I", zlib.crc32(ctype + body) & 0xFFFFFFFF)


def main(frames_dir, out_path):
    frames = []
    for line in open(os.path.join(frames_dir, "frames.txt"), encoding="utf-8"):
        line = line.strip()
        if line:
            name, ms = line.rsplit(" ", 1)
            frames.append((name, int(ms)))

    ihdr = None
    out = [SIG]
    seq = 0
    for i, (name, ms) in enumerate(frames):
        chunks = read_chunks(os.path.join(frames_dir, name))
        header = next(b for t, b in chunks if t == b"IHDR")
        if ihdr is None:
            ihdr = header
            width, height = struct.unpack(">II", header[:8])
            out.append(chunk(b"IHDR", header))
            out.append(chunk(b"acTL", struct.pack(">II", len(frames), 0)))  # 0 = loop forever
        elif header != ihdr:
            raise SystemExit(name + " has a different size or format from the first frame")
        idat = b"".join(b for t, b in chunks if t == b"IDAT")
        # frame control: sequence, width, height, x, y, delay numerator, delay denominator, dispose, blend
        out.append(chunk(b"fcTL", struct.pack(">IIIIIHHBB", seq, width, height, 0, 0, ms, 1000, 0, 0)))
        seq += 1
        if i == 0:
            out.append(chunk(b"IDAT", idat))
        else:
            out.append(chunk(b"fdAT", struct.pack(">I", seq) + idat))
            seq += 1
    out.append(chunk(b"IEND", b""))
    open(out_path, "wb").write(b"".join(out))
    total = sum(ms for _, ms in frames)
    print("wrote %s: %d frames, %.1fs loop, %d KB" % (out_path, len(frames), total / 1000, os.path.getsize(out_path) // 1024))


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
