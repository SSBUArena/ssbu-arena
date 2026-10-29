"""Detachable stage backgrounds (0.2.12): the background parts of the competitive stages, taken from YOUR data.arc and
written as files you can keep, replace or switch off. Nothing of the game is in this file; the output is game content
derived from your dump and stays on your PC (build\\stages, git-ignored, never in a release package).

    python tools/stages/extract_backgrounds.py [--arc <data.arc>] [--out <build>/stages] [--stage <name> ...]

Output, per stage (folder names as in the archive: battlefield, battlefield_s, end, poke_stadium2, animal_village,
animal_city):

    <out>/LEGGIMI.txt                                   for the community: what the files are, how to replace them
    <out>/<stage>/parti.txt                             every model part of the stage: playable or background, label,
                                                        files, textures
    <out>/<stage>/original/stage/<stage>/normal/model/<part>/...
                                                        the background parts as the game has them (untouched copies;
                                                        never served: the game reads its own archive for these)
    <out>/<stage>/off/stage/<stage>/normal/model/<part>/model.numshb
                                                        the same meshes with every index set to 0 (all triangles
                                                        degenerate: nothing is drawn). SAME SIZE as the original.
    <out>/<stage>/custom/                               empty: the files you put here (same relative path as in
                                                        original/) replace the game's

The runtime serves one of the two folders through the loose-file layer (runtime/loose_files.c): SSBU_STAGE_BG=off or
SSBU_STAGE_BG=custom; unset / "original" = the game as it is. A served file must have EXACTLY the size of the
archive's copy (else it is skipped and logged): the game's buffers and its heap then stay identical between two
players with different backgrounds (rollback\\stage_backgrounds_design.md, determinism).

Names. The archive keeps only hashes (hash40 = crc32 | length << 32). The files of a part have the standard model
names (model.numdlb, model.numshb, ...) and texture names taken from the part's own material file; the part folders'
names are not known, so they are written as "0x<10 hex digits>" (the hash40 of the folder name), which the
loose-file layer joins to the path through the crc32 combination. A file whose name is not found is written as
"0x<hash40 of the name>.<ext>" in the same way. Every path written is checked against the archive's own hash.
"""
import argparse
import json
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools', 'nro'))
sys.path.insert(0, os.path.join(ROOT, 'fighter_sdk', 'tools'))

STAGES = ['battlefield', 'battlefield_s', 'end', 'poke_stadium2', 'animal_village', 'animal_city']
M40 = 0xffffffffff

# The PLAYABLE parts of each stage (folder name hashes under stage/<stage>/normal/model): the main body, the platforms,
# the parts that change with a transformation, the shadow-only meshes. Every other part with a mesh is BACKGROUND.
# Chosen from the parts' mesh names and bounding boxes in the archive (no game content here, only name hashes); the
# stages marked "check" have parts placed by the stage's joints at run time, so their list wants a look in game.
TRANSITION = 0x146372909d          # every stage: the stage's own silhouette used by the stage transition
PLAYABLE = {
    'battlefield':    {0x15bfa1b388, 0x12bf759851, 0x16ce2efebe, TRANSITION},
    'battlefield_s':  {0x15bfa1b388, 0x12bf759851, 0x16ce2efebe, TRANSITION},
    'end':            {0x806ae66ff, 0x13fbef9d43, 0x1368726318, 0x12bf759851, TRANSITION},
    'poke_stadium2':  {0x12db03f4fe, 0x121e829983, 0xef6c66e97, 0x16fe6c4fcf, 0x10555db0c9, 0x120e6baab0,
                       0x14d5ce671a, 0x102ac7e828, 0x10ca9cd1a3, 0x12963e23fa, 0x1249ee0a19, 0x103bc02e8b,
                       0xc924d3c58, TRANSITION},
    'animal_village': {0x11cb38ced2, 0x1324f918c0, TRANSITION},
    'animal_city':    {0x1ae09d28b4, 0x148ecbe207, 0x1aa41a6c97, 0x147b94986b, TRANSITION},
}
CHECKED = {'battlefield', 'battlefield_s', 'end'}
STD_NAMES = ['model.numdlb', 'model.numshb', 'model.numatb', 'model.nusktb', 'model.numshexb', 'model.nuhlpb',
             'model.nusrcmdlb', 'model.xmb', 'model.adjb', 'model.nuanmb', 'model.nuflxb']
