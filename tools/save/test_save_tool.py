"""Tests of tools/save/save_tool.py on made-up saves (no game data, no real save in the repo).

    python tools/save/test_save_tool.py

Optional: SSBU_SAVE_SAMPLES=<folder> also round-trips every *.bin of that folder (copies of real saves, kept OUTSIDE the
repo): decode + encode must be byte-identical, and a write of unchanged values must not change a byte.
"""
import glob
import json
import os
import random
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import save_tool as st  # noqa: E402


def made_up_save(seed=1, garbage=True):
    """a save of the right size and header; random bytes everywhere (so offsets that are wrong show up), then the rule
    sets and name tags in the shapes the game writes"""
    rnd = random.Random(seed)
    d = bytearray(rnd.getrandbits(8) for _ in range(st.SIZE)) if garbage else bytearray(st.SIZE)
    struct.pack_into('<III', d, 0, st.SIZE, st.FORMAT, 0xD004)
    struct.pack_into('<I', d, st.COUNTER, 100)
    for sec in st.RULE_SECTIONS:
        for k in range(st.RULE_SLOTS):
            o = st.Save.rule_offset(sec, k)
            d[o] = 0                                         # empty slots; some made valid below
    for p in range(st.PROFILE_SLOTS):
        d[st.Save.profile_offset(p)] = 0
    return d


def valid_rule(s, sec, slot, name):
    o = s.rule_offset(sec, slot)
    d = s.d
    d[o] = 1
    for off, v in ((0x10, 0), (0x11, 1), (0x12, 0), (0x16, 3), (0x17, 5), (0x18, 0), (0x1a, 2), (0x1b, 0), (0x1c, 0),
                   (0x1e, 10), (0x20, 0), (0x23, 0), (0x24, 0), (0x28, 2)):
        d[o + off] = v
    struct.pack_into('<H', d, o + 0x14, 150)
    st.write_name(d, o + 0x50, o + 0x54, o + st.RULE_NAME, st.RULE_NAME_UNITS, st.RULE_NAME_MAX, name)


class HashTest(unittest.TestCase):
    def test_empty_is_the_basis(self):
        self.assertEqual(st.name_hash([]), 0x811C9DC5)          # what every empty slot holds in the saves

    def test_formula(self):
        h = ((0x811C9DC5 * 0x89) & 0xFFFFFFFF) ^ ord('a')
        h = ((h * 0x89) & 0xFFFFFFFF) ^ ord('b')
        self.assertEqual(st.name_hash(st.units_of('ab')), h)

    def test_utf16_units(self):                                # the game hashes UTF-16 units, not UTF-8 bytes
        self.assertEqual(st.units_of('è'), [0xE8])
        self.assertNotEqual(st.name_hash(st.units_of('è')), st.name_hash(list('è'.encode('utf-8'))))


class HeaderTest(unittest.TestCase):
    def test_refuses_other_layouts(self):
        d = made_up_save()
        with self.assertRaises(st.SaveError):
            st.Save(bytes(d[:-1]))
        bad = bytearray(d)
        struct.pack_into('<I', bad, 4, 0x1A)
        with self.assertRaises(st.SaveError):
            st.Save(bytes(bad))
        self.assertEqual(st.Save(bytes(d)).version, 0xD004)


class RuleTest(unittest.TestCase):
    def setUp(self):
        self.s = st.Save(bytes(made_up_save()))
        valid_rule(self.s, 'smash', 0, 'Arena test')

    def test_decode(self):
        r = self.s.rule('smash', 0)
        self.assertTrue(r['in_use'])
        self.assertEqual((r['name'], r['style'], r['time_s'], r['stock'], r['hp'], r['launch_rate']), ('Arena test', 'stock', 150, 3, 150, 1.0))
        self.assertTrue(r['name_hash_ok'])
        self.assertEqual(len(self.s.rules('smash')), 1)

    def test_roundtrip_garbage(self):
        c = self.s.roundtrip()
        self.assertEqual(bytes(c.d), bytes(self.s.d))

    def test_unchanged_write_is_identical(self):
        before = bytes(self.s.d)
        self.s.set_rule('smash', 0, self.s.rule('smash', 0))
        self.assertEqual(bytes(self.s.d), before)            # nothing changed: not even the counter

    def test_write_fields_offsets_counter(self):
        o = self.s.rule_offset('smash', 0)
        self.s.set_rule('smash', 0, {'time_s': 420, 'fs_meter': False, 'item_frequency': 0, 'launch_rate': 1.5})
        self.assertEqual(struct.unpack_from('<H', self.s.d, o + 0x14)[0], 420)
        self.assertEqual(self.s.d[o + 0x1e], 15)
        self.assertEqual(self.s.d[o + 0x28], 0)
        self.assertEqual(self.s.counter, 101)
        self.assertEqual(struct.unpack_from('<I', self.s.d, o + 0x4c)[0], 101)

    def test_new_set_and_name(self):
        slot = self.s.free_rule_slot('smash')
        self.assertEqual(slot, 1)
        valid = self.s.rule('smash', 0)
        valid.update({'in_use': True, 'name': 'Second'})
        valid.pop('raw_head')
        self.s.set_rule('smash', slot, valid)
        o = self.s.rule_offset('smash', slot)
        r = self.s.rule('smash', slot)
        self.assertEqual((r['name'], r['created'], r['modified']), ('Second', 101, 101))
        self.assertEqual(struct.unpack_from('<II', self.s.d, o + 0x50), (st.name_hash(st.units_of('Second')), 6))
        self.assertEqual(bytes(self.s.d[o + 0x58 + 12:o + 0x58 + 46]), bytes(34))   # NUL and zeros after the name

    def test_limits(self):
        for bad in ({'time_s': 30}, {'stock': 0}, {'launch_rate_x10': 21}, {'stage_select': 8}, {'style': 'coins'},
                    {'name': 'x' * 23}, {'nope': 1}):
            s = st.Save(bytes(self.s.d))
            with self.assertRaises(st.SaveError, msg=str(bad)):
                s.set_rule('smash', 0, bad)


