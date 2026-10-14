# -*- coding: utf-8 -*-
"""
给指定窗口发点击 / 按键（纯 ctypes）。

用法：
    python click.py <窗口标题关键字> <x> <y> [--dbl] [--right] [--key VK] [--type TEXT]

坐标是窗口**客户区**坐标（左上角 0,0），脚本内部会换算成屏幕坐标。
之所以不用 PostMessage：WPF 对 WM_LBUTTONDOWN 的合成消息不总认，
直接摆真实光标再 mouse_event 最贴近人手。
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except OSError:
    try:
        user32.SetProcessDPIAware()
    except OSError:
        pass

WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010
MOUSEEVENTF_WHEEL = 0x0800
KEYEVENTF_UNICODE = 0x0004
KEYEVENTF_KEYUP = 0x0002
INPUT_KEYBOARD = 1


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(wt.ULONG))]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", ctypes.c_long), ("dy", ctypes.c_long), ("mouseData", wt.DWORD),
                ("dwFlags", wt.DWORD), ("time", wt.DWORD),
                ("dwExtraInfo", ctypes.POINTER(wt.ULONG))]


# 联合体必须按**最大的那个成员**来定尺寸：64 位下 MOUSEINPUT 是 32 字节，
# 只按 KEYBDINPUT(24) 声明的话 sizeof(INPUT) 会算成 32 而不是 40，
# SendInput 拿到错的 cbSize 会直接返回 0（ERROR_INVALID_PARAMETER），
# 表现就是「点了输入框但一个字也没输进去」，而且不报任何错。
class _INPUTunion(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("mi", MOUSEINPUT), ("pad", ctypes.c_byte * 32)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _INPUTunion)]


def find(keyword):
    hits = []

    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 2)
        user32.GetWindowTextW(hwnd, buf, n + 2)
        r = RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top
        if w > 200 and h > 200 and keyword.lower() in buf.value.lower():
            hits.append((hwnd, buf.value, r, w * h))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return max(hits, key=lambda t: t[3]) if hits else None


def enumerate_all():
    out = []

    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 2)
        user32.GetWindowTextW(hwnd, buf, n + 2)
        c = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, c, 256)
        r = RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top
        if w > 60 and h > 20:
            out.append((hwnd, f"{c.value} | {buf.value}", r, w * h))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return out


def client_origin(hwnd):
    """客户区左上角在屏幕上的坐标（用 ClientToScreen 更准，避免边框/标题栏偏差）。"""
    p = POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(p))
    return p.x, p.y


def focus(hwnd, topmost=True):
    """把窗口弄到最前面。

    只调 SetForegroundWindow 不够：从后台进程调用时系统常常直接拒掉
    （返回 0，窗口还在别人下面），后果是后续的点击全落到挡住它的那个窗口上。
    这里先 ShowWindow 还原，再 SetWindowPos 置顶，最后才 SetForegroundWindow。

    topmost=False 用于**截图**：把窗口设成置顶之后，它自己拉出来的 Popup
    （WPF 的下拉菜单是普通层级的独立窗口，不是置顶窗口）会被压在主窗口下面，
    截出来就是"菜单半透明、底下的按钮透出来"——纯属测量方式造成的假象。
    """
    SW_RESTORE = 9
    HWND_TOPMOST = ctypes.c_void_p(-1)
    SWP_NOMOVE = 0x0002
    SWP_NOSIZE = 0x0001
    SWP_SHOWWINDOW = 0x0040

    flags = SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW
    if user32.IsIconic(hwnd):
        user32.ShowWindow(hwnd, SW_RESTORE)

    if topmost:
        user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags)

    user32.SetForegroundWindow(hwnd)
    user32.BringWindowToTop(hwnd)
    time.sleep(0.35)


def drop_topmost(hwnd):
    HWND_NOTOPMOST = ctypes.c_void_p(-2)
    SWP_NOMOVE = 0x0002
    SWP_NOSIZE = 0x0001
    user32.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE)


def move(x, y):
    user32.SetCursorPos(x, y)
    time.sleep(0.06)


def click(x, y, right=False, dbl=False):
    move(x, y)
    down = MOUSEEVENTF_RIGHTDOWN if right else MOUSEEVENTF_LEFTDOWN
    up = MOUSEEVENTF_RIGHTUP if right else MOUSEEVENTF_LEFTUP
    for _ in range(2 if dbl else 1):
        user32.mouse_event(down, 0, 0, 0, 0)
        time.sleep(0.04)
        user32.mouse_event(up, 0, 0, 0, 0)
        time.sleep(0.06)


def type_text(text):
    """逐字符走 unicode 注入，中文也能打。"""
    sent = 0
    for ch in text:
        for flags in (KEYEVENTF_UNICODE, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP):
            inp = INPUT(type=INPUT_KEYBOARD)
            inp.u.ki = KEYBDINPUT(0, ord(ch), flags, 0, None)
            n = user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))
            if n == 0:
                err = ctypes.get_last_error()
                print(f"SendInput 失败（字符 {ch!r} 字段 {flags}）：错误码 {err}，"
                      f"sizeof(INPUT)={ctypes.sizeof(INPUT)}")
                return False
            sent += 1
            time.sleep(0.012)
    print(f"已注入 {sent} 个按键事件")
    return True


def press(vk):
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.04)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)


VK = {"enter": 0x0D, "tab": 0x09, "esc": 0x1B, "down": 0x28, "up": 0x26,
      "space": 0x20, "back": 0x08, "a": 0x41, "c": 0x43, "v": 0x56,
      "f4": 0x73, "menu": 0x5D, "alt": 0x12}


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        return 1

    keyword = sys.argv[1]
    x, y = int(sys.argv[2]), int(sys.argv[3])

    found = find(keyword)
    if not found:
        print(f"没找到标题含 {keyword!r} 的窗口")
        return 2
    hwnd, title, _, _ = found
    print(f"窗口: {title!r} hwnd={hwnd}")

    focus(hwnd, topmost="--soft" not in sys.argv)
    ox, oy = client_origin(hwnd)
    print(f"客户区原点: ({ox},{oy})  点击目标: ({ox + x},{oy + y})")

    try:
        return act(hwnd, ox, oy, x, y)
    finally:
        # focus() 是留着置顶给截图用的，收尾必须撤掉，不然窗口会一直悬在最上面
        if "--keep-top" not in sys.argv:
            drop_topmost(hwnd)


def act(hwnd, ox, oy, x, y):
    if "--move" in sys.argv:
        # 把窗口摆到指定屏幕坐标。副屏在上次会话里把窗口挪过去了，截图抓到一片虚空
        i = sys.argv.index("--move")
        mx, my = int(sys.argv[i + 1]), int(sys.argv[i + 2])
        user32.SetWindowPos(hwnd, 0, mx, my, 0, 0, 0x0001 | 0x0004)  # NOSIZE | NOZORDER
        time.sleep(0.3)
        print(f"已移动到 ({mx},{my})")
        return finish(hwnd)

    if "--key" in sys.argv:
        k = sys.argv[sys.argv.index("--key") + 1].lower()
        press(VK[k])
        move(ox + x, oy + y)
        click(ox + x, oy + y)
        return finish(hwnd)

    if "--type" in sys.argv:
        t = sys.argv[sys.argv.index("--type") + 1]
        click(ox + x, oy + y)
        time.sleep(0.12)
        fg = user32.GetForegroundWindow()
        print(f"键入前的前台窗口 hwnd={fg}（目标 {hwnd}）"
              f"{'，一致' if fg == hwnd else '，**不一致**'}")
        if fg != hwnd:
            # 前台不是它，SendInput 打出去的字符会跑到别的窗口去
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.25)
            print(f"  再取一次前台：hwnd={user32.GetForegroundWindow()}")
        type_text(t)
        return finish(hwnd)

    if "--wheel" in sys.argv:
        d = int(sys.argv[sys.argv.index("--wheel") + 1])
        move(ox + x, oy + y)
        user32.mouse_event(MOUSEEVENTF_WHEEL, 0, 0, d, 0)
        return finish(hwnd)

    if "--hold" in sys.argv:
        # 按住不放，在按住期间截图 —— 有些 popup 在鼠标抬起时立刻收起，
        # 分开两个进程抓根本来不及
        move(ox + x, oy + y)
        user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
        time.sleep(0.9)
        r0 = RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r0))
        user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
        return finish(hwnd)

    click(ox + x, oy + y,
          right="--right" in sys.argv,
          dbl="--dbl" in sys.argv)
    return finish(hwnd)


def finish(hwnd):
    """点完之后可选：枚举窗口 / 截图。

    截图放在**同一个进程**里做是有意的——分两次调用之间，别的窗口会跑到前面来，
    截出来就是一片别人的界面。
    """
    if "--enum" in sys.argv:
        time.sleep(0.35)
        print("--- 点击后的可见窗口 ---")
        for h, t, r, a in enumerate_all():
            print(f"  hwnd={h} {t[:40]!r} rect=({r.left},{r.top},{r.right},{r.bottom})")

    if "--shot" in sys.argv:
        import shot
        png = sys.argv[sys.argv.index("--shot") + 1]
        time.sleep(0.55)
        r0 = RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r0))
        raw, w, h = shot.capture_screen(r0.left, r0.top,
                                        r0.right - r0.left, r0.bottom - r0.top)
        shot.write_png(png, raw, w, h)
        print(f"已保存 {png}（{w}x{h}）")

    if "--pixel" in sys.argv:
        # 取屏幕上的像素值。分两个进程做不行——后一个进程跑起来时菜单可能已经被关掉了
        import shot
        time.sleep(0.5)
        for spec in sys.argv[sys.argv.index("--pixel") + 1:]:
            if spec.startswith("--"):
                break
            px, py = (int(v) for v in spec.split(","))
            raw, _, _ = shot.capture_screen(px, py, 1, 1, pad=0)
            b, g, r = raw[0], raw[1], raw[2]
            print(f"  ({px},{py}) → #{r:02X}{g:02X}{b:02X}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
