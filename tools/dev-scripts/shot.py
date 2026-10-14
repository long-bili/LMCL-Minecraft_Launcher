# -*- coding: utf-8 -*-
"""
窗口截图工具（纯 ctypes，不依赖 Pillow / pywin32）。

用法：
    python shot.py <输出png> [窗口标题关键字] [--pid N] [--class CLASS]

策略：
  - 枚举所有可见的顶层窗口
  - 如果有 title 关键字就按标题模糊匹配，否则按面积取最大的那个
  - PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2) 能抓到 DirectComposition 合成的内容
  - 手工编 PNG（zlib + struct），不引第三方库
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import zlib

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

# 必须在任何 GetWindowRect 之前设置，否则拿到的是被 DPI 虚拟化过的逻辑坐标，
# 抓出来的图会被裁掉右下角
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)   # PER_MONITOR_AWARE_V2
except OSError:
    try:
        user32.SetProcessDPIAware()
    except OSError:
        pass

PW_RENDERFULLCONTENT = 0x00000002
SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0

WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD),
        ("biWidth", ctypes.c_long),
        ("biHeight", ctypes.c_long),
        ("biPlanes", wt.WORD),
        ("biBitCount", wt.WORD),
        ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD),
        ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long),
        ("biClrUsed", wt.DWORD),
        ("biClrImportant", wt.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def list_windows(pid=None):
    out = []

    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        # 无标题的也留着，可能靠 class / pid 匹配
        buf = ctypes.create_unicode_buffer(n + 2)
        user32.GetWindowTextW(hwnd, buf, n + 2)
        title = buf.value

        r = RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top

        cbuf = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cbuf, 256)

        wpid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))

        if w > 40 and h > 40:
            out.append({
                "hwnd": hwnd, "title": title, "class": cbuf.value,
                "w": w, "h": h, "pid": wpid.value,
                "area": w * h, "x": r.left, "y": r.top,
            })
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    if pid:
        out = [w for w in out if w["pid"] == pid]
    return out


def capture(hwnd, w, h):
    hdc = user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)

    ok = user32.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)
    if not ok:
        gdi32.BitBlt(mem, 0, 0, w, h, hdc, 0, 0, SRCCOPY)

    raw = read_dib(mem, bmp, w, h)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(hwnd, hdc)
    return raw


# 窗口下方多抓多少像素。WPF 的下拉菜单、右键菜单都是独立顶层窗口，
# 挂在目标窗口下方，不留余量就正好把它们切掉。
PAD_BOTTOM = 300


def capture_screen(x, y, w, h, pad=PAD_BOTTOM):
    """从屏幕 DC 抓一块区域，返回 (像素数据, 实际宽, 实际高)。

    尺寸会被裁到屏幕范围内，所以**必须**用返回的宽高去编码 PNG——
    调用方按自己算的尺寸去写就会越界（这是个已经踩过的坑）。
    """
    sw = user32.GetSystemMetrics(0)
    sh = user32.GetSystemMetrics(1)
    x = max(0, x)
    y = max(0, y)
    w = max(1, min(w, sw - x))
    h = max(1, min(h + pad, sh - y))

    screen = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(screen)
    bmp = gdi32.CreateCompatibleBitmap(screen, w, h)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY)

    raw = read_dib(mem, bmp, w, h)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(0, screen)
    return raw, w, h


def read_dib(mem, bmp, w, h):
    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h          # 负数 = 自上而下
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = 0

    size = w * h * 4
    buf = ctypes.create_string_buffer(size)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), DIB_RGB_COLORS)
    return buf.raw


def write_png(path, raw, w, h):
    rows = bytearray()
    stride = w * 4
    for y in range(h):
        rows.append(0)                              # filter type 0
        line = raw[y * stride:(y + 1) * stride]
        # BGRA -> RGBA（把 B、R 两通道对调，A 补成不透明）
        out = bytearray(stride)
        for i in range(0, stride, 4):
            out[i] = line[i + 2]
            out[i + 1] = line[i + 1]
            out[i + 2] = line[i]
            out[i + 3] = 255
        rows.extend(out)

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(rows), 6))
    png += chunk(b"IEND", b"")

    with open(path, "wb") as f:
        f.write(png)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    out = sys.argv[1]
    keyword = None
    pid = None
    cls = None

    if out == "--list":
        for w in sorted(list_windows(), key=lambda x: -x["area"]):
            print(f"[{w['pid']:>6}] {w['class']:<34} {w['title'][:44]!r:<48} {w['w']}x{w['h']}")
        return 0

    # --region X Y W H：直接抓屏幕上一块矩形。
    # 整窗口截图会被阅读器缩放到装得下，细节就糊了；要看某几个像素的细节只能这么抓。
    if "--region" in sys.argv:
        i = sys.argv.index("--region")
        rx, ry, rw, rh = (int(v) for v in sys.argv[i + 1:i + 5])
        raw, cw, ch = capture_screen(rx, ry, rw, rh, pad=0)
        write_png(out, raw, cw, ch)
        print(f"已保存 {out}（{cw}x{ch}）")
        return 0

    # --pixel X1,Y1 X2,Y2 …：打印屏幕上指定点的颜色。
    # 判断「半透明层到底透了多少」靠肉眼看不准，直接读像素最实在。
    if out == "--pixel":
        for spec in sys.argv[2:]:
            px, py = (int(v) for v in spec.split(","))
            raw, _, _ = capture_screen(px, py, 1, 1, pad=0)
            b, g, r = raw[0], raw[1], raw[2]
            print(f"({px},{py}) → #{r:02X}{g:02X}{b:02X}")
        return 0

    i = 2
    while i < len(sys.argv):
        a = sys.argv[i]
        if a == "--pid":
            i += 1
            pid = int(sys.argv[i])
        elif a == "--class":
            i += 1
            cls = sys.argv[i]
        elif a.startswith("--"):
            pass                    # 布尔开关，留给下面判断
        else:
            keyword = a
        i += 1

    wins = list_windows(pid)
    if not wins:
        print("没有找到可见窗口")
        return 2

    if cls:
        wins = [w for w in wins if cls.lower() in w["class"].lower()]
    if keyword:
        wins = [w for w in wins if keyword.lower() in w["title"].lower()]

    if not wins:
        print("没有匹配的窗口。候选：")
        for w in list_windows(pid):
            print(f"  [{w['pid']}] {w['class']!r} {w['title']!r} {w['w']}x{w['h']}")
        return 3

    # 取面积最大的：主窗口通常比提示框大
    w = max(wins, key=lambda x: x["area"])
    print(f"抓取: [{w['pid']}] {w['class']} {w['title']!r} {w['w']}x{w['h']}")

    if "--screen" in sys.argv:
        raw, sw, sh = capture_screen(w["x"], w["y"], w["w"], w["h"])
        write_png(out, raw, sw, sh)
    else:
        raw = capture(w["hwnd"], w["w"], w["h"])
        write_png(out, raw, w["w"], w["h"])

    print(f"已保存 {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
