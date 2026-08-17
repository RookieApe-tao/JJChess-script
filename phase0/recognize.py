#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Phase 0: 象棋截图识别 -> FEN

使用 棋弈江湖 (xiangqi-pwa-offline, MIT) 的 ONNX 模型:
  online_xiangqi_piece_detector.onnx  检测棋子位置 (YOLO 风格, 输入 640x640)
  online_xiangqi_classifier.onnx      分类棋子类型 (输入 64x64, 7 类)
红黑阵营由颜色启发式判定, 最后映射到 9x10 棋盘输出 FEN。

针对 JJ 象棋 (cn.jj) 做的适配:
  - 有标定范围 (--layout/--corners) 时, 检测结果只保留棋盘区域内的框
  - 分类采用多倍率融合 (含向内收缩), 避免"被吃/可吃"时棋子周围的红绿渐变光晕
    把相邻像素裁进 ROI 导致误判
  - 红黑判定用"字符区红色纯度"(r - max(g,b) 的 95 分位), 避开暖色木纹背景
  - 棋规自动纠错: 象/相必须在合法落点, 每种棋子数量不超上限

用法:
  python recognize.py <图片> [--layout jj_layout.json] [--corners x1,y1 x2,y2 x3,y3 x4,y4]
                            [--pawn x,y] [--side w|b] [--debug] [--out 目录]
