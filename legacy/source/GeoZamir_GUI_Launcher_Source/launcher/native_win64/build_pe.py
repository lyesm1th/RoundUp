"""Cross-build a tiny native Windows x64 launcher without MinGW.

GNU cc/objcopy/ld on Linux can emit PE/COFF with i386pep, but objcopy loses
some ELF local-symbol relocations. We verify and repair the six affected
addresses, while keeping the Windows relocation table for ASLR intact.
The launcher delegates the visible UI to Windows Forms PowerShell 5.1.
"""
from pathlib import Path
import re
import struct
import subprocess

HERE = Path(__file__).resolve().parent
BUILD = HERE / 'build'
EXE = HERE.parent.parent / 'GeoZamir_Launcher.exe'


def cmd(*args):
    return subprocess.check_output([str(x) for x in args], text=True)


def sections(pe_path):
    result = {}
    for row in cmd('objdump', '-h', pe_path).splitlines():
        cols = row.split()
        if len(cols) >= 6 and cols[0].isdigit() and cols[1].startswith('.'):
            result[cols[1]] = dict(vma=int(cols[3],16),offset=int(cols[5],16))
    return result


def symbols(pe_path):
    result = {}
    for row in cmd('nm', '-n', pe_path).splitlines():
        match = re.match(r'^([0-9a-fA-F]+)\s+[a-zA-Z]\s+(\w+)$',row)
        if match: result[match.group(2)] = int(match.group(1),16)
    return result


BUILD.mkdir(exist_ok=True)
for stem in ['kernel32_mock', 'launcher_stub']:
    args=['gcc','-c','-Os','-mcmodel=large','-ffreestanding','-fno-stack-protector',
          '-fno-asynchronous-unwind-tables','-fno-pic','-o',BUILD/(stem+'.o'),HERE/(stem+'.c')]
    subprocess.run([str(x) for x in args],check=True)
    subprocess.run(['objcopy','-R','.comment','-O','pe-x86-64',
                    str(BUILD/(stem+'.o')),str(BUILD/(stem+'.obj'))],check=True)

# A real Windows icon in Explorer and on the taskbar. The resource tree has
# type -> icon id -> language -> data, for RT_ICON and RT_GROUP_ICON.
png=(HERE.parent/'assets/geozamir-mark.png').read_bytes()
assert png.startswith(b'\x89PNG\r\n\x1a\n')
resource=bytearray(180+len(png))
def directory(offset,count): struct.pack_into('<IIHHHH',resource,offset,0,0,0,0,0,count)
def entry(offset,identifier,target,subdir):
    struct.pack_into('<II',resource,offset,identifier,target|(0x80000000 if subdir else 0))
directory(0,2);entry(16,3,32,True);entry(24,14,56,True)
directory(32,1);entry(48,1,80,True)
directory(56,1);entry(72,1,104,True)
directory(80,1);entry(96,1033,128,False)
directory(104,1);entry(120,1033,144,False)
struct.pack_into('<IIII',resource,128,0,len(png),0,0)
struct.pack_into('<IIII',resource,144,0,20,0,0)
struct.pack_into('<HHHBBBBHHIH',resource,160,0,1,1,0,0,0,0,1,32,len(png),1)
resource[180:]=png
(BUILD/'resource.bin').write_bytes(resource)
subprocess.run(['objcopy','-I','binary','-O','elf64-x86-64',
                str(BUILD/'resource.bin'),str(BUILD/'resource.o')],check=True)
subprocess.run(['objcopy','-O','pe-x86-64',
                '--rename-section','.data=.rsrc,alloc,load,readonly,data,contents',
                str(BUILD/'resource.o'),str(BUILD/'resource.obj')],check=True)

subprocess.run(['ld','-mi386pep','-shared','--export-all-symbols','--out-implib',
                str(BUILD/'libkernel32.a'),'-e','ExitProcess','-o',
                str(BUILD/'kernel32.dll'),str(BUILD/'kernel32_mock.obj')],check=True)
subprocess.run(['ld','-mi386pep','--subsystem','windows','--disable-auto-import',
                '--disable-runtime-pseudo-reloc','-e','launcher_entry','-o',str(EXE),
                str(BUILD/'launcher_stub.obj'),str(BUILD/'resource.obj'),
                str(BUILD/'libkernel32.a')],check=True)

payload=bytearray(EXE.read_bytes())
places=sections(EXE)
names=symbols(EXE)
assert 'launcher_entry' in names and '.text' in places and '.rodata.' in places
rodata=places['.rodata.']
prefix=b'powershell.exe -NoProfile -Sta -ExecutionPolicy Bypass -WindowStyle Hidden -File "'
suffix=b'launcher\\GeoZamir_Launcher.ps1"'

def address_of(needle):
    filepos=payload.find(needle)
    assert filepos>=rodata['offset'], f'Missing resource {needle!r}'
    assert filepos < rodata['offset']+1024
    return rodata['vma']+filepos-rodata['offset']

locations={
    0x0b:names['module_path'], 0x61:address_of(prefix),
    0x6d:names['command_line'], 0xb0:address_of(suffix),
    0xf3:names['startup'], 0xfd:names['process'],
}
text_offset=places['.text']['offset']
for displacement,target in locations.items():
    pointer=text_offset+displacement
    before=struct.unpack_from('<Q',payload,pointer)[0]
    assert before in (0,names['module_path']), f'Unexpected relocation at {displacement:x}: {before:x}'
    struct.pack_into('<Q',payload,pointer,target)

assert '.rsrc' in places
resource_file=places['.rsrc']['offset']
resource_rva=places['.rsrc']['vma']-0x140000000
struct.pack_into('<I',payload,resource_file+128,resource_rva+180)
struct.pack_into('<I',payload,resource_file+144,resource_rva+160)

assert payload[:2] == b'MZ'
assert b'kernel32.dll' in payload
EXE.write_bytes(payload)
print(f'Built {EXE} ({len(payload)} bytes, Windows GUI x64, branded icon, 3 kernel32 imports)')
