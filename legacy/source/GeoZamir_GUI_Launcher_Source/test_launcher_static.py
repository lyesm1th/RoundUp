"""Static smoke check for native GUI launcher and clean Windows distribution."""
from pathlib import Path
import json
import struct

ROOT=Path(__file__).resolve().parent.parent
EXE=ROOT/'geozamir_launcher/GeoZamir_Launcher.exe'
PS=ROOT/'geozamir_launcher/launcher/GeoZamir_Launcher.ps1'
PACKAGE=ROOT/'geozamir_package_gui/GeoZamir_GUI'
p=EXE.read_bytes()
assert p[:2] == b'MZ'
pe=struct.unpack_from('<I',p,0x3c)[0]
assert p[pe:pe+4] == b'PE\0\0'
assert struct.unpack_from('<H',p,pe+24)[0] == 0x20b, 'PE32+ required'
assert struct.unpack_from('<H',p,pe+24+68)[0] == 2, 'GUI subsystem required'
assert b'kernel32.dll\0' in p and b'CreateProcessW\0' in p
assert b'powershell.exe -NoProfile' in p
count=struct.unpack_from('<H',p,pe+6)[0]
option_size=struct.unpack_from('<H',p,pe+20)[0]
table=pe+24+option_size
sections={}
for i in range(count):
    row=table+i*40
    name=p[row:row+8].split(b'\0')[0].decode('ascii')
    virtual_size,rva,raw_size,raw=struct.unpack_from('<IIII',p,row+8)
    sections[name]=(rva,raw,raw_size)
assert '.rsrc' in sections
resource_rva,resource_pos,_=sections['.rsrc']
root_ids=[struct.unpack_from('<I',p,resource_pos+n)[0] for n in (16,24)]
assert root_ids == [3,14], 'Expected RT_ICON and RT_GROUP_ICON'
assert struct.unpack_from('<I',p,resource_pos+128)[0] == resource_rva+180
assert p[resource_pos+180:resource_pos+188] == b'\x89PNG\r\n\x1a\n'
assert struct.unpack_from('<I',p,resource_pos+144)[0] == resource_rva+160
assert b'Add_Shown' in PS.read_bytes()
assert PS.read_bytes().startswith(b'\xef\xbb\xbf'), 'PowerShell 5.1 needs UTF-8 BOM'
config=json.loads((PACKAGE/'launcher.config.json').read_text())
assert config['mode']=='solo' and config['sync']['enabled'] is False
assert config['updates']['remoteManifestUrl'] is None
assert (PACKAGE/'GeoZamir.exe').is_file()
assert (PACKAGE/'GeoZamir_Launcher.exe').read_bytes() == p
assert not [f for f in PACKAGE.rglob('*') if f.suffix.lower() in ('.cmd','.bat')]
print('PASS: GUI PE x64, logo/icon resource, PowerShell bootstrap, offline config, clean archive')
