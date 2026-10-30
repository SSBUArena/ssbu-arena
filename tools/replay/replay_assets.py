#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""The launcher's replay viewer draws the game's own fighters and stages under the hit boxes. This tool makes what it reads from
YOUR data.arc, into build/replay_assets: game content, never in this repository and never uploaded. Made per replay, only what is
missing: the models of its fighters and stage and the animations its fighters really use.

    python tools/replay/replay_assets.py --arc <data.arc> --out <build>/replay_assets --replay <file.ssbv>

<fighter>_c<NN>/model.rvm     bones, meshes, skinning (layout: write_model), tex<N>.png
<fighter>/<motion hash>.rva   one motion's bone tracks and the meshes it switches (layout: write_motion)
stage_<id>/model.rvm          the stage's playable parts, already placed (no bones)
replays/<replay name>.txt     what the launcher loads: one line "fighter <slot> <model folder> <motions folder> <scale>" per fighter, "stage <folder>"
"""
import argparse
import hashlib
import io
import os
import struct
import sys
import zlib

import numpy as np

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools', 'nro'))
sys.path.insert(0, os.path.join(ROOT, 'tools', 'stages'))
sys.path.insert(0, os.path.join(ROOT, 'tools', 'replay'))
sys.path.insert(0, os.path.join(ROOT, 'fighter_sdk', 'tools'))

M40 = 0xffffffffff
TEX_MAX = 512                # textures are made smaller than this: a replay's fighter is small on the screen
TEX_PARAM, TEX_BAKED = 0x5c, 0x61   # the material's colour map: Texture0, and Texture5 on the stages whose maps are baked (elsewhere it is a glow)
BAKED_STAGES = ('battlefield', 'battlefield_s', 'end')
FIGHTERS = '''mario donkey link samus samusd yoshi kirby fox pikachu luigi ness captain purin peach daisy koopa popo nana sheik zelda
mariod pichu falco marth lucina younglink ganon mewtwo roy chrom gamewatch metaknight pit pitb szerosuit wario snake ike pzenigame
pfushigisou plizardon diddy lucas sonic dedede pikmin lucario robot toonlink wolf murabito rockman wiifit rosetta littlemac gekkouga
palutena pacman reflet shulk koopajr duckhunt ryu ken cloud kamui bayonetta inkling ridley simon richter krool shizue gaogaen packun
jack brave buddy dolly master tantan pickel edge eflame elight demon trail miifighter miiswordsman miigunner'''.split()
# a select screen's entry that is not one fighter's folder (ui_chara -> the folders)
UI_FOLDER = {'ice_climber': 'popo', 'eflame_first': 'eflame', 'elight_first': 'elight', 'pfushigisou': 'pfushigisou'}


def hash40(s):
    return (len(s) << 32) | zlib.crc32(s.lower().encode())


class Files:
    """the files of one folder of the archive, by name hash"""

    def __init__(self, arc, tree, group, folder):
        """a fighter's files are kept in the archive by colour (the group "fighter/<name>/c<NN>"), each with its folder as its parent"""
        self.arc, self.names = arc, {}
        key, parent = hash40(group), hash40(folder + '/')
        if key not in tree.dirs:
            return

        def walk(d):
            for f in tree.files(d):
                if f['parent'] == parent:
                    self.names[f['name']] = f['pi']
            for c in tree.children(d):
                walk(c)
        walk(tree.dirinfo(tree.dirs[key]))

    def get(self, name):
        pi = self.names.get(hash40(name) if isinstance(name, str) else name)
        return None if pi is None else bytes(self.arc.read(self.arc.file_record(pi)))


