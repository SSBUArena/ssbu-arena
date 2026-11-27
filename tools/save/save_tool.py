"""save_tool.py - read / write the parts of SSBU's system save that SSBU Arena needs: the custom rule sets and the
controls of the name tags (control profiles). Study / launcher-side tool; it holds no game code and no game data.

    python tools/save/save_tool.py info      <system_data.bin>
    python tools/save/save_tool.py rules     <system_data.bin> [--all] [--section smash|squad|tourney|section3]
    python tools/save/save_tool.py profiles  <system_data.bin> [--all]
    python tools/save/save_tool.py set-rule  <system_data.bin> --section smash --slot N --json <file|-> [--out <file>]
    python tools/save/save_tool.py set-profile <system_data.bin> --slot N --json <file|-> [--out <file>]
    python tools/save/save_tool.py delete-profile <system_data.bin> --slot N [--out <file>]   (as the game leaves a deleted tag)
    python tools/save/save_tool.py roundtrip <system_data.bin>       (decode + encode everything: must be byte-identical)

The format (13.0.2 / 13.0.4 / 13.0.5 alike) is described in save_research/SAVE_FORMAT.txt, which this file follows:
  - file: save_data/system_data.bin, 0x5b4af8 bytes; u32 size at +0, u32 format 0x1b at +4, u32 game version at +8
    (0xd002 / 0xd004 / 0xd005). No checksum or signature over the file (the launcher and the runtime already patch
    bytes in place and the game accepts them). The only "hashes" are the per-name hashes of the rule sets and name
    tags, which the game recomputes when it reads them; this tool keeps them right anyway.
  - rule sets: 4 sections of 30 entries of 0x88 bytes (0x330 Smash, 0x1320 Squad Strike, 0x2310 Tourney, 0x3300 a
    fourth list never used in any sample); entry = 0x48 bytes of settings + created u32, modified u32, name hash u32,
    name length u32, name UTF-16 (22 characters + NUL).
  - name tags: 61 entries of 0xf7d8 bytes at 0x42f0 (entry 0 is the unnamed default one); + 0 in use, + 4 name hash,
    + 8 name length, + 0xc name UTF-16 (10 characters + NUL), + 0x24 .. + 0x4e the controls of the three controller
    kinds, + 0x50 created u32, + 0x54 modified u32; the rest of the entry (records) is not touched.
  - u32 at 0x550ad8: the save's change counter (the created / modified numbers of rule sets and name tags come from it).

Writes change only the bytes of the fields given in the JSON (an unchanged name keeps its bytes, garbage after the NUL
included), so a write of unchanged values is byte-identical. A write in place first keeps a copy <file>.bak-<time>.
Close the game first (it rewrites the whole save at its next save), and never write while <saves>\\save.commit exists.
"""
import argparse
import datetime
import json
import os
import shutil
import struct
import sys

SIZE = 0x5B4AF8
FORMAT = 0x1B
COUNTER = 0x550AD8

# ---------------------------------------------------------------- rule sets
RULE_SECTIONS = {'smash': 0x330, 'squad': 0x1320, 'tourney': 0x2310, 'section3': 0x3300}
RULE_SLOTS = 30
RULE_SIZE = 0x88
RULE_NAME = 0x58            # UTF-16, 23 units (22 characters + NUL), then 2 bytes of padding
RULE_NAME_UNITS = 23
RULE_NAME_MAX = 22

STYLE = ['time', 'stock', 'stamina']                       # ui_rule_db "rule": rule_time 0, rule_stock 1, rule_hp 2
MODE = ['melee', 'team', 'tournament']                     # "mode": mode_melee, mode_team, mode_tournament (validator <= 2)
HP = [10, 30, 50, 80, 100, 150, 200, 250, 300, 500, 750, 999]   # "hp": hp_10 .. hp_999 (index; default 5 = 150)
CPU_LEVEL = ['1', '2', '3', '4', '5', '6', '7', '8', '9', 'random']   # "level": cpu_lv_1 .. cpu_lv_9, cpu_random

