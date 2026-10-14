# -*- coding: utf-8 -*-
"""造几张假纹理 + 一份带微软账号的 accounts.json，用来看外观界面的实际渲染。

只在本地截图验证时用，跑完把 accounts.json 换回来（见 restore_accounts.py）。
"""
import json
import os
import struct
import zlib

APP = os.path.join(os.environ["APPDATA"], "LMCL")
CACHE = os.path.join(APP, "cache", "textures")


def write_png(path, pixels, w, h):
    """pixels: list of rows, each row a list of (r,g,b,a)。"""
    rows = bytearray()
    for y in range(h):
        rows.append(0)
        for x in range(w):
            r, g, b, a = pixels[y][x]
            rows += bytes((r, g, b, a))

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(rows), 6))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def blank(w, h, color):
    return [[color for _ in range(w)] for _ in range(h)]


def make_skin(base, face, eye):
    px = blank(64, 64, (base[0], base[1], base[2], 255))
    # 头部展开区（前 32×16）
    for y in range(16):
        for x in range(32):
            px[y][x] = (face[0], face[1], face[2], 255)
    # 脸（8,8)-(16,16)
    for y in range(8, 16):
        for x in range(8, 16):
            px[y][x] = (face[0], face[1], face[2], 255)
    # 两只眼睛
    for ey in (11, 12):
        for ex in (9, 13):
            px[ey][ex] = (eye[0], eye[1], eye[2], 255)
    # 一条腰带，让全身图也有辨识度
    for y in range(28, 32):
        for x in range(20, 40):
            px[y][x] = (255, 255, 255, 255)
    return px


def make_cape():
    px = blank(64, 32, (0, 0, 0, 0))
    for y in range(32):
        for x in range(64):
            px[y][x] = (192, 57, 43, 255)
    # 披风本体区域 (0,1)-(10,17) 加一条白边，裁剪出来能看出图案
    for y in range(1, 17):
        for x in range(10):
            px[y][x] = (192, 57, 43, 255)
    for y in range(1, 3):
        for x in range(10):
            px[y][x] = (245, 245, 245, 255)
    for y in range(15, 17):
        for x in range(10):
            px[y][x] = (245, 245, 245, 255)
    return px


SKIN_A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
SKIN_B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
CAPE_A = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"


def main():
    os.makedirs(CACHE, exist_ok=True)

    write_png(os.path.join(CACHE, SKIN_A + ".png"), make_skin((46, 92, 138), (198, 134, 66), (32, 32, 32)), 64, 64)
    write_png(os.path.join(CACHE, SKIN_B + ".png"), make_skin((138, 46, 92), (232, 185, 138), (24, 24, 24)), 64, 64)
    write_png(os.path.join(CACHE, CAPE_A + ".png"), make_cape(), 64, 32)

    tex = "https://textures.minecraft.net/texture/"

    accounts = json.load(open(os.path.join(APP, "accounts.json"), encoding="utf-8"))

    probe = {
        "Id": "probe00000000000000000000000001",
        "Name": "SteveTester",
        "Uuid": "069a79f444e94726a5befca90e38aaf5",
        "AccessToken": "probe-token",
        "RefreshToken": "probe-refresh",
        "Xuid": "1234567890",
        "Type": "Microsoft",
        "Server": "",
        "ExpiresAt": "2030-01-01T00:00:00+08:00",
        "LastUsed": "2026-10-14T08:45:00+08:00",
        "SkinUrl": tex + SKIN_A,
        "SkinVariant": "CLASSIC",
        "CapeUrl": tex + CAPE_A,
        "Skins": [
            {"Id": "skin-1", "Url": tex + SKIN_A, "Variant": "CLASSIC", "State": "ACTIVE", "Alias": "standard"},
            {"Id": "skin-2", "Url": tex + SKIN_B, "Variant": "SLIM", "State": "INACTIVE", "Alias": "slim"},
        ],
        "Capes": [
            {"Id": "cape-1", "Url": tex + CAPE_A, "State": "ACTIVE", "Alias": "Migrator"},
        ],
    }

    accounts["Accounts"].insert(0, probe)
    accounts["SelectedId"] = probe["Id"]

    with open(os.path.join(APP, "accounts.json"), "w", encoding="utf-8") as f:
        json.dump(accounts, f, ensure_ascii=False, indent=2)

    print("已写入探针账号与 3 张纹理")
    print("缓存目录:", CACHE)


if __name__ == "__main__":
    main()