# ------------------------------------------------------------------ textures
def texture_png(data, out_path, shader=''):
    from brawl import nutexb
    from PIL import Image
    f = nutexb.read_footer(data)
    w, h, fmt = f['width'], f['height'], f['format']
    bc = {0x80: (1, 8), 0x85: (1, 8), 0xa0: (3, 16), 0xa5: (3, 16), 0xb0: (4, 8), 0xb5: (4, 8), 0xc0: (5, 16), 0xc5: (5, 16),
          0xe0: (7, 16), 0xe5: (7, 16)}.get(fmt)
    if bc:
        bw, bh = (w + 3) // 4, (h + 3) // 4
        raw = nutexb.swizzle(data, bw, bh, bc[1], to_linear=True)
        im = Image.frombytes('RGBA', (bw * 4, bh * 4), bytes(raw[:bw * bh * bc[1]]), 'bcn', bc[0]).crop((0, 0, w, h))
    elif fmt in (0x00, 0x01, 0x04, 0x05, 0x06, 0x07) or fmt in (0x50, 0x55):
        raw = nutexb.swizzle(data, w, h, 4, to_linear=True)
        im = Image.frombytes('RGBA', (w, h), bytes(raw[:w * h * 4]))
    else:
        return False
    if max(im.size) > TEX_MAX:
        k = TEX_MAX / max(im.size)
        im = im.resize((max(1, int(im.width * k)), max(1, int(im.height * k))), Image.BILINEAR)
    if shader.endswith('_opaque') or 'alpha' not in shader:     # the alpha of an opaque material's map is not transparency
        im = im.convert('RGB')
    im.save(out_path)
    return True


# ------------------------------------------------------------------ meshes
def read_attr(vb, o, at, n):
    stride = o['stride%d' % at['buffer_index']]
    base = o['vb%d_offset' % at['buffer_index']] + at['buffer_offset']
    t = at['data_type']
    dt, comps = {0: ('<f4', 3), 4: ('<f4', 2), 5: ('<f2', 4), 8: ('<f2', 2), 9: ('<f2', 2), 2: ('<u1', 4)}[t]
    return np.ndarray((n, comps), dtype=dt, buffer=vb, offset=base, strides=(stride, np.dtype(dt).itemsize)).astype('<f4')


# (the meshes the game shows until an expression script says otherwise: the replay has no expressions)
FACE_DEFAULT = ('_Eye', '_FaceN', '_FaceNMClose')


def visible_default(full, short):
    return '_VIS_' not in full or short.endswith(FACE_DEFAULT)


def short_name(n):
    for s in ('_VIS_O_OBJShape', '_O_OBJShape', 'Shape'):
        if n.endswith(s):
            return n[:-len(s)]
    return n


def mesh_data(files, baked=False):
    from brawl import ssbh
    _m, _v, mesh = ssbh.parse(files.get('model.numshb'))
    _m, _v, modl = ssbh.parse(files.get('model.numdlb'))
    _m, _v, matl = ssbh.parse(files.get('model.numatb'))
    mats = {e['label']: e for e in matl['entries']}
    mat_of = {(e['mesh_name'], e['subindex']): e['material_label'] for e in modl['entries']}
    rig = {(r['mesh_name'], r['subindex']): r for r in mesh['rigging']}
    vb = [v['data'] for v in mesh['vertex_buffers']]
    ib = mesh['index_buffer']
    out = []
    for o in mesh['objects']:
        key = (o['name'], o['subindex'])
        mat = mats.get(mat_of.get(key))
        if mat is None:
            continue
        maps = {p['param_id']: p['param'][1] for p in mat['attributes'] if p['param'][0] == 0xB}
        tex = next((maps[k] for k in ((TEX_PARAM, TEX_BAKED) if baked else (TEX_PARAM,)) if k in maps and not maps[k].startswith('#')), None)
        attrs = {}
        for at in o['attributes']:
            names = [x['name'] for x in at['attribute_names']]
            attrs.setdefault(at['usage'], {})[names[0] if names else at['name']] = at
        n = o['vertex_count']
        pos_at = next(iter(attrs[0].values()))
        pos = read_attr(vb[pos_at['buffer_index']], o, pos_at, n)
        nor_at = next(iter(attrs[1].values()))
        nor = read_attr(vb[nor_at['buffer_index']], o, nor_at, n)[:, :3]
        uv_at = attrs.get(4, {}).get('map1') or next(iter(attrs.get(4, {}).values()), None)
        uv = read_attr(vb[uv_at['buffer_index']], o, uv_at, n) if uv_at else np.zeros((n, 2), '<f4')
        fmt = 'H' if o['draw_element_type'] == 0 else 'I'
        idx = np.frombuffer(ib, dtype='<' + ('u2' if fmt == 'H' else 'u4'), count=o['vertex_index_count'], offset=o['index_buffer_offset'])
        out.append(dict(name=short_name(o['name']), default=visible_default(o['name'], short_name(o['name'])), material=mat['label'], shader=mat['shader_label'], tex=tex, pos=pos, nor=nor, uv=uv, idx=idx.astype('<u4'),
                        parent=o['parent_bone_name'], rig=rig.get(key)))
    return out