# settings fields: (name, offset, kind); kind 'u8' / 'bool' / 'u16' / enum list. Offsets are in the save entry
# (the game's in-memory rule struct has them at + 0x40 more: + 0x51 rule kind, + 0x54 time, + 0x56 stock ...).
RULE_FIELDS = [
    ('mode', 0x10, MODE),
    ('style', 0x11, STYLE),
    ('squad_style', 0x12, 'u8'),        # "team_battle": 3 values (Squad Strike style), 0..2
    ('time_s', 0x14, 'u16'),            # seconds, 0 = no limit; the game accepts 0 or 60..5940
    ('stock', 0x16, 'u8'),              # 1..99
    ('hp', 0x17, HP),                   # stamina HP
    ('wins', 0x18, 'u8'),               # "win_1".."win_5" stored 0..4 (meaning not confirmed)
    ('unk_19', 0x19, 'bool'),           # hash 0x076b452955, default 0
    ('cpu_level', 0x1a, CPU_LEVEL),
    ('unk_1b', 0x1b, 'u8'),             # hash 0x0cb043a60b, 3 values
    ('stage_select', 0x1c, 'u8'),       # "select_stage": 0..7 (select_turn / select_order / select_loser / select_random ...)
    ('team_attack', 0x1d, 'bool'),
    ('launch_rate_x10', 0x1e, 'u8'),    # 5..20 = 0.5x .. 2.0x, default 10
    ('fs_meter', 0x1f, 'bool'),         # "charge"
    ('unk_20', 0x20, 'u8'),             # hash 0x0b0c053ec9, 0..1
    ('stage_hazards', 0x21, 'bool'),    # "stage_gimmick"
    ('unk_22', 0x22, 'u8'),             # not in the rule loader, default 1
    ('stage_morph', 0x23, 'u8'),        # "change_stage": 0 = change_none, 1..10 morph choices (change_100 .. change_500, change_auto)
    ('unk_24', 0x24, 'u8'),             # hash 0x08f34393c9, 0..10
    ('pause', 0x25, 'bool'),            # "can_pause"
    ('unk_26', 0x26, 'bool'),           # hash 0x0dcf90adf0, default 0
    ('unk_27', 0x27, 'bool'),           # hash 0x0eb0e4ce88, default 1
    ('item_frequency', 0x28, 'u8'),     # 0..3 (0 = no items; default 2)
]
RULE_ITEMS = (0x2C, 0x48)               # item switch: one bit per item, 224 bits

# ---------------------------------------------------------------- name tags (control profiles)
PROFILE_BASE = 0x42F0
PROFILE_SLOTS = 61
PROFILE_SIZE = 0xF7D8
PROFILE_NAME = 0x0C
PROFILE_NAME_UNITS = 11
PROFILE_NAME_MAX = 10
CTL = (0x24, 0x4F)                      # 0x2b bytes, what the game copies into a fight's controls
ACTIONS = {0: 'attack', 1: 'special', 2: 'jump', 3: 'shield', 4: 'grab', 5: 'smash_attack',
           10: 'taunt_up', 11: 'taunt_side', 12: 'taunt_down'}
ACTION_CODES = {v: k for k, v in ACTIONS.items()}
CONTROLLERS = [   # (name, first byte, buttons); then 4 option bytes each
    ('gamecube', 0x24, ['L', 'R', 'Z', 'DPAD_UP', 'DPAD_SIDE', 'DPAD_DOWN', 'A', 'B', 'C_STICK', 'X', 'Y']),
    ('pro', 0x33, ['L', 'R', 'ZL', 'ZR', 'DPAD_UP', 'DPAD_SIDE', 'DPAD_DOWN', 'A', 'B', 'R_STICK', 'X', 'Y']),
    ('joycon', 0x43, ['JC1', 'JC2', 'JC3', 'JC4', 'JC5', 'JC6', 'JC7', 'JC8']),
]
DEFAULT_CONTROLS = bytes.fromhex('0303040a0b0c000105020201010101040403030a0b0c0001050202010101010d0d04030200020101010101')   # the game's defaults
OPTIONS = ['opt0', 'opt1', 'opt2', 'opt3']   # opt0 is the byte the fight record takes (record + 0x48); see SAVE_FORMAT.txt


class SaveError(Exception):
    pass


def name_hash(units):
    """the game's hash of a name (FixedString16: 0x811c9dc5, then h = h * 0x89 ^ unit for each UTF-16 unit)"""
    h = 0x811C9DC5
    for u in units:
        h = ((h * 0x89) & 0xFFFFFFFF) ^ u
    return h