EXTS = ['nutexb', 'numdlb', 'numshb', 'numatb', 'nusktb', 'nuanmb', 'numshexb', 'nuhlpb', 'nusrcmdlb', 'xmb', 'adjb',
        'nuflxb', 'prc', 'stprm', 'stdat', 'lvd', 'shpcanim', 'nus3bank', 'nus3audio', 'tonelabel']
INDEX_FIELD = 0xc0                 # .numshb: the index buffer's {relative offset, size} (fighter_sdk/tools/brawl/ssbh.py MESH)


def hash40(s):
    b = s.lower().encode()
    return (len(b) << 32) | zlib.crc32(b)


def crc_shift(x, nbytes):
    for _ in range(nbytes * 8):
        x = (x >> 1) ^ (0xedb88320 & -(x & 1))
    return x


def path_hash(parts):
    """hash40 of "/".join(parts), where a part may be an int (the hash40 of that part's text, text unknown)."""
    crc, n = 0, 0
    for i, p in enumerate(parts):
        if i:
            crc = zlib.crc32(b'/', crc); n += 1
        if isinstance(p, int):
            ln = p >> 32
            crc = crc_shift(crc, ln) ^ (p & 0xffffffff); n += ln
        else:
            b = p.lower().encode()
            crc = zlib.crc32(b, crc); n += len(b)
    return ((n & 0xff) << 32) | crc


class Tree:
    """The archive's directory rows (data.arc tables, as tools/nro/arc_extract.py reads them)."""
    def __init__(self, arc):
        self.A = arc
        self.dirs = {}
        for i in range(arc.n_folder):
            q, = struct.unpack_from('<Q', arc.t, arc.o_dirhash + i * 8)
            self.dirs[q & M40] = q >> 40
        self.ext = {hash40(e): e for e in EXTS}

    def dirinfo(self, i):
        t, o = self.A.t, self.A.o_dirinfo + i * 52
        path, name, parent = struct.unpack_from('<3Q', t, o)
        fstart, fcount, cstart, ccount = struct.unpack_from('<4I', t, o + 0x20)
        return dict(path=path & M40, name=name & M40, parent=parent & M40, fstart=fstart, fcount=fcount,
                    cstart=cstart, ccount=ccount, index=i)

    def children(self, d):
        for c in range(d['ccount']):
            q, = struct.unpack_from('<Q', self.A.t, self.A.o_child + (d['cstart'] + c) * 8)
            yield self.dirinfo(q >> 40)

    def files(self, d):
        for k in range(d['fcount']):
            fp_i, _ii, _itd, _fl = struct.unpack_from('<4I', self.A.t, self.A.o_info + (d['fstart'] + k) * 16)
            (ph, _), (xh, _), (par, _), (nm, _) = self.A.file_path(fp_i)
            yield dict(path=ph, ext=self.ext.get(xh, '0x%010x' % xh), parent=par, name=nm, pi=fp_i)


def texture_names(numatb):
    from brawl import ssbh
    out = set()
    _m, _v, mat = ssbh.parse(numatb)
    for e in mat['entries']:
        for a in e['attributes']:
            v = a['param']
            if isinstance(v, tuple) and v[0] == 0xB and v[1]:
                out.add(v[1].lower() + '.nutexb')
    return out


def mesh_info(numshb):
    from brawl import ssbh
    _m, _v, mesh = ssbh.parse(numshb)
    cx, cy, cz, r = struct.unpack_from('<4f', mesh['bounding'], 0)
    names = sorted(set((o['name'] or '').strip('_').replace('Shape', '') for o in mesh['objects']))
    return dict(centre=(cx, cy, cz), radius=r, objects=len(mesh['objects']), names=names)


def blank_mesh(numshb):
    """The mesh file with its index buffer zeroed (every triangle has three equal corners). Same size, same layout."""
    rel, size = struct.unpack_from('<QQ', numshb, INDEX_FIELD)
    start = INDEX_FIELD + rel
    if not rel or start + size > len(numshb):
        raise ValueError('index buffer outside the file')
    from brawl import ssbh
    if ssbh.parse(numshb)[2]['index_buffer'] != numshb[start:start + size]:
        raise ValueError('index buffer field does not match the parsed mesh')
    out = bytearray(numshb)
    out[start:start + size] = bytes(size)
    return bytes(out)