def write_model(path, bones, meshes, tex_names):
    """u32 'RVM2'; u32 bones, then per bone: i32 parent, 16 f local (rows), 16 f inverse world (rows);
    u32 textures, then per texture: u16 length, name; u32 vis names, then each: u16 length, name;
    u32 meshes, then per mesh: i32 texture, i32 vis name (-1 none), i32 shown by default, u32 verts, u32 indices, 3f pos[], 3f normal[], 2f uv[],
    u8 bones[4][], f weights[4][] (per vertex), u32 index[]. Bone matrices: translation in the last row."""
    with open(path + '.tmp', 'wb') as f:
        f.write(b'RVM2')
        f.write(struct.pack('<I', len(bones)))
        for b in bones:
            f.write(struct.pack('<i', b['parent']))
            f.write(b['local'])
            f.write(b['inv'])
        f.write(struct.pack('<I', len(tex_names)))
        for t in tex_names:
            e = t.encode()
            f.write(struct.pack('<H', len(e)) + e)
        vis = sorted(set(m['name'] for m in meshes))
        f.write(struct.pack('<I', len(vis)))
        for t in vis:
            e = t.encode()
            f.write(struct.pack('<H', len(e)) + e)
        f.write(struct.pack('<I', len(meshes)))
        for m in meshes:
            f.write(struct.pack('<iiiII', m['tex_i'], vis.index(m['name']), 1 if m.get('default', True) else 0, len(m['pos']), len(m['idx'])))
            f.write(m['pos'].tobytes())
            f.write(m['nor'].tobytes())
            f.write(m['uv'].tobytes())
            f.write(m['skin_b'].tobytes())
            f.write(m['skin_w'].tobytes())
            f.write(m['idx'].tobytes())
    os.replace(path + '.tmp', path)


def skinning(m, bone_of, n):
    """per vertex up to 4 bones and weights, the heaviest ones, weights adding up to 1"""
    bi = np.zeros((n, 4), 'u1')
    bw = np.zeros((n, 4), '<f4')
    rig = m['rig']
    if rig and rig['buffers']:
        for b in rig['buffers']:
            i = bone_of.get(b['bone_name'])
            if i is None:
                continue
            k = len(b['data']) // 6
            rec = np.frombuffer(b['data'], dtype=np.dtype([('v', '<u2'), ('w', '<f4')]), count=k)
            for v, w in zip(rec['v'], rec['w']):
                j = int(np.argmin(bw[v]))
                if w > bw[v][j]:
                    bw[v][j], bi[v][j] = w, i
        s = bw.sum(axis=1, keepdims=True)
        s[s == 0] = 1
        bw /= s
    else:
        bi[:, 0] = bone_of.get(m['parent'], 0)
        bw[:, 0] = 1
    return bi, bw


def make_fighter(arc, tree, fighter, colour, out):
    from brawl import ssbh
    d = os.path.join(out, '%s_c%02d' % (fighter, colour))
    if os.path.exists(os.path.join(d, 'model.rvm')):
        return 0
    files = Files(arc, tree, 'fighter/%s/c%02d' % (fighter, colour), 'fighter/%s/model/body/c%02d' % (fighter, colour))
    if files.get('model.numshb') is None:
        print('no model of', fighter, colour)
        return 0
    os.makedirs(d, exist_ok=True)
    _m, _v, sk = ssbh.parse(files.get('model.nusktb'))
    bones = [dict(parent=b['parent'], name=b['name'], local=sk['local'][i], inv=sk['inv_world'][i]) for i, b in enumerate(sk['bone_entries'])]
    bone_of = {b['name']: i for i, b in enumerate(bones)}
    meshes = mesh_data(files)
    tex_names = []
    for m in meshes:
        m['tex_i'] = -1
        if m['tex']:
            name = m['tex']
            if name not in tex_names:
                data = files.get(name + '.nutexb')
                ok = False
                if data:
                    try:
                        ok = texture_png(data, os.path.join(d, 'tex%d.png' % len(tex_names)), m['shader'])
                    except Exception as e:
                        print('texture', name, e)
                if ok:
                    tex_names.append(name)
            if name in tex_names:
                m['tex_i'] = tex_names.index(name)
        if m['parent'] in bone_of and not (m['rig'] and m['rig']['buffers']):
            # a mesh on one bone (eyes, things in the hands) has its vertices relative to that bone: to the model's place at rest, like the others
            w = np.array(struct.unpack('<16f', sk['world'][bone_of[m['parent']]]), '<f4').reshape(4, 4)
            m['pos'] = (m['pos'].dot(w[:3, :3]) + w[3, :3]).astype('<f4')
            m['nor'] = m['nor'].dot(w[:3, :3]).astype('<f4')
        m['skin_b'], m['skin_w'] = skinning(m, bone_of, len(m['pos']))
    meshes = [m for m in meshes if m['tex_i'] >= 0]       # (without a colour map: glows and effects)
    write_model(os.path.join(d, 'model.rvm'), bones, meshes, tex_names)
    with open(os.path.join(d, 'names.txt'), 'w') as f:
        f.write('\n'.join(b['name'] for b in bones))
    return 1