def units_of(text):
    b = text.encode('utf-16-le')
    return list(struct.unpack('<%dH' % (len(b) // 2), b))


def read_name(d, off, nunits):
    units = []
    for i in range(nunits):
        u = struct.unpack_from('<H', d, off + 2 * i)[0]
        if u == 0:
            break
        units.append(u)
    return struct.pack('<%dH' % len(units), *units).decode('utf-16-le', 'replace')


def write_name(d, hash_off, len_off, name_off, nunits, nmax, text):
    """writes a new name: its units, a NUL, zeros up to the end of the field; hash and length as the game computes them"""
    units = units_of(text)
    if len(units) > nmax:
        raise SaveError('name too long: %d UTF-16 units, at most %d' % (len(units), nmax))
    if any(u == 0 for u in units):
        raise SaveError('a name cannot hold NUL')
    raw = struct.pack('<%dH' % nunits, *(units + [0] * (nunits - len(units))))
    d[name_off:name_off + 2 * nunits] = raw
    struct.pack_into('<I', d, hash_off, name_hash(units))
    struct.pack_into('<I', d, len_off, len(units))


class Save:
    def __init__(self, data):
        self.d = bytearray(data)
        if len(self.d) != SIZE:
            raise SaveError('not a system_data.bin of the known layout: %d bytes, not %d' % (len(self.d), SIZE))
        size, fmt = struct.unpack_from('<II', self.d, 0)
        if size != SIZE or fmt != FORMAT:
            raise SaveError('header %#x / %#x: not the known layout (%#x / %#x)' % (size, fmt, SIZE, FORMAT))

    @classmethod
    def load(cls, path):
        with open(path, 'rb') as f:
            return cls(f.read())

    @property
    def version(self):
        return struct.unpack_from('<I', self.d, 8)[0]

    @property
    def counter(self):
        return struct.unpack_from('<I', self.d, COUNTER)[0]

    def bump_counter(self):
        n = (self.counter + 1) & 0xFFFFFFFF
        struct.pack_into('<I', self.d, COUNTER, n)
        return n

    # ------------------------------------------------------------ rule sets
    @staticmethod
    def rule_offset(section, slot):
        if section not in RULE_SECTIONS:
            raise SaveError('unknown section %r (%s)' % (section, ', '.join(RULE_SECTIONS)))
        if not 0 <= slot < RULE_SLOTS:
            raise SaveError('slot %d out of 0..%d' % (slot, RULE_SLOTS - 1))
        return RULE_SECTIONS[section] + RULE_SIZE * slot

    def rule(self, section, slot):
        o = self.rule_offset(section, slot)
        d = self.d
        # + 0 is 1 for a rule set in use, 0 for an empty slot; the saves made by a console / an emulator hold Smash slot 0
        # filled with 0xff (an erased built-in set): listed as not in use
        r = {'section': section, 'slot': slot, 'offset': '%#x' % o, 'in_use': d[o] == 1}
        created, modified, h, n = struct.unpack_from('<IIII', d, o + 0x48)
        r['name'] = read_name(d, o + RULE_NAME, RULE_NAME_UNITS)
        r['created'] = created
        r['modified'] = modified
        r['name_hash_ok'] = h == name_hash(units_of(r['name'])) and n == len(units_of(r['name']))
        for name, off, kind in RULE_FIELDS:
            if kind == 'u16':
                v = struct.unpack_from('<H', d, o + off)[0]
            else:
                v = d[o + off]
            if kind == 'bool':
                v = bool(v) if v in (0, 1) else v
            elif isinstance(kind, list):
                v = kind[v] if v < len(kind) else {'raw': v}      # a value the game would refuse (an unused slot's)
            r[name] = v
        r['launch_rate'] = d[o + 0x1e] / 10.0
        items = bytes(d[o + RULE_ITEMS[0]:o + RULE_ITEMS[1]])
        r['items_mask'] = items.hex()
        r['items_on'] = sum(bin(b).count('1') for b in items)
        r['raw_head'] = bytes(d[o:o + 0x10]).hex()      # + 0 in use, + 1 .. + 0xf not understood (kept)
        r['raw_13'] = d[o + 0x13]
        return r

    def rules(self, section=None, include_unused=False):
        out = []
        for s in RULE_SECTIONS:
            if section and s != section:
                continue
            for k in range(RULE_SLOTS):
                r = self.rule(s, k)
                if r['in_use'] or include_unused:
                    out.append(r)
        return out

    def free_rule_slot(self, section):
        """the first slot not in use (the game keeps its sets in the first slots, without gaps)"""
        for k in range(RULE_SLOTS):
            if self.d[self.rule_offset(section, k)] != 1:
                return k
        raise SaveError('no free slot in %s' % section)

    def set_rule(self, section, slot, values, touch=True, check=True):
        """writes the fields given in values (names as rule() returns them); returns the list of fields written.
        check: the game's own limits (off only for the round trip, which writes back what is there)"""
        o = self.rule_offset(section, slot)
        d = self.d
        before = bytes(d[o:o + RULE_SIZE])
        known = {f[0]: f for f in RULE_FIELDS}
        done = []
        for key, v in values.items():
            if key in ('section', 'slot', 'offset', 'name_hash_ok', 'items_on', 'created', 'modified', 'launch_rate'):
                if key == 'launch_rate' and 'launch_rate_x10' not in values:
                    d[o + 0x1e] = self._int(round(float(v) * 10), 5, 20, key)
                    done.append(key)
                continue
            if key == 'in_use':
                if bool(v) != (d[o] == 1):
                    d[o] = 1 if v else 0
            elif key == 'name':
                if v != read_name(d, o + RULE_NAME, RULE_NAME_UNITS):
                    write_name(d, o + 0x50, o + 0x54, o + RULE_NAME, RULE_NAME_UNITS, RULE_NAME_MAX, v)
            elif key == 'items_mask':
                b = bytes.fromhex(v)
                if len(b) != RULE_ITEMS[1] - RULE_ITEMS[0]:
                    raise SaveError('items_mask: %d bytes, not %d' % (len(b), RULE_ITEMS[1] - RULE_ITEMS[0]))
                d[o + RULE_ITEMS[0]:o + RULE_ITEMS[1]] = b
            elif key == 'raw_head':
                b = bytes.fromhex(v)
                if len(b) != 0x10:
                    raise SaveError('raw_head: 16 bytes')
                d[o:o + 0x10] = b
            elif key == 'raw_13':
                d[o + 0x13] = self._int(v, 0, 255, key)
            elif key in known:
                _, off, kind = known[key]
                if kind == 'u16':
                    struct.pack_into('<H', d, o + off, self._int(v, 0, 0xFFFF, key))
                elif kind == 'bool':
                    d[o + off] = self._int(int(v) if isinstance(v, bool) else v, 0, 255, key)
                elif isinstance(kind, list):
                    if isinstance(v, dict) and 'raw' in v:
                        d[o + off] = self._int(v['raw'], 0, 255, key)
                    elif v in kind and not isinstance(v, bool):
                        d[o + off] = kind.index(v)
                    else:
                        raise SaveError('%s: %r is not one of %s' % (key, v, kind))
                else:
                    d[o + off] = self._int(v, 0, 255, key)
            else:
                raise SaveError('unknown rule field %r' % key)
            done.append(key)
        if check:
            self._check_rule(o)
        if bytes(d[o:o + RULE_SIZE]) != before and touch:
            n = self.bump_counter()
            if before[0] == 0 and d[o]:
                struct.pack_into('<I', d, o + 0x48, n)          # a new rule set: created now
            struct.pack_into('<I', d, o + 0x4c, n)
        return done

    def _check_rule(self, o):
        """the same limits as the game's own check before it stores a rule set (0x71017dc930): a value outside them
        would make the game refuse or misread the set"""
        d = self.d
        t = struct.unpack_from('<H', d, o + 0x14)[0]
        limits = [(0x10, 2), (0x11, 2), (0x12, 2), (0x17, 11), (0x18, 4), (0x1a, 9), (0x1b, 2), (0x1c, 7), (0x20, 1),
                  (0x23, 10), (0x24, 10), (0x28, 3)]
        for off, hi in limits:
            if d[o + off] > hi:
                raise SaveError('rule byte + %#x = %d: the game takes 0..%d' % (off, d[o + off], hi))
        if t != 0 and not 60 <= t <= 60 + 0x16F8:
            raise SaveError('time_s %d: the game takes 0 or 60..5940' % t)
        if not 1 <= d[o + 0x16] <= 99:
            raise SaveError('stock %d: the game takes 1..99' % d[o + 0x16])
        if not 5 <= d[o + 0x1e] <= 20:
            raise SaveError('launch_rate_x10 %d: the game takes 5..20' % d[o + 0x1e])

    # ------------------------------------------------------------ name tags
    @staticmethod
    def profile_offset(slot):
        if not 0 <= slot < PROFILE_SLOTS:
            raise SaveError('slot %d out of 0..%d' % (slot, PROFILE_SLOTS - 1))
        return PROFILE_BASE + PROFILE_SIZE * slot

    def profile(self, slot):
        o = self.profile_offset(slot)
        d = self.d
        h, n = struct.unpack_from('<II', d, o + 4)
        p = {'slot': slot, 'offset': '%#x' % o, 'in_use': d[o] == 1,
             'name': read_name(d, o + PROFILE_NAME, PROFILE_NAME_UNITS)}
        p['name_hash_ok'] = h == name_hash(units_of(p['name'])) and n == len(units_of(p['name']))
        p['created'], p['modified'] = struct.unpack_from('<II', d, o + 0x50)
        for cname, first, buttons in CONTROLLERS:
            c = {'buttons': {}, 'options': {}}
            for i, b in enumerate(buttons):
                v = d[o + first + i]
                c['buttons'][b] = ACTIONS.get(v, v)
            for i, opt in enumerate(OPTIONS):
                c['options'][opt] = d[o + first + len(buttons) + i]
            p[cname] = c
        p['raw_controls'] = bytes(d[o + CTL[0]:o + CTL[1]]).hex()
        return p

    def profiles(self, include_unused=False):
        return [self.profile(k) for k in range(PROFILE_SLOTS) if include_unused or self.d[self.profile_offset(k)] == 1]

    def set_profile(self, slot, values, touch=True, check=True):
        o = self.profile_offset(slot)
        d = self.d
        before = bytes(d[o:o + 0x58])
        done = []
        ctl = {c[0]: c for c in CONTROLLERS}
        for key, v in values.items():
            if key in ('slot', 'offset', 'name_hash_ok', 'created', 'modified'):
                continue
            if key == 'in_use':
                if bool(v) != (d[o] == 1):
                    d[o] = 1 if v else 0
            elif key == 'name':
                if v != read_name(d, o + PROFILE_NAME, PROFILE_NAME_UNITS):
                    write_name(d, o + 4, o + 8, o + PROFILE_NAME, PROFILE_NAME_UNITS, PROFILE_NAME_MAX, v)
            elif key == 'raw_controls':
                b = bytes.fromhex(v)
                if len(b) != CTL[1] - CTL[0]:
                    raise SaveError('raw_controls: %d bytes' % (CTL[1] - CTL[0]))
                if 'gamecube' in values or 'pro' in values or 'joycon' in values:
                    continue                                    # the decoded blocks win
                d[o + CTL[0]:o + CTL[1]] = b
            elif key in ctl:
                _, first, buttons = ctl[key]
                for b, a in v.get('buttons', {}).items():
                    if b not in buttons:
                        raise SaveError('%s: no button %r (%s)' % (key, b, ', '.join(buttons)))
                    code = ACTION_CODES.get(a, a)
                    d[o + first + buttons.index(b)] = self._int(code, 0, 0x0F if check else 0xFF, '%s.%s' % (key, b))
                for opt, a in v.get('options', {}).items():
                    if opt not in OPTIONS:
                        raise SaveError('%s: no option %r' % (key, opt))
                    d[o + first + len(buttons) + OPTIONS.index(opt)] = self._int(a, 0, 2 if check else 0xFF, '%s.%s' % (key, opt))
            else:
                raise SaveError('unknown profile field %r' % key)
            done.append(key)
        if bytes(d[o:o + 0x58]) != before and touch:
            n = self.bump_counter()
            if before[0] == 0 and d[o]:
                struct.pack_into('<I', d, o + 0x50, n)
            struct.pack_into('<I', d, o + 0x54, n)
        return done

    def delete_profile(self, slot):
        """a name tag removed as the game leaves one it deleted (seen in the samples: the whole entry zero, name hash 0, the
        default controls, its records gone); the change counter + 1. Entry 0 (the game's own unnamed one) is refused."""
        if slot < 1:
            raise SaveError("entry 0 is the game's own unnamed one: not deleted")
        o = self.profile_offset(slot)
        self.d[o:o + PROFILE_SIZE] = bytes(PROFILE_SIZE)
        self.d[o + CTL[0]:o + CTL[1]] = DEFAULT_CONTROLS
        self.bump_counter()

    @staticmethod
    def _int(v, lo, hi, what):
        if isinstance(v, bool) or not isinstance(v, int):
            raise SaveError('%s: %r is not a number' % (what, v))
        if not lo <= v <= hi:
            raise SaveError('%s: %d out of %d..%d' % (what, v, lo, hi))
        return v

    # ------------------------------------------------------------ whole-file check
    def roundtrip(self):
        """decodes every rule set and name tag and writes each back with all its fields: returns the copy (it must be
        byte-identical to the original)"""
        c = Save(bytes(self.d))
        for r in self.rules(include_unused=True):
            v = {k: x for k, x in r.items() if k not in ('launch_rate',)}
            c.set_rule(r['section'], r['slot'], v, touch=False, check=False)
        for p in self.profiles(include_unused=True):
            v = dict(p)
            v.pop('raw_controls')
            c.set_profile(p['slot'], v, touch=False, check=False)
        return c


def write_file(path, data, out):
    dst = out or path
    if not out:
        saves = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(path))))
        if os.path.exists(os.path.join(saves, 'save.commit')):
            raise SaveError('%s exists: the game has a commit to finish; start and close the game first' % os.path.join(saves, 'save.commit'))
        shutil.copy2(path, path + '.bak-' + datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))
    tmp = dst + '.tmp'
    with open(tmp, 'wb') as f:
        f.write(data)
    os.replace(tmp, dst)
    return dst


