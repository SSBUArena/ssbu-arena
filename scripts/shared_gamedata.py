"""Shared game data: use the game data of an OpenSmash Ultimate install on this PC instead of a second copy (installer tool).

A player who has both SSBU Arena and OpenSmash Ultimate kept the game's data twice: OpenSmash's dump\\modules (the
decrypted base NCA + the 13.0.5 update's own bytes, ~17 GB, read through a layer map) and Arena's dump\\romfs\\data.arc
(~16.4 GB, the same archive as a plain file). With sharing on, Arena keeps no data.arc of its own: it writes a LINKED
archive, romfs\\data.arcl (tools/nro/arc_file.py says the format), that says where each byte of data.arc lies in
OpenSmash's files. The runtime (runtime/arcpack.c) and the tools read through it. Nothing is written in OpenSmash's folder.

What Arena makes in its own folder (<build>\\dump_shared, about 90 MB):
    exefs\\            a COPY of OpenSmash's dump\\exefs (5 files, ~50 MB; a copy, so an OpenSmash update cannot change it)
    romfs\\data.arcl   the map (a few hundred KB) + the size and date of each OpenSmash file it reads
    romfs\\.nrr\\      the RomFS's .nrr files, copied out of the image (14 files, 4 KB each)
    sysdata\\fonts\\   the system fonts, decoded from OpenSmash's .bfttf files
    shared_from.json  where it came from and what was checked

Checks before anything is used (any failure: nothing is made, the normal install goes on):
    - the folder is an OpenSmash install (tools\\install_windowsmash.ps1, dump\\modules) and its game data is the vanilla
      cfg (romfs_vanilla.cfg, never a mods map) in the layered form, not encrypted in place (no romfs.crypt)
    - every file the map names lies inside that OpenSmash folder (nothing else on the PC is read)
    - the update's blocks were all confirmed by OpenSmash's installer (romfs.unverified.txt absent or empty)
    - main in its exefs is a game build Arena handles (scripts/setup.py GAME_BUILDS: OpenSmash is 13.0.5)
    - data.arc read through the map is whole (its header and file-system table) and has the size of that build's data.arc
    - with --full: every byte of data.arc is read once through the map (SHA-256 recorded in shared_from.json)

    python scripts/shared_gamedata.py find                      the OpenSmash installs this PC remembers
    python scripts/shared_gamedata.py check <opensmash folder>  the checks above, nothing written
    python scripts/shared_gamedata.py make <opensmash folder> <out folder> [--full]
    python scripts/shared_gamedata.py status <dump folder>      a linked dump still usable? (exit 1 if not)
"""
import bisect
import hashlib
import json
import os
import shutil
import struct
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(REPO, 'tools', 'nro'))
import arc_file  # noqa: E402

GAME_BUILDS = {0x39c7a90: '13.0.2', 0x39c7e90: '13.0.4', 0x39c8440: '13.0.5'}     # scripts/setup.py (main's code size)
ARC_SIZES = {'13.0.4': 17600869448, '13.0.5': 17600880952}                       # data.arc of each build, bytes
# SHA-256 of a whole data.arc (--full): 13.0.5's measured 2026-11-23 through the link to an OpenSmash install whose update
# blocks OpenSmash's installer had all confirmed (no romfs.unverified.txt); it differs from 13.0.4's data.arc in 13.6 MB
ARC_SHA256 = {'13.0.5': 'ae9ebecfc570933cd4d2d85595b231de59ba1c9d2990e8eb07a5ec421da03a14'}
LAYER_MAGIC = b'SSBUROM1'
LAYER_ZERO = 0xFFFFFFFF


class NotUsable(Exception):
    pass


def norm(p):
    return os.path.normcase(os.path.abspath(p))


def inside(path, folder):
    p, f = norm(path), norm(folder)
    return p == f or p.startswith(f.rstrip('\\/') + os.sep)


# ---------------------------------------------------------------------------------------------------------- finding
def remembered_opensmash():
    """The project folder OpenSmash's launcher remembers (%LOCALAPPDATA%\\OpenSmash\\project_folder.txt), or None."""
    la = os.environ.get('LOCALAPPDATA')
    if not la:
        return None
    try:
        with open(os.path.join(la, 'OpenSmash', 'project_folder.txt'), encoding='utf-8-sig') as f:
            line = f.readline().strip().strip('"')
        return line or None
    except OSError:
        return None


def is_opensmash(d):
    return bool(d) and os.path.isfile(os.path.join(d, 'tools', 'install_windowsmash.ps1')) and os.path.isdir(os.path.join(d, 'dump', 'modules'))


