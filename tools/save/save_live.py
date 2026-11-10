"""Study tool: read / write a running game's live save (13.0.4: the game's addresses are the process's). Not used by the launcher.

    python tools/save/save_live.py <exe name> save <out file>          the live save image (0x5b4af8 bytes) -> file
    python tools/save/save_live.py <exe name> mgr                      the accessor table: each entry -> save offset
    python tools/save/save_live.py <exe name> poke <save off> <hex>    write bytes into the live save
"""
import ctypes, ctypes.wintypes as wt, struct, subprocess, sys

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
k32.OpenProcess.restype = wt.HANDLE
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.WriteProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
SIZE = 0x5B4AF8


def pid_of(name):
    out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq %s' % name, '/FO', 'CSV', '/NH'], capture_output=True, text=True).stdout
    for line in out.splitlines():
        f = [x.strip('"') for x in line.split('","')]
        if len(f) > 1 and f[0].lower() == name.lower():
            return int(f[1])
    raise SystemExit('no process ' + name)


class Proc:
    def __init__(self, name):
        self.h = k32.OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, False, pid_of(name))

    def read(self, a, n):
        buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(a), buf, n, ctypes.byref(got)):
            raise OSError('read %x: %d' % (a, ctypes.get_last_error()))
        return buf.raw[:got.value]

    def q(self, a):
        return struct.unpack('<Q', self.read(a, 8))[0]

    def write(self, a, data):
        got = ctypes.c_size_t()
        if not k32.WriteProcessMemory(self.h, ctypes.c_void_p(a), data, len(data), ctypes.byref(got)):
            raise OSError('write %x: %d' % (a, ctypes.get_last_error()))

    def save_base(self):
        return self.q(self.q(0x7105313508))


def main():
    p = Proc(sys.argv[1]); cmd = sys.argv[2]
    base = p.save_base()
    if cmd == 'save':
        open(sys.argv[3], 'wb').write(p.read(base, SIZE)); print('save at %x' % base)
    elif cmd == 'mgr':
        m = p.q(p.q(0x7105313510))
        print('save %x mgr %x' % (base, m))
        for off in range(0, 0x2b8, 8):
            v = p.q(m + off)
            chain = []
            x = v
            for _ in range(4):
                if base <= x < base + SIZE:
                    chain.append('save+%#x' % (x - base)); break
                try: x = p.q(x)
                except OSError: break
                chain.append('%x' % x)
            print('%#5x %x  %s' % (off, v, ' -> '.join(chain)))
    elif cmd == 'poke':
        off = int(sys.argv[3], 16); data = bytes.fromhex(sys.argv[4])
        p.write(base + off, data); print('wrote %d bytes at save+%#x' % (len(data), off))


if __name__ == '__main__':
    main()
