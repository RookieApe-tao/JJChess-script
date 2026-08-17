# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> 本仓库主页与使用教程见 [README.md](README.md)；本文件面向开发者，重点记录架构与"会咬人的坑"。

## What this project is

象棋助手 (Xiangqi Assistant): a tool that recognizes a Chinese-chess board from a **screen capture** of the **JJ 象棋** Android game (`cn.jj`), converts it to FEN, runs the **Pikafish** UCI engine for the best move, and renders the answer in **Chinese notation**. The shipped product is a floating-window overlay that runs on top of the game. There is also a Python reference implementation and a vendored web app.

All UI text, comments, and many identifiers are in Chinese; keep new strings in Chinese to match.

## Top-level layout

- `app/XiangqiCore/` — **.NET 10 class library**: the recognition + engine pipeline. No Android/UI dependency; runnable and testable on Windows desktop.
- `app/XiangqiAssistant/` — **.NET MAUI Android app** (single project, `net10.0-android36.0`). The overlay product. References `XiangqiCore`.
- `app/XiangqiCore.Tests/` — console-exe test harness (top-level `Program.cs`) that drives `XiangqiCore` end-to-end.
- `phase0/` — **Python 3 reference implementation** of the recognition pipeline (`recognize.py`); `XiangqiCore` is a faithful C# port. `jj_layout.json` is the board calibration.
- `engines/` — **prebuilt Pikafish** binaries (Windows/Linux/Mac/Android) + `pikafish.nnue` weights + wiki. GPL v3. The Android arm64 build is shipped inside the app as `libpikafish.so`.
- `xiangqi-pwa-offline/` — **vendored third-party** PWA (Xiangqi Arena / 棋弈江湖, MIT). Source of the ONNX recognition models and `pikafish.nnue`. Treat as upstream — do not edit for this project's needs; only the `.onnx`/`.nnue` files are consumed. Its own `.git` directory (142 MB pack) is **excluded from this repo's git** via `.gitignore` — it is upstream history, not ours.
- `screenshots/` — test fixtures consumed by `XiangqiCore.Tests/Program.cs` via hardcoded absolute paths (board states with expected FENs). Keep in the repo; tests break without them.
- `release/` — 发布包说明（README + 校验和）。APK 本体超过 100 MB，不入库，走 GitHub Release 附件。

## Build, run, test

There is **no `.sln`** and **no lint/test framework** (no xUnit/analyzers). Build the three projects individually with `dotnet`. Paths contain spaces — always quote them.

```bash
# Core library (builds on Windows desktop, no Android needed)
dotnet build "app/XiangqiCore/XiangqiCore.csproj"

# Test harness: notation + recognition + engine end-to-end.
# Exits 0 on all-pass. Hardcodes absolute paths to models/screenshots/engine
# (D:\Trae CN\project\xiangqi-assistant) — edit Program.cs if the repo moves.
dotnet run --project "app/XiangqiCore.Tests/XiangqiCore.Tests.csproj"

# Diagnostic modes (append as args to the same dotnet run):
#   boxes   – raw detector boxes + confidences
#   tensor  – letterbox tensor dump (for matching Python)
#   pilcmp  – byte-compare C# resize vs a PIL resize dump
#   cells   – per-cell piece/prob/alternatives after legality correction
```

Android app (requires the `android` MAUI workload, currently `36.1.43`):

```bash
# Build + deploy to a connected device/emulator
dotnet build -t:Run -f net10.0-android36.0 "app/XiangqiAssistant/XiangqiAssistant.csproj"

# Release APK (signed with the debug keystore per the Release PropertyGroup)
dotnet build -c Release -f net10.0-android36.0 "app/XiangqiAssistant/XiangqiAssistant.csproj"
```

Python prototype:

```bash
python "phase0/recognize.py" <screenshot.png> --layout "phase0/jj_layout.json" --debug
```

`启动Claude.bat` only launches Claude Code in this directory; it is not part of the build.

## Architecture: the recognition → engine → notation pipeline

This is the core of the system and spans `XiangqiCore` + `phase0/recognize.py` (the two are kept in sync):

1. **Detect** — YOLO-style ONNX detector (`online_xiangqi_piece_detector.onnx`, 640×640 letterbox input) finds piece bounding boxes. Letterbox is done by `ImageOps.LetterboxRgbChw`.
2. **Crop to board** — when a `Calibration` is supplied (it always is for the app), boxes outside the calibrated corners + margin are dropped.
3. **Classify** — classifier ONNX (`online_xiangqi_classifier.onnx`, 64×64, 7 classes: 士車馬卒將炮相) runs on each box ROI with **multi-ratio fusion** (`Expansions = {-0.05, 0, 0.05, 0.10}`). The primary label comes from the non-expanded ratios; the others only feed correction alternatives. The contraction ratios matter: they keep the red/green "可吃/被吃" glow *outside* the ROI so it doesn't poison classification.
4. **Color** — red vs black by the 95th-percentile of `r − max(g,b)` over the central 50% of the piece (avoids warm wooden-board backgrounds and the glow ring). Upper→red FEN, lower→black FEN.
5. **Place on board** — pixel→(row,col) via the calibrated grid; a center-pawn column offset snaps the board horizontally.
6. **Legality auto-correction** (`ApplyLegalityRules`) — iteratively reclassifies the lowest-confidence piece whenever 象/相 sits off `XiangSquares` or a type exceeds `MaxCounts`. Uses each cell's stored `Alts`.
7. **FEN** — `BoardToFen` with the side to move.
8. **Engine** — `XiangqiEngine` runs Pikafish as an **external process over UCI/stdio** (`position fen …`, `go movetime …`), parses `bestmove`/score cp/depth/pv.
9. **Notation** — `Notation` converts UCI moves (files a–i, ranks 0–9 with rank 0 = red home) to Chinese (车马炮…, 前后/中 prefixes for same-file duplicates, 进退平 verbs; red uses 一二三… numerals, black uses arabic).

