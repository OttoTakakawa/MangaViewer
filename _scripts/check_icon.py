import struct

exe = r'G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\bin\Debug\net8.0-windows\MangaReader.Native.exe'
with open(exe, 'rb') as f:
    data = f.read()

pe_off = struct.unpack_from('<I', data, 60)[0]
coff = pe_off + 4
num_sections = struct.unpack_from('<H', data, coff+2)[0]
opt_size = struct.unpack_from('<H', data, coff+14)[0]
sec_start = coff + 20 + opt_size

secs = []
for i in range(num_sections):
    name_end = sec_start
    name = b''
    while name_end < min(sec_start+8, len(data)) and data[name_end:][0:1] != b'\x00':
        name += data[name_end:name_end+1]
        name_end += 1
    va = struct.unpack_from('<I', data, sec_start+12)[0]
    size = struct.unpack_from('<I', data, sec_start+16)[0]
    fo = struct.unpack_from('<I', data, sec_start+20)[0]
    secs.append((name.hex(), va, fo, size))
    sec_start += 40

print("Sections:", [(n.encode('ascii','replace'), f'{v:#x}', f'{fo:#x}', s) for n, v, fo, s in secs])

opt_magic_off = coff + 20
magic = struct.unpack_from('<H', data, opt_magic_off)[0]
print(f"Magic: {magic:#06x}")

if magic == 0x20b:
    n_d_off = opt_magic_off + 96
elif magic == 0x10b:
    n_d_off = opt_magic_off + 92
n_dirs = struct.unpack_from('<I', data, n_d_off)[0]
print(f"NumDirectories: {n_dirs}")

dir_base = n_d_off + 8
res_rva = struct.unpack_from('<I', data, dir_base + 2*8)[0]
res_size = struct.unpack_from('<I', data, dir_base + 2*8 + 4)[0]
print(f"Resource: RVA={res_rva:#x}, Size={res_size}")

if res_rva > 0 and res_size > 0:
    fo_map = None
    for name, va, foff, sz in secs:
        if va <= res_rva < va + max(sz, 1):
            fo_map = foff + (res_rva - va)
            break

    if fo_map:
        rd = data[fo_map:fo_map+res_size]
        type_count = struct.unpack_from('<I', rd, 0)[0]
        print(f"\nResource directory entries: {type_count}")

        found_icon_type = False
        pos = 4
        for i in range(min(type_count, 50)):
            if pos + 16 > len(rd): break
            flags = struct.unpack_from('<I', rd, pos)[0]
            nid = struct.unpack_from('<I', rd, pos+4)[0]
            entry_off = struct.unpack_from('<I', rd, pos+8)[0] & 0x7FFFFFFF

            if nid in (3, 16):
                found_icon_type = True
                print(f"\n  *** FOUND RT_ICON(3) or RT_GROUP_ICON(16) at entry {i}! ***")

                # Follow sub-directory
                sub_rd = rd[entry_off:] if entry_off < len(rd) else b''
                if sub_rd:
                    sub_count = struct.unpack_from('<I', sub_rd, 0)[0]
                    print(f"  Sub-entry count: {sub_count}")

                    sp = 4
                    for j in range(min(sub_count, 10)):
                        if sp + 16 > len(sub_rd): break
                        soff = struct.unpack_from('<I', sub_rd, sp+8)[0] & 0x7FFFFFFF
                        isize = struct.unpack_from('<I', sub_rd, sp+12)[0]
                        icon_bytes = rd[soff:soff+min(isize, 64)]
                        print(f"    Icon #{j}: file_fo={fo_map+soff:#x}, size={isize}")
                        print(f"      First 32 bytes hex: {icon_bytes[:32].hex()}")
                        sp += 16

                if not found_icon_type:
                    pass  # Will check after loop

            pos += 16

        if found_icon_type:
            print("\n  RESULT: Icon resources are embedded!")
        else:
            print("\n  RESULT: No icon type found in resource table.")
    else:
        print("Could not map VA to file offset")
else:
    print("No resource directory found")