def read_json(arg):
    return json.load(sys.stdin if arg == '-' else open(arg, encoding='utf-8'))


def main(argv=None):
    ap = argparse.ArgumentParser(description='SSBU system save: rule sets and name-tag controls')
    sub = ap.add_subparsers(dest='cmd', required=True)
    for c in ('info', 'rules', 'profiles', 'roundtrip', 'set-rule', 'set-profile', 'delete-profile'):
        p = sub.add_parser(c)
        p.add_argument('save')
        if c in ('rules', 'profiles'):
            p.add_argument('--all', action='store_true', help='unused slots too')
        if c == 'rules':
            p.add_argument('--section', choices=list(RULE_SECTIONS))
        if c in ('set-rule', 'set-profile'):
            p.add_argument('--slot', type=int, required=True, help='-1 (set-rule): the first free slot')
            p.add_argument('--json', required=True, help='a JSON object of the fields to write (- = stdin)')
            p.add_argument('--out', help='write here instead of in place')
            p.add_argument('--no-touch', action='store_true', help='leave the change counter and modified number as they are')
        if c == 'set-rule':
            p.add_argument('--section', choices=list(RULE_SECTIONS), required=True)
        if c == 'delete-profile':
            p.add_argument('--slot', type=int, required=True)
            p.add_argument('--out', help='write here instead of in place')
    a = ap.parse_args(argv)
    try:
        s = Save.load(a.save)
        if a.cmd == 'info':
            print(json.dumps({'size': len(s.d), 'format': FORMAT, 'version': '%#x' % s.version, 'counter': s.counter,
                              'rule_sets': {k: sum(1 for r in s.rules(k)) for k in RULE_SECTIONS},
                              'name_tags': [p['name'] for p in s.profiles()]}, indent=2, ensure_ascii=False))
        elif a.cmd == 'rules':
            print(json.dumps(s.rules(a.section, a.all), indent=2, ensure_ascii=False))
        elif a.cmd == 'profiles':
            print(json.dumps(s.profiles(a.all), indent=2, ensure_ascii=False))
        elif a.cmd == 'roundtrip':
            c = s.roundtrip()
            same = bytes(c.d) == bytes(s.d)
            print('round trip: %s' % ('byte-identical' if same else 'DIFFERENT'))
            return 0 if same else 1
        elif a.cmd == 'set-rule':
            if a.slot == -1:
                a.slot = s.free_rule_slot(a.section)
            done = s.set_rule(a.section, a.slot, read_json(a.json), touch=not a.no_touch)
            print('rule set %s %d: %s -> %s' % (a.section, a.slot, ', '.join(done) or 'nothing', write_file(a.save, bytes(s.d), a.out)))
        elif a.cmd == 'set-profile':
            done = s.set_profile(a.slot, read_json(a.json), touch=not a.no_touch)
            print('name tag %d: %s -> %s' % (a.slot, ', '.join(done) or 'nothing', write_file(a.save, bytes(s.d), a.out)))
        elif a.cmd == 'delete-profile':
            s.delete_profile(a.slot)
            print('name tag %d: deleted -> %s' % (a.slot, write_file(a.save, bytes(s.d), a.out)))
    except (SaveError, OSError, ValueError) as e:
        print('save_tool: %s' % e, file=sys.stderr)
        return 2
    return 0


if __name__ == '__main__':
    sys.exit(main())
