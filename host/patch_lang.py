# Patch the PE version-resource language (neutral 0x0000 -> zh-CN 0x0804) in place.
# Same-length edits only: safe for single-file bundle offsets.
import struct, sys

def u16(b, o): return struct.unpack_from("<H", b, o)[0]
def u32(b, o): return struct.unpack_from("<I", b, o)[0]

def main(path):
    b = bytearray(open(path, "rb").read())
    pe = u32(b, 0x3C)
    assert b[pe:pe+4] == b"PE\0\0"
    nsec = u16(b, pe+6)
    optsz = u16(b, pe+20)
    opt = pe + 24
    magic = u16(b, opt)
    dd = opt + (112 if magic == 0x20B else 96)
    rsrc_rva, _ = u32(b, dd+16), u32(b, dd+20)
    secs = []
    for i in range(nsec):
        so = opt + optsz + i*40
        va, vsz, raw, rsz = u32(b, so+12), u32(b, so+8), u32(b, so+20), u32(b, so+16)
        secs.append((va, vsz, raw, rsz))
    def rva2off(rva):
        for va, vsz, raw, rsz in secs:
            if va <= rva < va + max(vsz, rsz): return raw + (rva - va)
        raise ValueError("rva not in sections")
    base = rva2off(rsrc_rva)
    def entries(dir_off):
        named, ids = u16(b, dir_off+12), u16(b, dir_off+14)
        return [(u32(b, dir_off+16+i*8), u32(b, dir_off+16+i*8+4)) for i in range(named+ids)]
    patched_lang_entry = patched_block = patched_trans = False
    for name1, off1 in entries(base):
        if name1 & 0x7FFFFFFF != 16:  # RT_VERSION only
            continue
        for name2, off2 in entries(base + (off1 & 0x7FFFFFFF)):
            lang_dir = base + (off2 & 0x7FFFFFFF)
            for j, (name3, off3) in enumerate(entries(lang_dir)):
                if off3 & 0x80000000: continue
                de = base + off3
                blob_rva, blob_sz = u32(b, de), u32(b, de+4)
                blob = rva2off(blob_rva)
                if (name3 & 0x7FFFFFFF) != 0x0804:
                    struct.pack_into("<I", b, lang_dir+16+j*8, 0x0804)
                    patched_lang_entry = True
                s_old = "000004b0".encode("utf-16-le"); s_new = "080404b0".encode("utf-16-le")
                k = bytes(b[blob:blob+blob_sz]).find(s_old)
                if k != -1:
                    b[blob+k:blob+k+16] = s_new; patched_block = True
                tkey = "Translation".encode("utf-16-le")
                k = bytes(b[blob:blob+blob_sz]).find(tkey)
                if k != -1:
                    seg = bytes(b[blob+k:blob+k+64])
                    m = seg.find(b"\x00\x00\xb0\x04")
                    if m != -1:
                        b[blob+k+m:blob+k+m+4] = b"\x04\x08\xb0\x04"; patched_trans = True
    open(path, "wb").write(b)
    print(path, "lang_entry:", patched_lang_entry, "block:", patched_block, "translation:", patched_trans)

if __name__ == "__main__":
    main(sys.argv[1])
