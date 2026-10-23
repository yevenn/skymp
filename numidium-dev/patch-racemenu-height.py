# Limits RaceMenu's Height slider (Body Scales) to what the Numidium server allows.
#
#   python patch-racemenu-height.py <Skyrim Data folder>
#
# Reads Scripts\racemenuplugin.pex from the installed RaceMenu.bsa, changes the
# Height slider's range from 0.25-2.00 to HEIGHT_MIN-HEIGHT_MAX, and writes it
# as a loose Data\Scripts\racemenuplugin.pex, which the game prefers over the
# archive. Patches the player's own RaceMenu instead of shipping a modified
# file. Keep the limits in sync with BODY_SCALE_LIMITS in numidium-gamemode.
import os, shutil, struct, sys

HEIGHT_MIN = 0.95
HEIGHT_MAX = 1.05

def lz4_block(src, out):
    i = 0
    while i < len(src):
        token = src[i]; i += 1
        n = token >> 4
        if n == 15:
            while True:
                b = src[i]; i += 1; n += b
                if b != 255: break
        out += src[i:i + n]; i += n
        if i >= len(src): break
        offset = src[i] | (src[i + 1] << 8); i += 2
        m = token & 15
        if m == 15:
            while True:
                b = src[i]; i += 1; m += b
                if b != 255: break
        m += 4
        start = len(out) - offset
        for k in range(m):
            out.append(out[start + k])

def lz4_frame(data):
    assert struct.unpack_from('<I', data, 0)[0] == 0x184D2204, 'not an LZ4 frame'
    flg = data[4]
    i = 6 + (8 if flg & 0x08 else 0) + (4 if flg & 0x01 else 0) + 1
    out = bytearray()
    while True:
        size = struct.unpack_from('<I', data, i)[0]; i += 4
        if size == 0: break
        raw, size = size & 0x80000000, size & 0x7FFFFFFF
        block = data[i:i + size]; i += size
        if raw: out += block
        else: lz4_block(block, out)
        if flg & 0x10: i += 4
    return bytes(out)

def read_from_bsa(path, wanted):
    """Returns one file from a Skyrim SE (v105) archive, e.g. 'scripts\\racemenuplugin.pex'."""
    data = open(path, 'rb').read()
    magic, version, offset, flags, folders, files, folder_names_len, file_names_len, _ = struct.unpack_from('<4sIIIIIIII', data, 0)
    if magic != b'BSA\0' or version != 105:
        raise SystemExit(f'{path} is not a Skyrim SE archive')
    entries = []
    for f in range(folders):
        _hash, count, _pad, foff = struct.unpack_from('<QIIQ', data, offset + f * 24)
        p = foff - file_names_len
        nlen = data[p]
        folder = data[p + 1:p + nlen].rstrip(b'\0').decode('latin-1')
        p += 1 + nlen
        for k in range(count):
            _hash, size, doff = struct.unpack_from('<QII', data, p + k * 16)
            entries.append([folder, size, doff])
    names_at = offset + folders * 24 + folder_names_len + folders + files * 16
    names = data[names_at:names_at + file_names_len].split(b'\0')
    for (folder, size, doff), name in zip(entries, names):
        if (folder + '\\' + name.decode('latin-1')).lower() != wanted:
            continue
        compressed = bool(flags & 0x4) != bool(size & 0x40000000)
        blob = data[doff:doff + (size & 0x3FFFFFFF)]
        return lz4_frame(blob[4:]) if compressed else blob
    raise SystemExit(f'{wanted} not found in {path}')

def float_arg(value):
    # A float argument in Papyrus bytecode: type 4, then the value big-endian
    return b'\x04' + struct.pack('>f', value)

def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__ or 'usage: patch-racemenu-height.py <Skyrim Data folder>')
    data_dir = sys.argv[1]
    pex = read_from_bsa(os.path.join(data_dir, 'RaceMenu.bsa'), 'scripts\\racemenuplugin.pex')
    # AddSliderEx("$Height", ..., 0.25, 2.00, 0.01, ...): min, max, step
    original = float_arg(0.25) + float_arg(2.00) + float_arg(0.01)
    if pex.count(original) != 1:
        raise SystemExit('Height slider range not found exactly once; different RaceMenu version?')
    patched = pex.replace(original, float_arg(HEIGHT_MIN) + float_arg(HEIGHT_MAX) + float_arg(0.01))

    target = os.path.join(data_dir, 'Scripts', 'racemenuplugin.pex')
    if os.path.exists(target):
        if open(target, 'rb').read() == patched:
            print(f'{target} is already patched')
            return
        backup = target + '.before-numidium'
        shutil.copyfile(target, backup)
        print(f'backed up the existing loose script to {backup}')
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, 'wb') as fh:
        fh.write(patched)
    print(f'wrote {target}: Height slider {HEIGHT_MIN:.2f}-{HEIGHT_MAX:.2f}')

main()