# ------------------------------------------------------------------ motions
def decode_track(data, flags, frame_count):
    """like brawl.nuanmb.decode, on arrays: -> float array frames x 10 (scale 3, quaternion 4, translation 3), or None"""
    if flags == 0x201:
        return np.frombuffer(data, '<f4', 10, 0)[None, :].copy()
    if flags != 0x401:
        return None
    _u, fl, def_off, bpe, bit_off, frames = struct.unpack_from('<HHHHII', data, 0)
    rng = [struct.unpack_from('<ffQ', data, 16 + i * 16) for i in range(9)]
    d = struct.unpack_from('<10f', data, def_off)
    sk, has_r, has_t = fl & 3, (fl >> 2) & 1, (fl >> 3) & 1
    use = ([0, 1, 2] if sk == 1 else [0] if sk == 3 else []) + ([3, 4, 5] if has_r else []) + ([6, 7, 8] if has_t else [])
    use = [i for i in use if rng[i][2] > 0]
    if sum(rng[i][2] for i in use) + has_r != bpe or frames == 0:
        return None
    bits = np.unpackbits(np.frombuffer(data, 'u1', (frames * bpe + 7) // 8, bit_off), bitorder='little')[:frames * bpe].reshape(frames, bpe)
    vals = np.tile(np.array([d[0], d[1], d[2], d[3], d[4], d[5], d[7], d[8], d[9]], '<f8'), (frames, 1))
    p = 0
    for i in use:
        mn, mx, n = rng[i]
        n = int(n)
        raw = bits[:, p:p + n].astype('<f8').dot(2.0 ** np.arange(n))
        vals[:, i] = mn + (mx - mn) * raw / (2.0 ** n - 1)
        p += n
    if sk == 3:
        vals[:, 1] = vals[:, 2] = vals[:, 0]
    w = np.full(frames, d[6], '<f8')
    if has_r:
        w = np.sqrt(np.maximum(0.0, 1.0 - vals[:, 3] ** 2 - vals[:, 4] ** 2 - vals[:, 5] ** 2))
        w[bits[:, p] != 0] *= -1
    return np.column_stack((vals[:, 0:3], vals[:, 3:6], w, vals[:, 6:9])).astype('<f4')


def bool_track(data, flags, count):
    if flags & 0xff00 == 0x500:
        return [bool(data[0])]
    if flags & 0xff00 == 0x100:
        return [bool(x) for x in data[:count]]
    if flags & 0xff00 == 0x400:
        _u, _f, _d, bpe, off, frames = struct.unpack_from('<HHHHII', data, 0)
        if bpe == 1:
            b = np.unpackbits(np.frombuffer(data, 'u1', (frames + 7) // 8, off), bitorder='little')[:frames]
            return [bool(x) for x in b]
    return None


def write_motion(path, anim, bone_of):
    """u32 'RVA1', f32 last frame, u32 tracks, then per track: u16 bone, u16 frames, f[frames][10] (scale, quaternion xyzw, translation);
    u32 vis tracks, then per track: u16 length, name, u16 frames, u8[frames]. A bone without a track keeps its local matrix."""
    tracks, vis = [], []
    for g in anim['groups']:
        for node in g['nodes']:
            t = node['tracks'][0]
            data = anim['buffer'][t['data_offset']:t['data_offset'] + t['data_size']]
            if g['type'] == 1 and node['name'] in bone_of:
                a = decode_track(data, t['flags'], t['frame_count'])
                if a is not None:
                    tracks.append((bone_of[node['name']], a))
            elif g['type'] == 2:
                b = bool_track(data, t['flags'], t['frame_count'])
                if b:
                    vis.append((node['name'], b))
    with open(path + '.tmp', 'wb') as f:
        f.write(b'RVA1' + struct.pack('<fI', anim['final_frame_index'], len(tracks)))
        for bone, a in tracks:
            f.write(struct.pack('<HH', bone, len(a)))
            f.write(a.tobytes())
        f.write(struct.pack('<I', len(vis)))
        for name, b in vis:
            e = name.encode()
            f.write(struct.pack('<H', len(e)) + e + struct.pack('<H', len(b)) + bytes(1 if x else 0 for x in b))
    os.replace(path + '.tmp', path)


def make_motions(arc, tree, fighter, motions, out):
    from brawl import ssbh
    from brawl.motion_list import MotionList
    d = os.path.join(out, fighter)
    os.makedirs(d, exist_ok=True)
    missing = [m for m in motions if not os.path.exists(os.path.join(d, '%010x.rva' % (m & M40)))]
    if not missing:
        return 0
    files = Files(arc, tree, 'fighter/%s/c00' % fighter, 'fighter/%s/motion/body/c00' % fighter)
    listed = files.get('motion_list.bin')
    if listed is None:
        return 0
    rows = {r.key & M40: r for r in MotionList(listed).rows}
    model = Files(arc, tree, 'fighter/%s/c00' % fighter, 'fighter/%s/model/body/c00' % fighter)
    _m, _v, sk = ssbh.parse(model.get('model.nusktb'))
    bone_of = {b['name']: i for i, b in enumerate(sk['bone_entries'])}
    made = 0
    for m in missing:
        p = os.path.join(d, '%010x.rva' % (m & M40))
        r = rows.get(m & M40)
        data = files.get(r.anims[0] & M40) if r and r.anims else None
        if data is None:
            open(p, 'wb').close()              # this motion has no animation: known, not asked for again
            continue
        try:
            _m, _v, anim = ssbh.parse(data)
            write_motion(p, anim, bone_of)
            made += 1
        except Exception as e:
            print('motion %x: %s' % (m, e))
            open(p, 'wb').close()
    return made


# ------------------------------------------------------------------ stages
# parts the replay does not draw: Pokemon Stadium 2's transformations (the replay does not say which one is on), keeping its normal form
# and Final Destination's platforms (its other forms). Town and City's moving platforms: the viewer draws them from the recorded lines.
HIDE_PARTS = {'poke_stadium2': {0x16fe6c4fcf, 0x10555db0c9, 0x102ac7e828, 0x103bc02e8b, 0x10ca9cd1a3, 0x121e829983, 0x12db03f4fe, 0x14d5ce671a, 0x120e6baab0},
              'end': {0x1368726318, 0x12bf759851},
              'animal_city': {0x1ae09d28b4, 0x148ecbe207, 0x1aa41a6c97}}
MOVING_PART = {'animal_village': 0x1324f918c0}        # the platform that moves: placed each frame on the recorded lines (moving.txt)
STAGE_FOLDER = {'battle_field': 'battlefield', 'battle_field_s': 'battlefield_s', 'end': 'end', 'poke_stadium2': 'poke_stadium2', 'animal_village': 'animal_village',
                'animal_city': 'animal_city'}
HIDE_MESH = ('inkmesh', 'shadow', 'castshadow')       # the ink of the Splatoon games' paint, shadow casters


def make_stage(arc, tree, stage, out):
    from brawl import ssbh
    import extract_backgrounds as eb
    d = os.path.join(out, 'stage_%s' % stage)
    if os.path.exists(os.path.join(d, 'model.rvm')) or stage not in eb.PLAYABLE:
        return 0
    key = hash40('stage/%s/normal/model' % stage)
    if key not in tree.dirs:
        return 0
    os.makedirs(d, exist_ok=True)
    all_meshes, tex_names, moving = [], [], None
    for part in tree.children(tree.dirinfo(tree.dirs[key])):
        if part['name'] not in eb.PLAYABLE[stage] or part['name'] == eb.TRANSITION or part['name'] in HIDE_PARTS.get(stage, ()):
            continue
        names = {}
        for f in tree.files(part):
            names[f['name']] = f['pi']
        files = Files.__new__(Files)
        files.arc, files.names = arc, names
        if files.get('model.numshb') is None:
            continue
        rows = {}
        bones = []
        skl = files.get('model.nusktb')
        if skl:
            _m, _v, sk = ssbh.parse(skl)
            world = {}
            for i, b in enumerate(sk['bone_entries']):
                world[b['name']] = np.array(struct.unpack('<16f', sk['world'][i]), '<f4').reshape(4, 4)
            rows = world
            bones = sk['bone_entries']
        for m in mesh_data(files, stage in BAKED_STAGES):
            if m['name'].lower().startswith(HIDE_MESH):
                continue
            m['tex_i'] = -1
            if m['tex']:
                u = m['tex']
                if u not in tex_names:
                    data = files.get(u + '.nutexb')
                    ok = False
                    if data:
                        try:
                            ok = texture_png(data, os.path.join(d, 'tex%d.png' % len(tex_names)), m['shader'])
                        except Exception as e:
                            print('texture', u, e)
                    if ok:
                        tex_names.append(u)
                if u in tex_names:
                    m['tex_i'] = tex_names.index(u)
            if m['parent'] and m['parent'] in rows and not (m['rig'] and m['rig']['buffers']):
                w = rows[m['parent']]                     # a mesh parented to one bone: placed here, the file has no bones
                m['pos'] = (m['pos'].dot(w[:3, :3]) + w[3, :3]).astype('<f4')
                m['nor'] = m['nor'].dot(w[:3, :3]).astype('<f4')
            if m['tex_i'] < 0:                            # (without a colour map: lights, glows)
                continue
            m['skin_b'] = np.zeros((len(m['pos']), 4), 'u1')
            m['skin_w'] = np.zeros((len(m['pos']), 4), '<f4')
            m['skin_w'][:, 0] = 1
            m['name'] = 'stage%d' % len(all_meshes)
            all_meshes.append(m)
            if part['name'] == MOVING_PART.get(stage):
                moving = (moving or []) + [len(all_meshes) - 1]
    ident = np.eye(4, dtype='<f4').tobytes()
    write_model(os.path.join(d, 'model.rvm'), [dict(parent=-1, local=ident, inv=ident)], all_meshes, tex_names)
    if moving:
        pts = np.concatenate([all_meshes[i]['pos'] for i in moving])
        with open(os.path.join(d, 'moving.txt'), 'w') as f:
            f.write('%d %d %f %f' % (moving[0], len(moving), (pts[:, 0].min() + pts[:, 0].max()) / 2, pts[:, 1].max()))
    if HIDE_PARTS.get(stage) and stage == 'animal_city':
        open(os.path.join(d, 'lines.txt'), 'w').close()      # the viewer draws the moving platforms as lines
    return 1


# ------------------------------------------------------------------ a replay's needs
def replay_needs(path):
    from ssbv import Ssbv
    with open(path, 'rb') as f:
        v = Ssbv(f.read())
    fighters = [(f['ui_chara'] & M40, f['colour']) for f in v.fighters]
    motions = {}
    for fr in v.frames:
        for slot, st in fr['f'].items():
            motions.setdefault(slot, set()).add(st['motion'])
    return v, fighters, motions


def scale_of(arc, tree, name, colour, slot, v, out):
    """the fighter's size in the game: the scale that puts its bones nearest the hurt boxes the game recorded (a fighter's model is
    drawn scaled by a parameter). Kept in the model's folder."""
    f = os.path.join(out, '%s_c%02d' % (name, colour), 'scale.txt')
    if os.path.exists(f):
        return open(f).read().strip()
    from brawl import ssbh
    model = Files(arc, tree, 'fighter/%s/c%02d' % (name, colour), 'fighter/%s/model/body/c%02d' % (name, colour))
    _m, _v, sk = ssbh.parse(model.get('model.nusktb'))
    bones = [(b['parent'], np.array(struct.unpack('<16f', sk['local'][i])).reshape(4, 4)) for i, b in enumerate(sk['bone_entries'])]
    scales = np.arange(0.5, 2.01, 0.01)
    best, votes = {}, []
    frames = [fr for fr in v.frames if slot in fr['f'] and fr['f'][slot]['hurts']]
    for fr in frames[::max(1, len(frames) // 40)]:
        st = fr['f'][slot]
        tr = read_rva(os.path.join(out, name, '%010x.rva' % (st['motion'] & M40)))
        if tr is None:
            continue
        B = bone_points(bones, tr, st['mf'])
        H = np.array([[h['x'], h['y']] for h in st['hurts']] + [[h['x2'], h['y2']] for h in st['hurts']])
        xs, ys = B[:, 2] * st['lr'], B[:, 1]
        errs = []
        for k in scales:
            P = np.column_stack((st['x'] + xs * k, st['y'] + ys * k))
            errs.append(np.median(np.sqrt(((H[:, None, :] - P[None, :, :]) ** 2).sum(axis=2)).min(axis=1)))
        votes.append(scales[int(np.argmin(errs))])
    s = '%.2f' % (np.median(votes) if votes else 1.0)
    with open(f, 'w') as fp:
        fp.write(s)
    return s


def read_rva(path):
    if not os.path.exists(path) or os.path.getsize(path) < 12:
        return None
    d = open(path, 'rb').read()
    _last, nt = struct.unpack_from('<fI', d, 4)
    o, tr = 12, {}
    for _ in range(nt):
        b, f = struct.unpack_from('<HH', d, o)
        o += 4
        tr[b] = np.frombuffer(d, '<f4', f * 10, o).reshape(f, 10)
        o += f * 40
    return tr


def bone_points(bones, tr, frame):
    W = [None] * len(bones)

    def world(i):
        if W[i] is None:
            par, loc = bones[i]
            if i in tr:
                a = tr[i][min(max(int(frame), 0), len(tr[i]) - 1)].astype('f8')
                x, y, z, w = a[3:7]
                m = np.eye(4)
                m[:3, :3] = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w)], [2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w)],
                                      [2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y)]]) * a[:3, None]
                m[3, :3] = a[7:10]
                if par < 0:
                    m[3, :3] = 0
            else:
                m = loc
            W[i] = m if par < 0 else m.dot(world(par))
        return W[i]
    return np.array([world(i)[3, :3] for i in range(len(bones))])


