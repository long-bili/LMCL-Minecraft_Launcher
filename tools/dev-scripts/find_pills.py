# -*- coding: utf-8 -*-
"""在 LMCL 窗口里找出所有「强调蓝」色块（含版本列表里的「选择」小药丸）。
用法: python find_pills.py
输出: 每个蓝色块的包围盒 (x, y, w, h) 与中心点，按自上而下排序。
"""
import ctypes
import collections
import sys

sys.path.insert(0, ".")
import shot


def main():
    wins = [w for w in shot.list_windows() if w["title"] == "LMCL"]
    if not wins:
        print("没找到 LMCL 窗口")
        return 1
    w = max(wins, key=lambda x: x["area"])
    hwnd, W, H = w["hwnd"], w["w"], w["h"]
    print(f"窗口 {W}x{H} hwnd={hwnd}")

    raw = shot.capture(hwnd, W, H)  # BGRA 自上而下

    def is_blue(r, g, b):
        return b > 150 and r < 90 and 60 < g < 150

    mask = bytearray(W * H)
    for y in range(H):
        base = y * W
        for x in range(W):
            i = (base + x) * 4
            if is_blue(raw[i + 2], raw[i + 1], raw[i]):
                mask[base + x] = 1

    visited = bytearray(W * H)
    boxes = []
    for y in range(H):
        base = y * W
        for x in range(W):
            p = base + x
            if not mask[p] or visited[p]:
                continue
            q = collections.deque([p])
            visited[p] = 1
            minx = maxx = x
            miny = maxy = y
            cnt = 0
            while q:
                c = q.popleft()
                cy, cx = divmod(c, W)
                cnt += 1
                if cx < minx:
                    minx = cx
                if cx > maxx:
                    maxx = cx
                if cy < miny:
                    miny = cy
                if cy > maxy:
                    maxy = cy
                for dy in (-1, 0, 1):
                    for dx in (-1, 0, 1):
                        nx, ny = cx + dx, cy + dy
                        if 0 <= nx < W and 0 <= ny < H:
                            np_ = ny * W + nx
                            if mask[np_] and not visited[np_]:
                                visited[np_] = 1
                                q.append(np_)
            if cnt > 60:
                boxes.append((minx, miny, maxx - minx + 1, maxy - miny + 1, cnt))

    boxes.sort(key=lambda b: (b[1], b[0]))
    print(f"共 {len(boxes)} 个蓝色块:")
    for bx, by, bw, bh, cnt in boxes:
        cx, cy = bx + bw // 2, by + bh // 2
        print(f"  box=({bx},{by}) size={bw}x{bh} center=({cx},{cy}) px={cnt}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
