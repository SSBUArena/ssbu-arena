"""data.arc as a file, wherever its bytes are (read-only; contains nothing of the game).

A RomFS folder holds the game's archive in one of three forms:
  data.arc    the archive itself (a dump made by the installer)
  data.arcp   the light archive (runtime/arcpack.c, tools/competitive/pack_arc.py): kept ranges, holes read as zeros
  data.arcl   a LINKED archive (shared game data, scripts/shared_gamedata.py): no bytes of its own, only a map saying
              where each byte range of data.arc is in OTHER files (another product's game data on this PC, e.g. the
              base and update files of an OpenSmash Ultimate install). The runtime reads it the same way (arcpack.c).

data.arcl format (little-endian):
    "SSBUARCL" | u64 size of data.arc | u32 source count | u32 range count
    sources: u64 size | u64 last-write time (Windows FILETIME) | u32 path bytes | path (UTF-8, absolute)
    ranges, sorted by offset, covering [0, size) with no gap:
        u64 offset in data.arc | u64 length | u64 offset in the source | u32 source (0xFFFFFFFF: zeros) | u32 0
A source whose size or write time is no longer the recorded one makes the map stale: it is refused (never read), and the
installer is run again (the other product changed or removed its files).

    open_arc(path)   path names data.arc; returns a binary file object for whichever form is there
    arc_exists(path) True when one of the forms is there
"""
import io
import os
import struct

ARCL_MAGIC = b'SSBUARCL'
ZEROS = 0xFFFFFFFF


class StaleLink(OSError):
    pass


def file_stamp(path):
    """(size, write time as a Windows FILETIME) of a file; the FILETIME is what the runtime reads (GetFileInformationByHandle)."""
    st = os.stat(path)
    return st.st_size, st.st_mtime_ns // 100 + 116444736000000000


def write_arcl(path, size, sources, ranges):
    """sources: [path]; ranges: [(offset, length, source offset, source index or ZEROS)] in order, covering [0, size)."""
    pos = 0
    for off, ln, _, _ in ranges:
        if off != pos or ln <= 0:
            raise ValueError('data.arcl ranges must cover the archive in order (gap or overlap at %#x)' % pos)
        pos += ln
    if pos != size:
        raise ValueError('data.arcl ranges cover %d of %d bytes' % (pos, size))
    out = bytearray(ARCL_MAGIC + struct.pack('<QII', size, len(sources), len(ranges)))
    for p in sources:
        p = os.path.abspath(p)
        sz, mt = file_stamp(p)
        b = p.encode('utf-8')
        out += struct.pack('<QQI', sz, mt, len(b)) + b
    for off, ln, at, src in ranges:
        out += struct.pack('<QQQII', off, ln, at, src, 0)
    tmp = path + '.tmp'
    with open(tmp, 'wb') as f:
        f.write(out)
    os.replace(tmp, path)


def read_arcl(path):
    """-> (size, [(path, size, mtime)], [(offset, length, source offset, source)])"""
    with open(path, 'rb') as f:
        d = f.read()
    if d[:8] != ARCL_MAGIC:
        raise OSError('%s: not a linked archive' % path)
    size, ns, nr = struct.unpack_from('<QII', d, 8)
    p, srcs = 24, []
    for _ in range(ns):
        sz, mt, n = struct.unpack_from('<QQI', d, p)
        p += 20
        srcs.append((d[p:p + n].decode('utf-8'), sz, mt))
        p += n
    ranges = [struct.unpack_from('<QQQII', d, p + 32 * i)[:4] for i in range(nr)]
    return size, srcs, ranges


def arcl_problems(path):
    """[] when every source of the linked archive is there with its recorded size and write time; else what is wrong."""
    try:
        size, srcs, ranges = read_arcl(path)
    except (OSError, struct.error, UnicodeDecodeError) as e:
        return ['%s cannot be read (%s)' % (path, e)]
    bad = []
    for p, sz, mt in srcs:
        try:
            if file_stamp(p) != (sz, mt):
                bad.append('%s changed since the link was made (size or date)' % p)
        except OSError:
            bad.append('%s is not there any more' % p)
    if not ranges or sum(r[1] for r in ranges) != size:
        bad.append('%s does not cover the whole archive' % path)
    return bad


class ArclFile(io.RawIOBase):
    """Read-only file over a data.arcl map. The sources are checked (size, write time) when it is opened."""

    def __init__(self, path):
        super().__init__()
        bad = arcl_problems(path)
        if bad:
            raise StaleLink('the shared game data cannot be used: ' + '; '.join(bad) + '. Run the installer again (Repair).')
        self.name = path
        self.size, srcs, self.ranges = read_arcl(path)
        self.starts = [r[0] for r in self.ranges]
        self.files = [open(p, 'rb') for p, _, _ in srcs]
        self.pos = 0

    def readable(self):
        return True

    def seekable(self):
        return True

    def tell(self):
        return self.pos

    def seek(self, off, whence=0):
        self.pos = off if whence == 0 else self.pos + off if whence == 1 else self.size + off
        return self.pos

    def pread(self, off, size):
        import bisect
        size = max(0, min(size, self.size - off))
        out = bytearray()
        k = bisect.bisect_right(self.starts, off) - 1
        while size > 0:
            roff, ln, at, src = self.ranges[k]
            n = min(size, roff + ln - off)
            if src == ZEROS:
                out += bytes(n)
            else:
                f = self.files[src]
                f.seek(at + (off - roff))
                chunk = f.read(n)
                if len(chunk) != n:
                    raise OSError('%s: short read in a source of the linked archive' % self.name)
                out += chunk
            off += n
            size -= n
            k += 1
        return bytes(out)

    def readinto(self, b):
        data = self.pread(self.pos, len(b))
        b[:len(data)] = data
        self.pos += len(data)
        return len(data)

    def read(self, n=-1):
        if n is None or n < 0:
            n = self.size - self.pos
        data = self.pread(self.pos, n)
        self.pos += len(data)
        return data

    def close(self):
        for f in getattr(self, 'files', []):
            f.close()
        super().close()


def arc_form(path):
    """'arc', 'arcl' or None: which form of the archive named by path (…\\data.arc) is there (data.arcp is not read here)."""
    if os.path.isfile(path):
        return 'arc'
    if os.path.isfile(path + 'l'):
        return 'arcl'
    return None


def arc_exists(path):
    return arc_form(path) is not None


def arc_size(path):
    if os.path.isfile(path):
        return os.path.getsize(path)
    return read_arcl(path + 'l')[0]


def open_arc(path):
    """A binary file object for data.arc (path), or for its linked form data.arcl when data.arc is not there."""
    if os.path.isfile(path) or not os.path.isfile(path + 'l'):
        return open(path, 'rb')
    return io.BufferedReader(ArclFile(path + 'l'), buffer_size=1 << 20)
