# -*- coding: utf-8 -*-
"""对 LMCL 窗口的某一区域做像素指纹（md5），用来判断「连续切换页面后内容有没有偏移」。
用法: python drift_test.py <x> <y> <w> <h>
"""
import sys
import hashlib

sys.path.insert(0, ".")
import shot


def region_hash(x, y, w, h):
    wins = [w for w in shot.list_windows() if w["title"] == "LMCL"]
    if not wins:
        return None
    win = max(wins, key=lambda t: t["area"])
    hwnd, W, H = win["hwnd"], win["w"], win["h"]
    raw = shot.capture(hwnd, W, H)  # BGRA 自上而下
    out = bytearray()
    for yy in range(y, min(y + h, H)):
        out += raw[(yy * W + x) * 4:(yy * W + x + w) * 4]
    return hashlib.md5(out).hexdigest()


if __name__ == "__main__":
    x, y, w, h = (int(a) for a in sys.argv[1:5])
    print(region_hash(x, y, w, h))