def find(extra=()):
    """OpenSmash install folders: the ones given, SSBU_OPENSMASH_DIR, then the one OpenSmash's launcher remembers."""
    out = []
    for d in list(extra) + [os.environ.get('SSBU_OPENSMASH_DIR'), remembered_opensmash()]:
        if d and is_opensmash(d) and norm(d) not in [norm(x) for x in out]:
            out.append(os.path.abspath(d))
    return out


# ---------------------------------------------------------------------------------------------------------- the image
class RomImage:
    """OpenSmash's RomFS image as its runtime reads it (OpenSmash runtime/hle_fs.c rom_open, the layered form only)."""

    def __init__(self, root):
        self.root = root
        mod = os.path.join(root, 'dump', 'modules')
        cfg = os.path.join(mod, 'romfs_vanilla.cfg')
        if not os.path.isfile(cfg):
            cfg = os.path.join(mod, 'romfs.cfg')
        self.cfg = cfg
        if os.path.isfile(os.path.join(mod, 'romfs.crypt')):
            raise NotUsable('its game data is read in place from the encrypted game file (romfs.crypt): only a decrypted '
                            'copy can be shared')
        try:
            with open(cfg, encoding='utf-8-sig') as f:
                lines = [l.strip() for l in f.read().splitlines() if l.strip()]
        except OSError as e:
            raise NotUsable('no game data cfg (%s)' % e)
        if not lines or lines[0] != 'layered' or len(lines) < 3:
            raise NotUsable('%s is not the layered form (base + update)' % cfg)
        if 'mods' in os.path.basename(lines[1]).lower():
            raise NotUsable('%s names a mods map (%s): only the vanilla game data is shared' % (cfg, lines[1]))
        self.map = self._local(lines[1], mod)
        self.paths = [self._local(p, mod) for p in lines[2:]]
        for p in [self.map] + self.paths:
            if not inside(p, root):
                raise NotUsable('%s is outside the OpenSmash folder: not read' % p)
            if not os.path.isfile(p):
                raise NotUsable('%s is missing' % p)
        with open(self.map, 'rb') as f:
            d = f.read()
        if d[:8] != LAYER_MAGIC:
            raise NotUsable('%s is not a layer map' % self.map)
        self.size, n, _ = struct.unpack_from('<QII', d, 8)
        self.layers = [(at, src, off) for at, off, src, _ in struct.iter_unpack('<QQII', d[24:24 + n * 24])]
        if not self.layers or self.layers[0][0] != 0:
            raise NotUsable('%s: the layers do not start at 0' % self.map)
        for i, (at, src, off) in enumerate(self.layers):
            if src != LAYER_ZERO and src >= len(self.paths):
                raise NotUsable('%s: layer %d names source %d of %d' % (self.map, i, src, len(self.paths)))
            if i and at <= self.layers[i - 1][0]:
                raise NotUsable('%s: layers out of order at %d' % (self.map, i))
        self.at = [l[0] for l in self.layers]
        self.files = [open(p, 'rb') for p in self.paths]
        h = struct.unpack('<10Q', self.read(0, 0x50))
        if h[0] != 0x50:
            raise NotUsable('no RomFS image behind %s' % cfg)
        self.dirs, self.ftab, self.data = self.read(h[3], h[4]), self.read(h[7], h[8]), h[9]

    @staticmethod
    def _local(p, mod):
        """a path of the cfg; a moved folder: the file of the same name in this dump\\modules (as hle_fs.c does)"""
        if os.path.isfile(p):
            return os.path.abspath(p)
        alt = os.path.join(mod, os.path.basename(p.replace('\\', '/')))
        return alt if os.path.isfile(alt) else p

    def close(self):
        for f in self.files:
            f.close()

    def extents(self, off, size):
        """[(image offset, length, source, source offset)] of image bytes [off, off + size)"""
        out = []
        k = bisect.bisect_right(self.at, off) - 1
        while size > 0:
            at, src, soff = self.layers[k]
            end = self.layers[k + 1][0] if k + 1 < len(self.layers) else self.size
            n = min(size, end - off)
            out.append((off, n, src, soff + (off - at) if src != LAYER_ZERO else 0))
            off += n
            size -= n
            k += 1
        return out

    def read(self, off, size):
        b = bytearray()
        for _, n, src, soff in self.extents(off, size):
            if src == LAYER_ZERO:
                b += bytes(n)
            else:
                f = self.files[src]
                f.seek(soff)
                b += f.read(n)
        return bytes(b)

    def walk(self, d=0, prefix=''):
        """(path, data offset in the image, size) of every file of the RomFS"""
        _, _, child_dir, child_file, _, _ = struct.unpack_from('<6I', self.dirs, d)
        k = child_file
        while k != LAYER_ZERO:
            _, sib, off, sz, _, nl = struct.unpack_from('<IIQQII', self.ftab, k)
            name = self.ftab[k + 32:k + 32 + nl].decode('utf-8', 'replace')
            yield prefix + '/' + name, self.data + off, sz
            k = sib
        k = child_dir
        while k != LAYER_ZERO:
            _, sib, _, _, _, nl = struct.unpack_from('<6I', self.dirs, k)
            name = self.dirs[k + 24:k + 24 + nl].decode('utf-8', 'replace')
            yield from self.walk(k, prefix + '/' + name)
            k = sib


