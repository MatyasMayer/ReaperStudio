"""Linear x86 disassembly of a VA range in a PE (helper for functions Ghidra missed)."""
import sys
import capstone
import pefile

pe = pefile.PE(sys.argv[1], fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
imports = {}
pe.parse_data_directories([pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"]])
for e in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
    for i in e.imports:
        if i.name:
            imports[i.address] = i.name.decode()
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
for spec in sys.argv[2:]:
    va, n = (int(x, 0) for x in spec.split(":"))
    data = pe.get_data(va - base, n)
    print(f"=== {va:#x}")
    for ins in md.disasm(data, va):
        note = ""
        for a, nm in imports.items():
            if f"{a:#x}" in ins.op_str:
                note = f"   ; {nm}"
        print(f"{ins.address:08x}  {ins.mnemonic:6} {ins.op_str}{note}")
        if ins.mnemonic == "ret" and "--stop-ret" in sys.argv[0:1]:
            break