def label(info):
    if not info:
        return '(nessuna mesh)'
    s = ', '.join(info['names'][:4])
    return s + (', ...' if len(info['names']) > 4 else '')


def do_stage(A, tree, stage, out):
    top = tree.dirs.get(hash40('stage/%s/normal' % stage))
    if top is None:
        raise SystemExit('stage/%s/normal: not in the archive' % stage)
    model = next((c for c in tree.children(tree.dirinfo(top)) if c['name'] == hash40('model')), None)
    if model is None:
        raise SystemExit('stage/%s/normal/model: not in the archive' % stage)
    keep = PLAYABLE.get(stage, set())
    base = os.path.join(out, stage)
    rows, n_files, n_bytes, n_off = [], 0, 0, 0
    for part in tree.children(model):
        files, regional = [], []
        for f in tree.files(part):
            rec = A.file_record(f['pi'])
            # a regional file (signs with text): region 0 has no data; the game picks its region's row. Left out.
            (regional if (rec['info_flags'] >> 15) & 3 and not rec['decomp_size'] else files).append(f)
        data = {f['pi']: A.read(A.file_record(f['pi'])) for f in files}
        names = {hash40(n): n for n in STD_NAMES}
        for f in files:
            if f['ext'] == 'numatb':
                names.update({hash40(n): n for n in texture_names(data[f['pi']])})
        mesh = next((f for f in files if f['ext'] == 'numshb'), None)
        info = mesh_info(data[mesh['pi']]) if mesh else None
        if part['name'] in keep:
            kind = 'gioco'
        elif not mesh:
            kind = 'senza mesh'
        else:
            kind = 'sfondo'
        folder = '0x%010x' % part['name']
        written = []
        for f in files:
            fname = names.get(f['name']) or ('0x%010x.%s' % (f['name'], f['ext']))
            rel = ['stage', stage, 'normal', 'model', part['name'], names.get(f['name']) or f['name']]
            if path_hash(rel) != f['path']:
                raise SystemExit('%s/%s/%s: the path does not give the archive hash' % (stage, folder, fname))
            written.append((fname, len(data[f['pi']]), f))
        tex = sorted(n for n, _s, f in written if f['ext'] == 'nutexb')
        rows.append((folder, kind, info, written, tex, len(regional)))
        if kind != 'sfondo':
            continue
        odir = os.path.join(base, 'original', 'stage', stage, 'normal', 'model', folder)
        os.makedirs(odir, exist_ok=True)
        for fname, size, f in written:
            with open(os.path.join(odir, fname), 'wb') as fp:
                fp.write(data[f['pi']])
            n_files += 1; n_bytes += size
        if mesh:
            ddir = os.path.join(base, 'off', 'stage', stage, 'normal', 'model', folder)
            os.makedirs(ddir, exist_ok=True)
            with open(os.path.join(ddir, 'model.numshb'), 'wb') as fp:
                fp.write(blank_mesh(data[mesh['pi']]))
            n_off += 1
    os.makedirs(os.path.join(base, 'custom'), exist_ok=True)
    with open(os.path.join(base, 'parti.txt'), 'w', encoding='utf-8') as fp:
        fp.write('Stage %s: parti del modello (stage/%s/normal/model/<cartella>)\n' % (stage, stage))
        fp.write('Classificazione %s.\n' % ('verificata in gioco' if stage in CHECKED else 'DA VERIFICARE in gioco (parti messe in scena dai giunti dello stage)'))
        fp.write('"gioco" = piano di gioco e piattaforme (mai toccate); "sfondo" = copiate in original/ e svuotate in off/.\n\n')
        for folder, kind, info, written, tex, nreg in sorted(rows, key=lambda r: (r[1] != 'sfondo', r[0])):
            geo = ''
            if info:
                geo = ' centro (%.0f, %.0f, %.0f) raggio %.0f, %d oggetti' % (info['centre'] + (info['radius'], info['objects']))
            fp.write('%s  [%s]%s\n    mesh: %s\n' % (folder, kind, geo, label(info)))
            fp.write('    file: %s\n' % ', '.join('%s (%d)' % (n, s) for n, s, _f in written))
            if tex:
                fp.write('    texture: %s\n' % ', '.join(tex))
            fp.write('\n')
    print('%s: %d parts (%d background), %d files / %.1f MB in original/, %d blank meshes in off/' % (
        stage, len(rows), sum(1 for r in rows if r[1] == 'sfondo'), n_files, n_bytes / 1e6, n_off))
    return dict(stage=stage, parts=len(rows), background=sum(1 for r in rows if r[1] == 'sfondo'), files=n_files,
                bytes=n_bytes, checked=stage in CHECKED)