# ---------------------------------------------------------------------------------------------------------- checks
def nso_text_size(path):
    with open(path, 'rb') as f:
        h = f.read(0x20)
    if h[:4] != b'NSO0':
        raise NotUsable('%s is not an NSO' % path)
    return struct.unpack_from('<I', h, 0x18)[0]


def arc_whole(read, size):
    """data.arc's header tables and file-system table lie inside the archive (install.py arc_complete)"""
    magic, stream, files, shared, fs, search, _ = struct.unpack('<7Q', read(0, 0x38))
    if magic != 0xABCDEF9876543210 or max(stream, files, shared, fs, search) + 16 > size:
        return False
    m, _, comp, _ = struct.unpack('<4I', read(fs, 16))
    return m == 0x10 and fs + 16 + comp <= size


def check(root):
    """-> dict describing what would be shared; raises NotUsable with the reason."""
    root = os.path.abspath(root)
    if not is_opensmash(root):
        raise NotUsable('%s is not an OpenSmash Ultimate install (no tools\\install_windowsmash.ps1 / dump\\modules)' % root)
    unv = os.path.join(root, 'dump', 'modules', 'romfs.unverified.txt')
    if os.path.isfile(unv):
        with open(unv, encoding='utf-8', errors='replace') as f:
            if any(l[:1].isdigit() for l in f):
                raise NotUsable('OpenSmash could not confirm some blocks of its game data (%s): not shared' % unv)
    exefs = os.path.join(root, 'dump', 'exefs')
    for n in ('main', 'main.npdm', 'rtld', 'sdk', 'subsdk0'):
        if not os.path.isfile(os.path.join(exefs, n)):
            raise NotUsable('%s is missing' % os.path.join(exefs, n))
    version = GAME_BUILDS.get(nso_text_size(os.path.join(exefs, 'main')))
    if not version:
        raise NotUsable('its game build is not one Arena handles (%s)' % ', '.join(sorted(GAME_BUILDS.values())))
    img = RomImage(root)
    try:
        files = list(img.walk())
        arc = [f for f in files if f[0].lower() == '/data.arc']
        if not arc:
            raise NotUsable('no data.arc in its RomFS image')
        _, aoff, asize = arc[0]
        if aoff + asize > img.size:
            raise NotUsable('data.arc goes past the end of the image')
        want = ARC_SIZES.get(version)
        if want and asize != want:
            raise NotUsable('its data.arc has %d bytes, %s\'s has %d' % (asize, version, want))
        if not arc_whole(lambda o, n: img.read(aoff + o, n), asize):
            raise NotUsable('its data.arc is not whole (header or file-system table)')
        nrr = [f for f in files if f[0].lower().startswith('/.nrr/') and f[0].lower().endswith('.nrr')]
        if not nrr:
            raise NotUsable('no .nrr files in its RomFS image')
        ext = img.extents(aoff, asize)
        fonts = os.path.join(root, 'dump', 'fonts')
        return {'root': root, 'version': version, 'cfg': img.cfg, 'map': img.map, 'sources': img.paths, 'arc_offset': aoff,
                'arc_size': asize, 'ranges': len(ext), 'nrr': [(p, o, s) for p, o, s in nrr],
                'other_files': [p for p, _, _ in files if p.lower() != '/data.arc' and not p.lower().startswith('/.nrr/')],
                'fonts': sorted(n for n in os.listdir(fonts) if n.lower().endswith('.bfttf')) if os.path.isdir(fonts) else [],
                'source_bytes': sum(os.path.getsize(p) for p in img.paths)}
    finally:
        img.close()


# ---------------------------------------------------------------------------------------------------------- making
BFTTF_PLAIN_MAGIC = 0x7f9a0218
ARENA_FONTS = {'nintendo_udsg-r_std_003.ttf', 'nintendo_udsg-r_org_zh-cn_003.ttf', 'nintendo_udsg-r_ext_zh-cn_003.ttf',   # runtime/nnpl.c
               'nintendo_udjxh-db_zh-tw_003.ttf', 'nintendo_udsg-r_ko_003.ttf', 'nintendo_ext_003.ttf', 'nintendo_ext2_003.ttf'}


