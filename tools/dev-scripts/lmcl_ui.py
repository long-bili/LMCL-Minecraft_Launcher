"""Drive the LMCL WPF window: click inside it and screenshot it.

The stock click.py matches windows by title, and "LMCL" also matches the
browser tab title, so it refuses to act. This variant matches the WPF window
class (HwndWrapper[LMCL;...]) instead, which is unique.

Usage: lmcl_ui.py click <x> <y>      -- click window-relative coords
       lmcl_ui.py shot <out.png>     -- screenshot the window
"""
import ctypes
import ctypes.wintypes as w
import struct
import sys
import time
import zlib

u32 = ctypes.windll.user32
g32 = ctypes.windll.gdi32

PW_RENDERFULLCONTENT = 2


def find_lmcl():
    """Return the HWND of the main LMCL window (matched by WPF window class)."""
    hits = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, w.HWND, w.LPARAM)
    def cb(hwnd, _):
        if not u32.IsWindowVisible(hwnd):
            return True
        cls = ctypes.create_unicode_buffer(512)
        u32.GetClassNameW(hwnd, cls, 512)
        # HwndWrapper[LMCL;;<guid>] is the main window; dialogs get their own
        if cls.value.startswith('HwndWrapper[LMCL;'):
            r = w.RECT()
            u32.GetWindowRect(hwnd, ctypes.byref(r))
            hits.append((hwnd, (r.right - r.left) * (r.bottom - r.top), cls.value))
        return True

    u32.EnumWindows(cb, 0)
    if not hits:
        raise SystemExit('LMCL window not found')
    # the main window is the biggest one
    return max(hits, key=lambda t: t[1])[0]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [('dx', w.LONG), ('dy', w.LONG), ('mouseData', w.DWORD),
                ('dwFlags', w.DWORD), ('time', w.DWORD),
                ('dwExtraInfo', ctypes.POINTER(w.ULONG))]


class INPUT(ctypes.Structure):
    _fields_ = [('type', w.DWORD), ('mi', MOUSEINPUT)]


def click(hwnd, x, y):
    u32.ShowWindow(hwnd, 9)
    u32.SetForegroundWindow(hwnd)
    time.sleep(0.25)

    r = w.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    sx, sy = r.left + x, r.top + y
    smx, smy = u32.GetSystemMetrics(0), u32.GetSystemMetrics(1)
    nx = int(sx * 65535 / (smx - 1))
    ny = int(sy * 65535 / (smy - 1))

    for _ in range(3):
        mi = MOUSEINPUT(nx, ny, 0, 0x0001 | 0x8000, 0, None)
        u32.SendInput(1, ctypes.byref(INPUT(type=0, mi=mi)), ctypes.sizeof(INPUT))
        time.sleep(0.08)
        mi = MOUSEINPUT(nx, ny, 0, 0x0002, 0, None)
        u32.SendInput(1, ctypes.byref(INPUT(type=0, mi=mi)), ctypes.sizeof(INPUT))
        time.sleep(0.05)
        mi = MOUSEINPUT(nx, ny, 0, 0x0004, 0, None)
        u32.SendInput(1, ctypes.byref(INPUT(type=0, mi=mi)), ctypes.sizeof(INPUT))
        time.sleep(0.12)
    print(f'clicked ({x},{y}) -> screen ({sx},{sy})')


class BMIH(ctypes.Structure):
    _fields_ = [('biSize', w.DWORD), ('biWidth', ctypes.c_long),
                ('biHeight', ctypes.c_long), ('biPlanes', w.WORD),
                ('biBitCount', w.WORD), ('biCompression', w.DWORD),
                ('biSizeImage', w.DWORD), ('biXPelsPerMeter', ctypes.c_long),
                ('biYPelsPerMeter', ctypes.c_long), ('biClrUsed', w.DWORD),
                ('biClrImportant', w.DWORD)]


def shot(hwnd, out):
    u32.ShowWindow(hwnd, 9)
    u32.SetForegroundWindow(hwnd)
    time.sleep(0.9)

    r = w.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    width, height = r.right - r.left, r.bottom - r.top

    dc = u32.GetWindowDC(hwnd)
    mem = g32.CreateCompatibleDC(dc)
    bmp = g32.CreateCompatibleBitmap(dc, width, height)
    g32.SelectObject(mem, bmp)
    ok = u32.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)

    hdr = BMIH()
    hdr.biSize = ctypes.sizeof(BMIH)
    hdr.biWidth, hdr.biHeight = width, -height
    hdr.biPlanes, hdr.biBitCount, hdr.biCompression = 1, 32, 0

    buf = ctypes.create_string_buffer(width * height * 4)
    g32.GetDIBits(mem, bmp, 0, height, buf, ctypes.byref(hdr), 0)

    raw = buf.raw
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        row = raw[y * width * 4:(y + 1) * width * 4]
        rows += bytes(b for x in range(width)
                      for b in (row[x * 4 + 2], row[x * 4 + 1], row[x * 4], 255))

    def chunk(tag, data):
        body = tag + data
        return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body) & 0xffffffff)

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(bytes(rows), 6))
    png += chunk(b'IEND', b'')

    with open(out, 'wb') as f:
        f.write(png)

    g32.DeleteObject(bmp)
    g32.DeleteDC(mem)
    u32.ReleaseDC(hwnd, dc)
    print(f'PrintWindow={ok} {width}x{height} -> {out}')


if __name__ == '__main__':
    cmd = sys.argv[1]
    hwnd = find_lmcl()
    if cmd == 'click':
        click(hwnd, int(sys.argv[2]), int(sys.argv[3]))
    elif cmd == 'shot':
        shot(hwnd, sys.argv[2])
    else:
        raise SystemExit(f'unknown command {cmd}')