README = """SFONDI DEGLI STAGE STACCATI (SSBU Arena)
=======================================

Questa cartella e' generata sul tuo PC dal TUO dump del gioco (tools/stages/extract_backgrounds.py).
Non va condivisa cosi' com'e': contiene file del gioco. Condividi solo i TUOI file modificati.

Per ogni stage:
  parti.txt   elenco delle parti del modello: "gioco" (piano e piattaforme: non si toccano) o "sfondo".
  original/   le parti di sfondo come le ha il gioco. Non vengono mai caricate da qui: sono la copia di
              riferimento da cui partire.
  off/        le stesse mesh "svuotate" (tutti gli indici a zero: non si disegna niente). Generate.
  custom/     vuota. I file che metti qui sostituiscono quelli del gioco.

Quale cartella usa il gioco: variabile SSBU_STAGE_BG all'avvio.
  (non impostata) o original   il gioco com'e'
  off                          sfondo spento: si vedono solo piano di gioco, piattaforme, personaggi, effetti
  custom                       i tuoi file in custom/

Come sostituire un file:
  1. trova la parte in parti.txt (cartella 0x...; le mesh danno un'idea di cosa sia: sky, cloud, sun, ...)
  2. copia il file da original/ in custom/ con lo STESSO percorso, es.
       original/stage/battlefield/normal/model/0x0ed57c000d/xxx.nutexb
    -> custom/stage/battlefield/normal/model/0x0ed57c000d/xxx.nutexb
  3. modificalo. Per spegnere solo una parte copia il suo model.numshb da off/ in custom/.

REGOLA: il file deve avere ESATTAMENTE la stessa dimensione in byte dell'originale. Un file di dimensione
diversa viene ignorato (lo dice il log, riga "[loose] ... size differs"). Cosi' la memoria del gioco resta
identica a quella di un giocatore con lo sfondo originale e il rollback online non si desincronizza.
Per le texture (.nutexb) vuol dire: stesse dimensioni, stesso formato, stessi livelli mip.

Che file sono:
  model.numshb   le mesh (vertici e indici)            model.numatb  i materiali (shader e texture usate)
  model.numdlb   l'elenco mesh -> materiale             model.nusktb  lo scheletro della parte
  *.nutexb       le texture (formato nativo Switch, block-linear; di solito compresse BC1/BC3/BC7)
  model.numshexb, model.nuhlpb, model.nusrcmdlb, model.xmb, *.nuanmb   dati accessori, animazioni

Cosa NON cambia: gli effetti (particelle) dello sfondo e la logica dello stage (animazioni, piattaforme
mobili, trasformazioni) restano quelli del gioco. Lo sfondo e' solo grafica locale: l'altro giocatore
vede il suo.
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--arc', default=None, help='data.arc (default: SSBU_ARC, or the romfs of build/state.json)')
    ap.add_argument('--out', default=os.path.join(ROOT, 'build', 'stages'))
    ap.add_argument('--stage', action='append', help='only these stages (default: the competitive six)')
    a = ap.parse_args()
    arc = a.arc or os.environ.get('SSBU_ARC')
    if not arc:
        st = json.load(open(os.path.join(ROOT, 'build', 'state.json')))
        arc = os.path.join(st['dump']['romfs'], 'data.arc')
    os.environ['SSBU_ARC'] = arc
    import arc_extract as ax
    A = ax.Arc(arc)
    tree = Tree(A)
    os.makedirs(a.out, exist_ok=True)
    report = [do_stage(A, tree, s, a.out) for s in (a.stage or STAGES)]
    with open(os.path.join(a.out, 'LEGGIMI.txt'), 'w', encoding='utf-8') as fp:
        fp.write(README)
    with open(os.path.join(a.out, 'stages.json'), 'w') as fp:
        json.dump(dict(version=1, stages=report), fp, indent=1)


if __name__ == '__main__':
    main()
