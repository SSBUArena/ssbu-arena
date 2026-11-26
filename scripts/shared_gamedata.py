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
    - (SSBU_SHARED_UNVERIFIED=refuse, or SSBU_SHARED_STRICT=0) the update's blocks were all confirmed by OpenSmash's installer
      (romfs.unverified.txt absent or empty). By default that file alone is no reason to refuse: OpenSmash's installer goes on
      with it ("the install goes on": a merged base, what most players have), and the data.arc checks below say whether the bytes
      Arena reads are right; with it, make() reads all of data.arc once (--full) whatever SSBU_SHARED_VERIFY says
    - main in its exefs is a game build Arena handles (scripts/setup.py GAME_BUILDS: OpenSmash is 13.0.5)
    - data.arc read through the map is whole (its header and file-system table) and has the size of that build's data.arc
    - (SSBU_SHARED_STRICT, default on) the cfg is one of the two forms Arena knows (layered with an SSBUROM1 map, or plain:
      one decrypted file and the RomFS offset in it), and data.arc read through the map is that build's own: its header and
      file-system table and 65 pieces of 64 KB have the recorded SHA-256 (ARC_TABLE_SHA256, ARC_SAMPLE_SHA256); the map as
      written is read back the runtime's way and checked the same; shared_from.json "checks": 2 (the runtime and the launcher
      refuse a link without it: one made by 0.2.64 is checked and made again). A RomFS that cannot be read as such is "not
      usable", never an error. SSBU_SHARED_STRICT=0: the checks of 0.2.64 only.
    - with --full (the installer's default when strict; SSBU_SHARED_VERIFY=quick skips it): every byte of data.arc is read once
      through the map and must have the build's SHA-256 (ARC_SHA256; recorded in shared_from.json)

    python scripts/shared_gamedata.py find                      the OpenSmash installs this PC remembers
    python scripts/shared_gamedata.py check <opensmash folder>  the checks above, nothing written
    python scripts/shared_gamedata.py make <opensmash folder> <out folder> [--full]
    python scripts/shared_gamedata.py status <dump folder>      a linked dump still usable? (exit 1 if not)
    python scripts/shared_gamedata.py relink <build folder> [<opensmash folder>]   make <build>\\dump_shared again (JSON line)
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
ARC_SHA256 = {'13.0.5': 'ae9ebecfc570933cd4d2d85595b231de59ba1c9d2990e8eb07a5ec421da03a14',
              '13.0.4': 'ef615e671e1d4907f0a707f2eb13e3cb0c8539c4e67ac51c89ef547c22b82995'}     # (13.0.4: a dump made by the installer)
LAYER_MAGIC = b'SSBUROM1'
LAYER_ZERO = 0xFFFFFFFF

# (fix/shared-install) SSBU_SHARED_STRICT (default 1; 0 = the checks of 0.2.64 only): before anything is linked, the game data
# read through the other install must BE the data.arc of the game build its exefs says, not only have its size:
#   - ARC_TABLE_SHA256: SHA-256 of data.arc's header (0x38 bytes) + its whole file-system table (~13 MB)
#   - ARC_SAMPLE_SHA256: SHA-256 of ARC_SAMPLES pieces of 64 KB spread evenly over data.arc (arc_samples; ~4 MB read)
# Hashes only (nothing of the game). Measured 2026-11-23: 13.0.5 through the link to an OpenSmash install whose data.arc as a
# whole has ARC_SHA256's hash, 13.0.4 on a dump made by the installer. A build missing here is not linked when strict.
# A layout of OpenSmash's that Arena does not know (another cfg form, a layer map of another version, game data read from an
# encrypted file, a map that pieces the data together wrongly) then gives a clear "not shared" instead of a game reading
# wrong bytes. STRICT also makes the check pick romfs.cfg (the data the game reads) over romfs_vanilla.cfg unless romfs.cfg
# names a mods map, and reads the plain form of romfs.cfg (one decrypted file: a game dumped with its update merged in).
CHECKS_VERSION = 2              # shared_from.json 'checks': a link made with fewer checks is checked again (SharedData.cs)
ARC_TABLE_SHA256 = {'13.0.4': '9a6d6f5795f4ee5142412019be4373db199b253d452c70ad1fdccb7807a8251b',
                    '13.0.5': '55c762af22dc5a922f0f464cdb0b19a18f0f72c290cac1977bf4a3bb5620081e'}
ARC_SAMPLES = 64
ARC_SAMPLE_SHA256 = {'13.0.4': '4b8a47ae3e7bd590e5de9c99a0d1513eedf73ca765f66248ffcb2d14083321c2',
                     '13.0.5': 'c8fc72769283e7d769605899691af176606d08cf6e8c5954f9b6f933c5e4c26b'}


def strict():
    return os.environ.get('SSBU_SHARED_STRICT', '1').strip() != '0'


# (fix/launcher-0277) SSBU_SHARED_UNVERIFIED: 'check' (default) or 'refuse' (as 0.2.76). OpenSmash writes
# dump\modules\romfs.unverified.txt when some 16 KB blocks of its updated RomFS could not be confirmed against the update's hashes,
# and goes on (tools/update.py: a warning, not an error); the blocks may be padding or files other than data.arc. 0.2.76 refused
# any such install (4 reports, 3 players, who then had no game data of their own). Now the marker is logged and the data.arc checks
# decide: when strict, the fingerprint (header + file table + samples) in check() and the SHA-256 of every byte in make(), forced
# on with the marker. Without strict (SSBU_SHARED_STRICT=0) nothing else proves the data: refused as before.
def unverified_refused():
    return os.environ.get('SSBU_SHARED_UNVERIFIED', 'check').strip().lower() == 'refuse' or not strict()


def unverified_blocks(root):
    """the blocks romfs.unverified.txt lists (lines that start with a digit, as OpenSmash's runtime counts them), 0 without it"""
    unv = os.path.join(root, 'dump', 'modules', 'romfs.unverified.txt')
    if not os.path.isfile(unv):
        return 0
    with open(unv, encoding='utf-8', errors='replace') as f:
        return sum(1 for l in f if l[:1].isdigit())


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
        cfg = self._pick_cfg(mod) if strict() else os.path.join(mod, 'romfs_vanilla.cfg')
        if not os.path.isfile(cfg):
            cfg = os.path.join(mod, 'romfs.cfg')
        self.cfg = cfg
        self.plain = None
        if os.path.isfile(os.path.join(mod, 'romfs.crypt')):
            raise NotUsable('its game data is read in place from the encrypted game file (romfs.crypt): only a decrypted '
                            'copy can be shared')
        try:
            with open(cfg, encoding='utf-8-sig') as f:
                lines = [l.strip() for l in f.read().splitlines() if l.strip()]
        except OSError as e:
            raise NotUsable('no game data cfg (%s)' % e)
        if strict() and len(lines) == 2 and lines[0] != 'layered' and lines[1].isdigit():
            self._plain(lines, mod, root)          # (the plain form: "<file>\n<RomFS offset>", one decrypted file)
            return
        if not lines or lines[0] != 'layered' or len(lines) < 3:
            raise NotUsable('%s is not a form of OpenSmash\'s game data this Arena knows (the layered form: base + update%s)'
                            % (cfg, ', or the plain form' if strict() else ''))
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
            raise NotUsable('%s is not a layer map of a version this Arena knows (%r)' % (self.map, d[:8]))
        self.size, n, _ = struct.unpack_from('<QII', d, 8)
        if strict() and (len(d) != 24 + n * 24 or not n):
            raise NotUsable('%s: %d bytes for %d layers (a layer map of another version?)' % (self.map, len(d), n))
        self.layers = [(at, src, off) for at, off, src, _ in struct.iter_unpack('<QQII', d[24:24 + n * 24])]
        self._open()

    def _plain(self, lines, mod, root):
        """romfs.cfg's plain form (OpenSmash's nca.py romfs-cfg): the RomFS image lies whole in one file from an offset (the game
        decrypted into dump\\modules, a dump with its update merged in)"""
        p = self._local(lines[0], mod)
        if not inside(p, root):
            raise NotUsable('%s is outside the OpenSmash folder: not read' % p)
        if not os.path.isfile(p):
            raise NotUsable('%s is missing' % p)
        off = int(lines[1])
        size = os.path.getsize(p)
        if off >= size:
            raise NotUsable('%s: the RomFS offset %d is past the end of %s' % (self.cfg, off, p))
        self.plain, self.map, self.paths = p, None, [p]
        self.size, self.layers = size - off, [(0, 0, off)]
        self._open()

    def _open(self):
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
            raise NotUsable('no RomFS image behind %s' % self.cfg)
        self.dirs, self.ftab, self.data = self.read(h[3], h[4]), self.read(h[7], h[8]), h[9]

    @staticmethod
    def _pick_cfg(mod):
        """(strict) the cfg of the game's own data: romfs.cfg, which the game reads, unless it names a mods map (OpenSmash's
        tools/mods.py); then romfs_vanilla.cfg, the copy mods.py kept. (0.2.64 took romfs_vanilla.cfg first: a copy kept from
        the first time mods were applied, possibly older than romfs.cfg.)"""
        cfg, vanilla = os.path.join(mod, 'romfs.cfg'), os.path.join(mod, 'romfs_vanilla.cfg')
        try:
            with open(cfg, encoding='utf-8-sig') as f:
                lines = [l.strip() for l in f.read().splitlines() if l.strip()]
            modded = len(lines) > 1 and lines[0] == 'layered' and 'mods' in os.path.basename(lines[1].replace('\\', '/')).lower()
        except OSError:
            modded = True
        return vanilla if modded and os.path.isfile(vanilla) else cfg

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


def arc_samples(size):
    """the offsets of the ARC_SAMPLES pieces of 64 KB (arc_fingerprint): spread evenly, 64 KB aligned, the last one at the end"""
    step = size // ARC_SAMPLES
    return [min(i * step, size - 0x10000) & ~0xffff for i in range(ARC_SAMPLES)] + [size - 0x10000]


def arc_fingerprint(read, size):
    """(SHA-256 of data.arc's header + file-system table, SHA-256 of its sample pieces): what ARC_TABLE_SHA256 and
    ARC_SAMPLE_SHA256 hold for each game build. read(offset, n) reads data.arc; ~17 MB is read."""
    hdr = read(0, 0x38)
    fs = struct.unpack('<7Q', hdr)[4]
    comp = struct.unpack('<4I', read(fs, 16))[2]
    table = hashlib.sha256(hdr + read(fs, 16 + comp)).hexdigest()
    h = hashlib.sha256()
    for o in arc_samples(size):
        h.update(read(o, 0x10000))
    return table, h.hexdigest()


def check_content(version, read, size):
    """(strict) data.arc as read is the build's own: header + file-system table and the sample pieces (arc_fingerprint)"""
    table, sample = arc_fingerprint(read, size)
    want_t, want_s = ARC_TABLE_SHA256.get(version), ARC_SAMPLE_SHA256.get(version)
    if not want_t or not want_s:
        raise NotUsable('Arena has no fingerprint of %s\'s data.arc to check it against: not shared' % version)
    if table != want_t:
        raise NotUsable('its data.arc read as Arena reads it is not %s\'s (the file-system table differs): a layout of '
                        'OpenSmash\'s game data this Arena does not read right, or other data' % version)
    if sample != want_s:
        raise NotUsable('its data.arc read as Arena reads it is not %s\'s (the file data differs at some of %d places checked): '
                        'a layout of OpenSmash\'s game data this Arena does not read right, or data OpenSmash could not '
                        'confirm' % (version, ARC_SAMPLES + 1))
    return table, sample


def check(root):
    """-> dict describing what would be shared; raises NotUsable with the reason. (strict) Anything that cannot be read as the
    layout this Arena knows (a RomFS table out of its bounds, a short read) is NotUsable too: never an error that stops the
    installer or the launcher's link."""
    if not strict():
        return _check(root)
    try:
        return _check(root)
    except NotUsable:
        raise
    except (struct.error, ValueError, IndexError, OSError, UnicodeError, MemoryError, OverflowError) as e:
        raise NotUsable('its game data cannot be read as the layout of OpenSmash\'s this Arena knows (%s: %s)' % (type(e).__name__, e))


def _check(root):
    root = os.path.abspath(root)
    if not is_opensmash(root):
        raise NotUsable('%s is not an OpenSmash Ultimate install (no tools\\install_windowsmash.ps1 / dump\\modules)' % root)
    unv = os.path.join(root, 'dump', 'modules', 'romfs.unverified.txt')
    unconfirmed = unverified_blocks(root)
    if unconfirmed and unverified_refused():
        raise NotUsable('OpenSmash could not confirm some blocks of its game data (%s): not shared' % unv)
    if unconfirmed:
        print('note: OpenSmash could not confirm %d blocks of 16 KB of its game data (%s); not refused for that: data.arc is '
              'checked instead' % (unconfirmed, unv), file=sys.stderr)
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
        content = check_content(version, lambda o, n: img.read(aoff + o, n), asize) if strict() else None
        nrr = [f for f in files if f[0].lower().startswith('/.nrr/') and f[0].lower().endswith('.nrr')]
        if not nrr:
            raise NotUsable('no .nrr files in its RomFS image')
        ext = img.extents(aoff, asize)
        fonts = os.path.join(root, 'dump', 'fonts')
        return {'root': root, 'version': version, 'cfg': img.cfg, 'map': img.map, 'sources': img.paths, 'arc_offset': aoff,
                'arc_size': asize, 'ranges': len(ext), 'nrr': [(p, o, s) for p, o, s in nrr],
                'other_files': [p for p, _, _ in files if p.lower() != '/data.arc' and not p.lower().startswith('/.nrr/')],
                'fonts': sorted(n for n in os.listdir(fonts) if n.lower().endswith('.bfttf')) if os.path.isdir(fonts) else [],
                'source_bytes': sum(os.path.getsize(p) for p in img.paths), 'form': 'plain' if img.plain else 'layered',
                'checks': CHECKS_VERSION if content else 1, 'unverified_blocks': unconfirmed}
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
    if info.get('unverified_blocks') and not full:
        # (fix/launcher-0277) OpenSmash left blocks unconfirmed: the link only after every byte of data.arc has the build's SHA-256
        say('   OpenSmash could not confirm %d blocks of its game data: all of data.arc is checked' % info['unverified_blocks'])
        full = True
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
    rec = {'checks': info['checks'], 'from': 'OpenSmash Ultimate', 'root': info['root'], 'version': info['version'], 'cfg': info['cfg'],
           'arc_size': info['arc_size'], 'ranges': len(ranges), 'made': time.strftime('%Y-%m-%d %H:%M:%S'),
           'fonts': len(os.listdir(fonts_out)) if os.path.isdir(fonts_out) else 0, 'other_romfs_files': info['other_files'],
           # OpenSmash's game data cfg and layer map as they were: a re-extraction or update rewrites them (status, the launcher)
           'stamps': {p: list(arc_file.file_stamp(p)) for p in (info['cfg'], info['map']) if p},
           'form': info['form']}
    arcl = os.path.join(part, 'romfs', 'data.arcl')
    if strict():                       # the map as written, read the way the runtime reads it (arclink.c): the same fingerprint
        with arc_file.ArclFile(arcl) as f:
            try:
                check_content(info['version'], f.pread, info['arc_size'])
            except NotUsable as e:
                f.close()
                shutil.rmtree(part, ignore_errors=True)
                raise NotUsable('the link as written: %s' % e)
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
    """[] when the linked dump in `dump` can be used now; else the problems. Cheap (sizes and dates only): the launcher's check
    before every start does the same in C# (SharedData.cs)."""
    arcl = os.path.join(dump, 'romfs', 'data.arcl')
    if not os.path.isfile(arcl):
        return ['%s is not there' % arcl]
    bad = arc_file.arcl_problems(arcl)
    try:
        with open(os.path.join(dump, 'shared_from.json'), encoding='utf-8') as f:
            rec = json.load(f)
    except (OSError, ValueError):
        rec = {}
    stamps = rec.get('stamps') or {}
    if strict() and (rec.get('checks') or 1) < CHECKS_VERSION:
        bad.append('the link was made without the content check of this Arena (it is checked and made again)')
    for p, st in stamps.items():
        try:
            if list(arc_file.file_stamp(p)) != list(st):
                bad.append('%s changed since the link was made (size or date)' % p)
        except OSError:
            bad.append('%s is not there any more' % p)
    return bad


def relink(build, root=None, say=print):
    """Makes <build>\\dump_shared again (the quick map, no compile): from the OpenSmash folder given, else the one the link was
    made from, else the one this PC remembers. -> shared_from.json's content; NotUsable with the reason."""
    out = os.path.join(build, 'dump_shared')
    roots = [root] if root else []
    try:
        with open(os.path.join(out, 'shared_from.json'), encoding='utf-8') as f:
            roots.append(json.load(f).get('root'))
    except (OSError, ValueError):
        pass
    cands = find([r for r in roots if r])
    if not cands:
        raise NotUsable('no OpenSmash Ultimate install found (it was moved or removed)')
    try:                                     # the game this install was built for: the link must give the same build
        with open(os.path.join(build, 'state.json'), encoding='utf-8') as f:
            built = json.load(f).get('game_version')
    except (OSError, ValueError):
        built = None
    why = None
    for r in cands:
        try:
            got = check(r)['version']
            if built and got != built:
                raise NotUsable('it has the game update %s, this install is built for %s (the setup builds it again)' % (got, built))
            rec = make(r, out, say=say)
            bad = status(out)
            if bad:
                raise NotUsable('; '.join(bad))
            return rec
        except NotUsable as e:
            why = '%s: %s' % (r, e)
    raise NotUsable(why)


def main(argv):
    if not argv or argv[0] not in ('find', 'check', 'make', 'status', 'relink'):
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
        if cmd == 'relink':                     # relink <build> [<OpenSmash folder>]: one JSON line for the launcher
            try:
                rec = relink(os.path.abspath(argv[1]), argv[2] if len(argv) > 2 else None, say=lambda s: None)
                print(json.dumps({'ok': True, 'root': rec['root'], 'version': rec['version']}))
                return 0
            except NotUsable as e:
                print(json.dumps({'ok': False, 'why': str(e)}))
                return 1
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