class ProfileTest(unittest.TestCase):
    def setUp(self):
        self.s = st.Save(bytes(made_up_save(2)))
        o = self.s.profile_offset(1)
        self.s.d[o] = 1
        default = bytes.fromhex('0303040a0b0c000105020201010101040403030a0b0c0001050202010101010d0d04030200020101010101')
        self.s.d[o + 0x24:o + 0x4f] = default
        st.write_name(self.s.d, o + 4, o + 8, o + 0xc, st.PROFILE_NAME_UNITS, st.PROFILE_NAME_MAX, 'Tester')

    def test_decode(self):
        p = self.s.profile(1)
        self.assertEqual(p['name'], 'Tester')
        self.assertEqual(p['gamecube']['buttons']['A'], 'attack')
        self.assertEqual(p['gamecube']['buttons']['C_STICK'], 'smash_attack')
        self.assertEqual(p['pro']['buttons']['ZL'], 'shield')
        self.assertEqual(p['pro']['buttons']['L'], 'grab')
        self.assertEqual([x['slot'] for x in self.s.profiles()], [1])

    def test_write_buttons(self):
        o = self.s.profile_offset(1)
        self.s.set_profile(1, {'pro': {'buttons': {'B': 'jump', 'X': 'special'}, 'options': {'opt3': 2}}})
        self.assertEqual(self.s.d[o + 0x33 + 8], 2)
        self.assertEqual(self.s.d[o + 0x33 + 10], 1)
        self.assertEqual(self.s.d[o + 0x33 + 12 + 3], 2)
        self.assertEqual(self.s.counter, 101)
        with self.assertRaises(st.SaveError):
            self.s.set_profile(1, {'pro': {'buttons': {'C_STICK': 'jump'}}})
        with self.assertRaises(st.SaveError):
            self.s.set_profile(1, {'name': 'elevenchars'})

    def test_roundtrip(self):
        self.assertEqual(bytes(self.s.roundtrip().d), bytes(self.s.d))

    def test_delete_as_the_game(self):
        o = self.s.profile_offset(1)
        self.s.d[o + 0x100] = 7                                # a record byte: goes with the tag
        self.s.set_profile(1, {'pro': {'buttons': {'B': 'jump'}}})
        n = self.s.counter
        self.s.delete_profile(1)
        self.assertEqual(self.s.counter, n + 1)
        e = bytes(self.s.d[o:o + st.PROFILE_SIZE])
        self.assertEqual(e[0x24:0x4f], st.DEFAULT_CONTROLS)
        self.assertEqual(e[:0x24] + e[0x4f:], bytes(st.PROFILE_SIZE - 0x2b))
        self.assertEqual(self.s.profiles(), [])
        with self.assertRaises(st.SaveError):
            self.s.delete_profile(0)


class CliTest(unittest.TestCase):
    def test_in_place_keeps_a_copy(self):
        with tempfile.TemporaryDirectory() as tmp:
            sd = os.path.join(tmp, 'save', 'save_data')
            os.makedirs(sd)
            path = os.path.join(sd, 'system_data.bin')
            s = st.Save(bytes(made_up_save(3)))
            valid_rule(s, 'smash', 0, 'One')
            open(path, 'wb').write(bytes(s.d))
            js = os.path.join(tmp, 'r.json')
            json.dump({'stock': 2}, open(js, 'w'))
            self.assertEqual(st.main(['set-rule', path, '--section', 'smash', '--slot', '0', '--json', js]), 0)
            self.assertEqual(len(glob.glob(path + '.bak-*')), 1)
            self.assertEqual(st.Save.load(path).rule('smash', 0)['stock'], 2)
            open(os.path.join(tmp, 'save.commit'), 'w').close()      # the game has a commit to finish: refused
            self.assertEqual(st.main(['set-rule', path, '--section', 'smash', '--slot', '0', '--json', js]), 2)
            self.assertEqual(st.main(['roundtrip', path]), 0)


class SamplesTest(unittest.TestCase):
    def test_real_copies(self):
        folder = os.environ.get('SSBU_SAVE_SAMPLES')
        if not folder:
            self.skipTest('SSBU_SAVE_SAMPLES not set')
        files = glob.glob(os.path.join(folder, '**', '*.bin'), recursive=True)
        for f in files:
            try:
                s = st.Save.load(f)
            except st.SaveError:
                continue
            self.assertEqual(bytes(s.roundtrip().d), bytes(s.d), f)
            for r in s.rules():
                c = st.Save(bytes(s.d))
                c.set_rule(r['section'], r['slot'], r, check=False)
                self.assertEqual(bytes(c.d), bytes(s.d), '%s %s %d' % (f, r['section'], r['slot']))


if __name__ == '__main__':
    unittest.main()