"""
import argparse
import json
import sys
from collections import Counter
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image, ImageDraw

CLASS_NAMES = ["士", "車", "馬", "卒", "將", "炮", "相"]
PIECE_TO_FEN = {"士": "A", "車": "R", "馬": "N", "卒": "P", "將": "K", "炮": "C", "相": "B"}

DET_SIZE = 640
CLS_SIZE = 64
CONF_THRESHOLD = 0.3
IOU_THRESHOLD = 0.45
EXPANSIONS = (-0.05, 0.0, 0.05, 0.10)  # 多倍率分类: 向内收缩 / 原框 / 微扩 / 稍扩
PRIMARY_RATIO = 0.05  # 校准过的主分类倍率, 其余倍率只作为纠错候选
CROP_MARGIN = 15  # 标定范围外扩像素

DEFAULT_MODEL_DIR = Path(r"D:\Trae CN\project\xiangqi-assistant\xiangqi-pwa-offline")

# 每种棋子每方上限
MAX_COUNTS = {"R": 2, "N": 2, "B": 2, "A": 2, "C": 2, "P": 5, "K": 1,
              "r": 2, "n": 2, "b": 2, "a": 2, "c": 2, "p": 5, "k": 1}
# 象/相的合法落点
XIANG_SQUARES = {(0, 2), (0, 6), (2, 0), (2, 4), (2, 8), (4, 2), (4, 6),
                 (9, 2), (9, 6), (7, 0), (7, 4), (7, 8), (5, 2), (5, 6)}


def letterbox(img, size):
    w, h = img.size
    scale = min(size / w, size / h)
    nw = max(1, round(w * scale))
    nh = max(1, round(h * scale))
    canvas = Image.new("RGB", (size, size), (114, 114, 114))
    offx = (size - nw) // 2
    offy = (size - nh) // 2
    canvas.paste(img.resize((nw, nh), Image.BILINEAR), (offx, offy))
    arr = np.asarray(canvas, dtype=np.float32) / 255.0
    return arr.transpose(2, 0, 1)[None, ...].copy(), scale, offx, offy


def iou(a, b):
    x1 = max(a["x"], b["x"]); y1 = max(a["y"], b["y"])
    x2 = min(a["x"] + a["w"], b["x"] + b["w"]); y2 = min(a["y"] + a["h"], b["y"] + b["h"])
    inter = max(0.0, x2 - x1) * max(0.0, y2 - y1)
    union = a["w"] * a["h"] + b["w"] * b["h"] - inter
    return inter / union if union > 0 else 0.0


def nms(boxes, thr=IOU_THRESHOLD):
    boxes = sorted(boxes, key=lambda b: -b["conf"])
    keep = []
    while boxes:
        best = boxes.pop(0)
        keep.append(best)
        boxes = [b for b in boxes if iou(best, b) <= thr]
    return keep


def run_detector(session, img):
    tensor, scale, offx, offy = letterbox(img, DET_SIZE)
    out = session.run(None, {"images": tensor})[0]
    dims = out.shape
    if len(dims) != 3:
        raise ValueError(f"unexpected detector output shape: {dims}")
    # 兼容 [1,5,8400] 和 [1,8400,5] 两种布局
    if dims[1] > dims[2]:
        arr = out.reshape(-1, 5).T
    else:
        arr = out.reshape(5, -1)
    xs, ys, ws, hs, confs = (arr[i] for i in range(5))
    boxes = []
    for i in range(arr.shape[1]):
        conf = float(confs[i])
        if conf < CONF_THRESHOLD:
            continue
        cx = (float(xs[i]) - offx) / scale
        cy = (float(ys[i]) - offy) / scale
        w = float(ws[i]) / scale
        h = float(hs[i]) / scale
        if cx < 0 or cx > img.width or cy < 0 or cy > img.height or w <= 0 or h <= 0:
            continue
        boxes.append({"x": cx - w / 2, "y": cy - h / 2,
                      "w": w, "h": h, "cx": cx, "cy": cy, "conf": conf})
    return nms(boxes)


def classify_roi_probs(session, img, box, ratio=0.0):
    """返回分类器 7 类概率向量 (float32), 裁剪失败返回 None。"""
    W, H = img.size
    ex, ey = box["w"] * ratio, box["h"] * ratio
    x1 = max(0, int(box["x"] - ex)); y1 = max(0, int(box["y"] - ey))
    x2 = min(W, int(box["x"] + box["w"] + ex)); y2 = min(H, int(box["y"] + box["h"] + ey))
    if x2 - x1 <= 0 or y2 - y1 <= 0:
        return None
    roi = img.crop((x1, y1, x2, y2)).resize((CLS_SIZE, CLS_SIZE), Image.BILINEAR)
    arr = np.asarray(roi, dtype=np.float32) / 255.0
    tensor = arr.transpose(2, 0, 1)[None, ...].copy()
    probs = session.run(None, {"images": tensor})[0].flatten()
    return probs


def classify_roi_topk(session, img, box, k=3):
    """多倍率分类: 以 PRIMARY_RATIO 的 argmax 为主, 每个倍率取 top-k 汇总成候选。
    返回 (top1, prob, alts)。alts 用于棋规纠错时的重判。"""
    agg = {}
    primary_ratios = (0.0, 0.05)  # primary ratios for main decision
    primary_scores = {}
    for ratio in EXPANSIONS:
        probs = classify_roi_probs(session, img, box, ratio)
        if probs is None:
            continue
        order = np.argsort(probs)[::-1][:k]
        if any(abs(ratio - r) < 1e-9 for r in primary_ratios):
            n0 = CLASS_NAMES[int(order[0])]
            p0 = float(probs[order[0]])
            if n0 not in primary_scores or p0 > primary_scores[n0]:
                primary_scores[n0] = p0
        for idx in order:
            name = CLASS_NAMES[int(idx)]
            p = float(probs[idx])
            if p < 0.01:
                continue
            if name not in agg or p > agg[name]:
                agg[name] = p
    if not agg:
        return None, 0.0, []
    top1 = max(primary_scores, key=primary_scores.get) if primary_scores else max(agg, key=agg.get)
    top1_prob = primary_scores.get(top1, agg[top1])
    alts = sorted(agg.items(), key=lambda kv: -kv[1])[:k]
    return top1, top1_prob, alts


def determine_color(roi_img):
    """红黑判定: 取棋子中央 50% 区域, 用 r - max(g,b) 的 95 分位区分红字/黑字。
    红绿渐变光晕在棋子外圈, 不进入中央区域, 不影响判定。"""
    w, h = roi_img.size
    cw, ch = max(1, int(w * 0.5)), max(1, int(h * 0.5))
    cx1, cy1 = int(w * 0.25), int(h * 0.25)
    px = np.asarray(roi_img.crop((cx1, cy1, cx1 + cw, cy1 + ch)), dtype=np.int32)
    r, g, b = px[..., 0], px[..., 1], px[..., 2]
    score = float(np.percentile(r - np.maximum(g, b), 95))
    return "red" if score > 60 else "black"


def next_alt(cell, exclude, cnt):
    for fen, _prob in cell["alts"]:
        if fen == exclude or fen not in MAX_COUNTS:
            continue
        if cnt.get(fen, 0) < MAX_COUNTS[fen]:
            return fen
    return None


def apply_legality_rules(cells):
    """棋规自动纠错: 象/相必须在合法落点; 每种棋子数量不超上限。"""
    passes = 0
    while passes < 10:
        changed = False
        cnt = Counter(c["piece"] for c in cells.values())
        # 规则1: 象/相必须在合法落点
        for key, cell in list(cells.items()):
            top = cell["piece"]
            if top in "bB" and key not in XIANG_SQUARES:
                repl = next_alt(cell, top, cnt)
                if repl:
                    cnt[top] -= 1
                    cnt[repl] += 1
                    cell["piece"] = repl
                    cell["corrected"] = True
                    changed = True
        # 规则2: 某类棋子超上限时, 重判置信度最低的那个
        for piece in list(cnt):
            if cnt[piece] > MAX_COUNTS[piece]:
                offenders = [k for k, c in cells.items() if c["piece"] == piece]
                worst = min(offenders, key=lambda k: cells[k]["prob"])
                repl = next_alt(cells[worst], piece, cnt)
                if repl:
                    cnt[piece] -= 1
                    cnt[repl] += 1
                    cells[worst]["piece"] = repl
                    cells[worst]["corrected"] = True
                    changed = True
        if not changed:
            break
        passes += 1


def detections_to_board(dets, corners=None, pawn=None):
    """返回 (棋盘字符矩阵, cells 字典)。cells[(row,col)] 含 piece/alts/prob/corrected。"""
    cells = {}
    if not dets:
        return [[None] * 9 for _ in range(10)], cells
    if corners and len(corners) == 4:
        xs = [p[0] for p in corners]; ys = [p[1] for p in corners]
        minx, maxx = min(xs), max(xs)
        miny, maxy = min(ys), max(ys)
    else:
        minx = min(d["cx"] for d in dets); maxx = max(d["cx"] for d in dets)
        miny = min(d["cy"] for d in dets); maxy = max(d["cy"] for d in dets)
        cell = ((maxx - minx) / 8 + (maxy - miny) / 9) / 2
        minx -= cell * 0.5; maxx += cell * 0.5
        miny -= cell * 0.5; maxy += cell * 0.5
    cellw = (maxx - minx) / 8
    cellh = (maxy - miny) / 9

    raw = []
    pawns = []
    for d in dets:
        row = round((d["cy"] - miny) / cellh)
        col = round((d["cx"] - minx) / cellw)
        raw.append((row, col, d))
        if d["piece"] in ("P", "p"):
            pawns.append((col, d))

    col_offset = 0
    if pawn:
        col_offset = 4 - round((pawn[0] - minx) / cellw)
    elif pawns:
        best = min(pawns, key=lambda pc: abs(pc[0] - 4))
        col_offset = 4 - best[0]

    for row, col, d in raw:
        c = col + col_offset
        if 0 <= row < 10 and 0 <= c < 9:
            cells[(row, c)] = {
                "piece": d["piece"],
                "alts": d["alts"],
                "prob": d["prob"],
                "corrected": False,
            }

    board = [[cells[(r, c)]["piece"] if (r, c) in cells else None
              for c in range(9)] for r in range(10)]
    return board, cells


def board_to_fen(board, side="w"):
    rows = []
    for row in board:
        s = ""
        e = 0
        for p in row:
            if p is None:
                e += 1
            else:
                if e:
                    s += str(e)
                    e = 0
                s += p
        if e:
            s += str(e)
        rows.append(s)
    return "/".join(rows) + " " + side


def draw_annotations(img, dets, out_path):
    draw = ImageDraw.Draw(img)
    for d in dets:
        x1, y1 = int(d["x"]), int(d["y"])
        x2, y2 = int(d["x"] + d["w"]), int(d["y"] + d["h"])
        col = (255, 0, 0) if d["color"] == "red" else (0, 0, 255)
        draw.rectangle([x1, y1, x2, y2], outline=col, width=3)
        draw.text((x1, y1 - 16), f"{d['piece']} {d['prob']:.2f}", fill=col)
    img.save(out_path)


def main():
    ap = argparse.ArgumentParser(description="象棋截图识别 -> FEN")
    ap.add_argument("image", help="截图路径")
    ap.add_argument("--model-dir", default=str(DEFAULT_MODEL_DIR),
                    help="ONNX 模型所在目录")
    ap.add_argument("--layout", default=None,
                    help="布局 JSON (含 board_rect 或 corners_px), 如 jj_layout.json")
    ap.add_argument("--corners", nargs="+", metavar="x,y",
                    help="棋盘四角原图坐标: x1,y1 x2,y2 x3,y3 x4,y4")
    ap.add_argument("--pawn", metavar="x,y", help="中路兵/卒坐标(可选, 列偏移校正)")
    ap.add_argument("--side", default="w", choices=["w", "b"], help="轮到谁走: w=红, b=黑")
    ap.add_argument("--debug", action="store_true", help="打印每个棋子")
    ap.add_argument("--out", default=None, help="标注图输出目录")
    args = ap.parse_args()

    corners = None
    if args.layout:
        with open(args.layout, encoding="utf-8") as f:
            layout = json.load(f)
        if "corners_px" in layout:
            corners = [tuple(float(v) for v in p) for p in layout["corners_px"]]
        elif "board_rect" in layout:
            x0, y0, x1, y1 = layout["board_rect"]
            corners = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    if corners is None and args.corners:
        corners = [tuple(float(v) for v in c.split(",")) for c in args.corners]

    model_dir = Path(args.model_dir)
    det = ort.InferenceSession(
        str(model_dir / "online_xiangqi_piece_detector.onnx"),
        providers=["CPUExecutionProvider"])
    cls = ort.InferenceSession(
        str(model_dir / "online_xiangqi_classifier.onnx"),
        providers=["CPUExecutionProvider"])

    img = Image.open(args.image).convert("RGB")

    # 检测用全图(保证检测框精度), 之后只保留标定范围内的框
    boxes = run_detector(det, img)
    if corners:
        xs = [p[0] for p in corners]; ys = [p[1] for p in corners]
        minx, maxx = min(xs), max(xs)
        miny, maxy = min(ys), max(ys)
        m = CROP_MARGIN
        boxes = [b for b in boxes
                 if minx - m <= b["cx"] <= maxx + m and miny - m <= b["cy"] <= maxy + m]

    if args.debug:
        print(f"[detector] {len(boxes)} boxes (board region only)")

    dets = []
    for b in boxes:
        name, prob, alts = classify_roi_topk(cls, img, b)
        if name is None:
            continue
        x1, y1 = int(b["x"]), int(b["y"])
        x2, y2 = min(img.width, int(b["x"] + b["w"])), min(img.height, int(b["y"] + b["h"]))
        roi = img.crop((x1, y1, x2, y2))
        color = determine_color(roi)
        fen = PIECE_TO_FEN[name].upper() if color == "red" else PIECE_TO_FEN[name].lower()
        alts_fen = [(PIECE_TO_FEN[n].upper() if color == "red" else PIECE_TO_FEN[n].lower(), p)
                    for n, p in alts]
        d = dict(b)
        d.update(class_name=name, color=color, piece=fen, alts=alts_fen, prob=prob)
        dets.append(d)
        if args.debug:
            print(f"  {fen} {color}{name} conf={prob:.3f} at ({d['cx']:.0f},{d['cy']:.0f})")

    pawn = tuple(float(v) for v in args.pawn.split(",")) if args.pawn else None
    board, cells = detections_to_board(dets, corners=corners, pawn=pawn)
    apply_legality_rules(cells)
    if args.debug:
        for (row, col), cell in sorted(cells.items()):
            if cell["corrected"]:
                print(f"  [fix] [{row},{col}] -> {cell['piece']}")
    board = [[cells[(r, c)]["piece"] if (r, c) in cells else None
              for c in range(9)] for r in range(10)]
    print(board_to_fen(board, args.side))

    if args.out:
        outdir = Path(args.out)
        outdir.mkdir(parents=True, exist_ok=True)
        out = outdir / (Path(args.image).stem + "_annotated.png")
        draw_annotations(img.copy(), dets, out)
        print(f"[out] {out}", file=sys.stderr)


if __name__ == "__main__":
    main()