`AnalysisService` glues 1–9 together: recognize → FEN → engine → Chinese best move + first 8 PV moves. On a broken pipe it restarts the engine once and retries.

## Non-obvious things that will bite you

- **`ImageOps.PilResizeRgb` must reproduce PIL `Image.BILINEAR` bit-for-bit.** It's a hand-rolled fixed-point (22-bit) separable resize. Model accuracy depends on the C# path matching the Python path exactly — there's a `pilcmp` test mode that byte-compares against a dumped PIL resize. Do not "simplify" this into a naive bilinear/Skia resize.
- **Detector output layout is shape-dependent.** It can be `[1,5,8400]` (attrs-first) or `[1,8400,5]` (anchors-first); `BoardRecognizer.RunDetector` picks the interpretation by comparing `dims[1]` vs `dims[2]`.
- **FEN coordinate convention.** Board array `[10,9]` has row 0 = *top of the screenshot*. Standard FEN rank 0 = red home = bottom. `Notation` and `BoardRecognizer` translate between them; `reverseRows: true` flips when 我方执黑 (playing black) so red returns to rank 0. **Do not rotate the screenshot 180°** — that inverts the FEN and breaks notation (the overlay toggles side instead).
- **The engine runs as an external process on Android too.** Pikafish ships as `Platforms/Android/lib/arm64-v8a/libpikafish.so` and executes from `ApplicationInfo.NativeLibraryDir`. Android 10+ forbids `execve` from the writable files dir (SELinux W^X), so the binary must **not** be extracted to `FilesDir` — only `pikafish.nnue` and the `.onnx` models are extracted/read there. See `AndroidAnalysisFactory` and the `.csproj` comments.
- **Android version handling is load-bearing.** `AssistantService` registers a MediaProjection callback before capture (Android 14+), creates the `VirtualDisplay` + `ImageReader` exactly once (Android 14+ rejects re-creation), and explicitly `Close()`s each captured `Image` (only 4 slots). Don't "clean up" by recreating these per frame.
- **`XiangqiAssistant` is effectively Android-only** (single `TargetFramework net10.0-android36.0`), but its `Platforms/` tree carries iOS/MacCatalyst/Windows stubs from the MAUI template — they are not maintained.
- **`JjCalibration` hardcodes one device** (1440×3200 "xiaoai", board 80,900–1360,2290) sourced from `phase0/jj_layout.json`. Different devices/screens need a new calibration.

## Model + engine assets

The recognition models and NNUE weights are shared across all three components from `xiangqi-pwa-offline/` (or the MAUI `Resources/Raw/` copies):

| File | Purpose |
|------|---------|
| `online_xiangqi_piece_detector.onnx` | YOLO piece detector (640×640) |
| `online_xiangqi_classifier.onnx` | 7-class piece classifier (64×64) |
| `pikafish.nnue` | Pikafish NNUE evaluation weights |

Pikafish CPU-binary selection (Windows, under `engines/Windows/`) is by CPU feature set; the test harness uses `pikafish-sse41-popcnt.exe`.

## 使用教程（App 用户）

面向最终用户的完整教程在 [README.md](README.md)。速览：

1. **首次授权**：打开 App → 点「授权并启动悬浮窗」→ 依次允许 悬浮窗、通知、录屏 三项权限。
2. **每局开始**：进入 JJ 象棋对局后，点悬浮窗 **「新局」** 标定棋盘基线。
3. **走棋前**：点 **「分析」**，悬浮窗显示中文最优着法（如 `炮二平五`）与引擎评分。
4. **执黑**：点 **「我方:红」** 切换为「我方:黑」，再重新点「新局」。不要旋转截图。
5. **悬浮窗操作**：标题栏按住拖动挪位置；标题栏短按折叠/展开；**×** 退出。
6. **状态色**：白=空闲，黄=识别中，绿=成功，红=失败（稍候重点「分析」）。

已知限制：棋盘标定 `JjCalibration.cs` 硬编码 1440×3200（"xiaoai"）设备，其他分辨率需重新标定。

## 发布（Release）流程

1. Release APK 构建在 `app/XiangqiAssistant/bin/Release/net10.0-android36.0/com.xiangqiassistant.app-Signed.apk`（debug 签名）。
2. 复制为 `release/xiangqi-assistant-v<版本>-arm64.apk` 并更新 `release/README.md` 的 SHA-256 校验和。
3. **APK 不入库**（156 MB > GitHub 100 MB 单文件上限），以 GitHub Release 附件形式发布。
4. 仓库远程：`https://github.com/RookieApe-tao/JJChess-script.git`（SSH 推送，密钥 `~/.ssh/id_ed25519`）。