def decode_bfttf(data):
    """tools/extract/fw_fonts.py decode_bfttf (copied: that module needs the keys at import)"""
    words = struct.unpack('>%dI' % (len(data) // 4), data[:len(data) // 4 * 4])
    key = words[0] ^ BFTTF_PLAIN_MAGIC
    size = words[1] ^ key
    out = struct.pack('>%dI' % (len(words) - 2), *[w ^ key for w in words[2:]])
    return out[:size] if size <= len(out) else b''


def make(root, out, full=False, say=print):
    """Writes the linked dump in `out` (a folder of Arena's own). Returns shared_from.json's content."""
    info = check(root)
    part = out + '.part'
    if os.path.isdir(part):
        shutil.rmtree(part)
    os.makedirs(os.path.join(part, 'romfs', '.nrr'))
    img = RomImage(info['root'])
    try:
        ranges = []
        for off, n, src, soff in img.extents(info['arc_offset'], info['arc_size']):
            ranges.append((off - info['arc_offset'], n, soff, arc_file.ZEROS if src == LAYER_ZERO else src))
        arc_file.write_arcl(os.path.join(part, 'romfs', 'data.arcl'), info['arc_size'], img.paths, ranges)
        for p, o, s in info['nrr']:
            with open(os.path.join(part, 'romfs', '.nrr', os.path.basename(p)), 'wb') as f:
                f.write(img.read(o, s))
    finally:
        img.close()
    shutil.copytree(os.path.join(info['root'], 'dump', 'exefs'), os.path.join(part, 'exefs'))
    fonts_in, fonts_out = os.path.join(info['root'], 'dump', 'fonts'), os.path.join(part, 'sysdata', 'fonts')
    for n in info['fonts']:
        if n[:-6].lower() + '.ttf' not in ARENA_FONTS:
            continue
        with open(os.path.join(fonts_in, n), 'rb') as f:
            plain = decode_bfttf(f.read())
        if plain[:4] in (b'\x00\x01\x00\x00', b'OTTO', b'true', b'ttcf'):
            os.makedirs(fonts_out, exist_ok=True)
            with open(os.path.join(fonts_out, n[:-6] + '.ttf'), 'wb') as f:
                f.write(plain)
    rec = {'from': 'OpenSmash Ultimate', 'root': info['root'], 'version': info['version'], 'cfg': info['cfg'],
           'arc_size': info['arc_size'], 'ranges': len(ranges), 'made': time.strftime('%Y-%m-%d %H:%M:%S'),
           'fonts': len(os.listdir(fonts_out)) if os.path.isdir(fonts_out) else 0, 'other_romfs_files': info['other_files']}
    arcl = os.path.join(part, 'romfs', 'data.arcl')
    if full:
        say('   reading all of data.arc once through the link (%.1f GB)...' % (info['arc_size'] / 2 ** 30))
        t0, h = time.time(), hashlib.sha256()
        with arc_file.ArclFile(arcl) as f:
            pos = 0
            while pos < info['arc_size']:
                b = f.pread(pos, 64 << 20)
                if not b:
                    raise NotUsable('data.arc ends early at %d through the link' % pos)
                h.update(b)
                pos += len(b)
        rec['sha256'] = h.hexdigest()
        rec['full_read_secs'] = round(time.time() - t0, 1)
        want = ARC_SHA256.get(info['version'])
        if want and rec['sha256'] != want:
            shutil.rmtree(part, ignore_errors=True)
            raise NotUsable('its data.arc is not %s\'s (SHA-256 %s)' % (info['version'], rec['sha256']))
    with open(os.path.join(part, 'shared_from.json'), 'w', encoding='utf-8') as f:
        json.dump(rec, f, indent=1)
    if os.path.isdir(out):
        old = out + '.old_%d' % int(time.time())       # a link made before: kept aside (it has no game data; removed below)
        os.replace(out, old)
        shutil.rmtree(old, ignore_errors=True)
    os.replace(part, out)
    return rec


def status(dump):
    """[] when the linked dump in `dump` can be used now; else the problems"""
    arcl = os.path.join(dump, 'romfs', 'data.arcl')
    if not os.path.isfile(arcl):
        return ['%s is not there' % arcl]
    return arc_file.arcl_problems(arcl)


def main(argv):
    if not argv or argv[0] not in ('find', 'check', 'make', 'status'):
        print(__doc__)
        return 2
    cmd = argv[0]
    if cmd == 'find':
        for d in find(argv[1:]):
            print(d)
        return 0
    if cmd == 'status':
        bad = status(argv[1])
        print('usable' if not bad else '\n'.join(bad))
        return 1 if bad else 0
    try:
        if cmd == 'check':
            info = check(argv[1])
            info['nrr'] = len(info['nrr'])
            print(json.dumps(info, indent=1))
        else:
            rec = make(argv[1], os.path.abspath(argv[2]), full='--full' in argv)
            print(json.dumps(rec, indent=1))
    except NotUsable as e:
        print('not usable: %s' % e)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
