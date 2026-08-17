#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成一张合成的象棋棋盘截图, 用于冒烟测试识别管线。

输出:
  out/synthetic_start.png   合成棋盘图
  out/synthetic_start.fen   对应的标准开局 FEN
"""
from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(r"D:\Trae CN\project\xiangqi-assistant\xiangqi-pwa-offline")
OUT = Path(__file__).resolve().parent / "out"

W, H = 1080, 1200
M = 60
CELL = 120
PIECE_SIZE = 104

# (阵营, 棋子英文名, 行, 列) —— 标准开局
START = [
    ("b", "Jiang", 0, 4), ("b", "Shi", 0, 3), ("b", "Shi", 0, 5),
    ("b", "Xiang", 0, 2), ("b", "Xiang", 0, 6),
    ("b", "Ma", 0, 1), ("b", "Ma", 0, 7), ("b", "Ju", 0, 0), ("b", "Ju", 0, 8),
    ("b", "Pao", 2, 1), ("b", "Pao", 2, 7),
    ("b", "Zu", 3, 0), ("b", "Zu", 3, 2), ("b", "Zu", 3, 4), ("b", "Zu", 3, 6), ("b", "Zu", 3, 8),
    ("r", "Bing", 6, 0), ("r", "Bing", 6, 2), ("r", "Bing", 6, 4), ("r", "Bing", 6, 6), ("r", "Bing", 6, 8),
    ("r", "Pao", 7, 1), ("r", "Pao", 7, 7),
    ("r", "Shuai", 9, 4), ("r", "Shi", 9, 3), ("r", "Shi", 9, 5),
    ("r", "Xiang", 9, 2), ("r", "Xiang", 9, 6),
    ("r", "Ma", 9, 1), ("r", "Ma", 9, 7), ("r", "Ju", 9, 0), ("r", "Ju", 9, 8),
]

EXPECTED_FEN = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w"


def load_piece(side, name, size=PIECE_SIZE):
    side_name = {"r": "Red", "b": "Black"}[side]
    p = REPO / "chess pieces" / f"White {side_name} {name}.png"
    if not p.exists():
        raise FileNotFoundError(f"missing piece asset: {p}")
    im = Image.open(p).convert("RGBA")
    im.thumbnail((size, size), Image.LANCZOS)
    return im


def draw_board():
    img = Image.new("RGB", (W, H), (246, 232, 207))
    d = ImageDraw.Draw(img)
    line = (120, 78, 38)
    for i in range(9):
        x = M + i * CELL
        d.line([(x, M), (x, H - M)], fill=line, width=3)
    for j in range(10):
        if j == 4:
            continue  # 楚河汉界
        y = M + j * CELL
        d.line([(M, y), (W - M, y)], fill=line, width=3)
    # 九宫斜线
    d.line([(M + 3 * CELL, M), (M + 5 * CELL, M + 2 * CELL)], fill=line, width=2)
    d.line([(M + 5 * CELL, M), (M + 3 * CELL, M + 2 * CELL)], fill=line, width=2)
    d.line([(M + 3 * CELL, H - M - 2 * CELL), (M + 5 * CELL, H - M)], fill=line, width=2)
    d.line([(M + 5 * CELL, H - M - 2 * CELL), (M + 3 * CELL, H - M)], fill=line, width=2)
    return img


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    img = draw_board()
    for side, name, row, col in START:
        piece = load_piece(side, name)
        cx = M + col * CELL
        cy = M + row * CELL
        img.paste(piece, (int(cx - piece.width / 2), int(cy - piece.height / 2)), piece)
    img.save(OUT / "synthetic_start.png")
    (OUT / "synthetic_start.fen").write_text(EXPECTED_FEN, encoding="utf-8")
    print(f"written: {OUT / 'synthetic_start.png'}")
    print(f"expected FEN: {EXPECTED_FEN}")


if __name__ == "__main__":
    main()