def fighter_of(ui40):
    names = {hash40('ui_chara_' + f): f for f in FIGHTERS}
    names.update({hash40('ui_chara_' + k): v for k, v in UI_FOLDER.items()})
    return names.get(ui40)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--arc', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--replay', required=True)
    a = ap.parse_args()
    os.environ['SSBU_ARC'] = a.arc
    import arc_extract as ax
    import extract_backgrounds as eb
    arc = ax.Arc(a.arc)
    tree = eb.Tree(arc)
    os.makedirs(a.out, exist_ok=True)
    v, fighters, motions = replay_needs(a.replay)
    made = 0
    manifest = []
    for slot, (ui, colour) in enumerate(fighters):
        name = fighter_of(ui)
        if not name:
            print('fighter not known: %x' % ui)
            manifest.append('fighter %d' % slot)
            continue
        print('fighter', slot, name, 'colour', colour, flush=True)
        made += make_fighter(arc, tree, name, colour, a.out)
        made += make_motions(arc, tree, name, motions.get(slot, ()), a.out)
        manifest.append('fighter %d %s_c%02d %s %s' % (slot, name, colour, name, scale_of(arc, tree, name, colour, slot, v, a.out)))
    stage = next((f for u, f in STAGE_FOLDER.items() if hash40('ui_stage_' + u) == v.ui_stage & M40), None)
    print('stage', stage, flush=True)
    if stage:
        made += make_stage(arc, tree, stage, a.out)
        manifest.append('stage stage_%s' % stage)
    os.makedirs(os.path.join(a.out, 'replays'), exist_ok=True)
    with open(os.path.join(a.out, 'replays', os.path.basename(a.replay) + '.txt'), 'w') as f:
        f.write('\n'.join(manifest) + '\n')
    print('done', made, flush=True)


if __name__ == '__main__':
    main()
