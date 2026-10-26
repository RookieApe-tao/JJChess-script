# 交接：识别率低 / 应急重校失败 —— 诊断与修复清单

## 背景

JJ 象棋悬浮窗助手（本仓库）。当前问题：

1. **先点选一个棋子再点「记录」→ 识别失败**。JJ 选中棋子后有悬浮动画效果（棋子抬起、可走点提示、可吃光晕）。
2. **不选中棋子，「应急」重校也经常失败**（残局尤甚）。

工作区已有改动并已提交推送（commit `519c21a`）：「记录」「应急」按钮 + 手数记账。当前工作区干净（除 `tmp-crashdiag/` 未入库，不用管）。

## 根因分析（已通过读代码确认，尚未跑实验验证）

### 根因 1：行/列偏移启发式只在开局成立 —— 残局应急失败的元凶

`app/XiangqiCore/Recognition/BoardRecognizer.cs`：

- `ComputeRowOffset`（约 :201）：把**最靠上的棋子强制对到 row 0**。开局成立（对方底线在 row 0）；残局最靠上的棋子可能在第 2-3 行 → `BuildOccupancy` 整盘行错位。
- `DetectionsToBoard` 的中兵列吸附（约 :353-358）：假设"最接近中路 file 4 的兵"定位列。开局 5 兵成立；残局只剩边路兵 → `colOffset = ±2` → `Analyze` 整盘列错位，FEN 全废。

**关键**：`Analyze`（生成 FEN，应急用）和 `BuildOccupancy`（生成占位，跟踪用）是**两条独立路径，坐标系可能不一致**：

- `BuildOccupancy`（:137）：minConf=0.5 过滤 + 动态 margin + ComputeColOffset/ComputeRowOffset 行列校正。
- `Analyze`（:47）：ConfThreshold=0.3（过低）+ 固定 CropMargin=15 + **只做中兵列吸附、完全不做行校正**。

`TrackingAnalysisService.Resync`（`app/XiangqiCore/TrackingAnalysisService.cs:126`）里用 `PlacementMatches(fen, occ)` 校验两者一致（:178）→ 两条路径坐标不一致时**必然失败**，报「识别结果与棋子占位不一致」。这就是"不选中也容易应急失败"的机理。

### 根因 2：选中动画的杂框 + settle 误判 —— "点棋子后记录失败"的元凶

- JJ 选中棋子后：可走点提示、可吃红绿光晕 → YOLO 产生 **0.3-0.5 低置信杂框**。跟踪路径（minConf 0.5）滤掉，但应急路径（0.3）全收 → 当真棋子分类 → 占位对不上。
- 选中棋子悬浮抬起 → 该检测框中心离格点超过 0.35（`TrackingAnalysisService.cs:29` SettleThreshold，:254 检查）→ 直接报「棋子仍在移动，请稍候再点『分析』」。**选中不动≠走子**：占位其实没变（该格仍有子），应判 NoChange，但因离格被前置拦截 → 永远失败。

### 真棋子 vs 杂框的分离度

真棋子检测置信度通常 0.9+，杂框 0.3-0.5，分离度好 → 提高阈值对开局场景零风险。

## 修复清单（用户已确认按此执行）

| # | 修复 | 文件 | 说明 |
|---|------|------|------|
| 1 | **网格拟合格偏移**：在 [-2,2]² 偏移候选里选"高置信框中心离格点总距离最小"者，`BuildOccupancy` 与 `DetectionsToBoard` 共用 | BoardRecognizer.cs | 替换 ComputeRowOffset/ComputeColOffset + 中兵吸附，两条路径坐标强制一致，残局不再错位 |
| 2 | **过滤统一**：`Analyze` 同样 minConf≥0.5 + 尺寸过滤（框宽 <0.6 格距丢弃——可走提示点远小于棋子） | BoardRecognizer.cs :47-91 | 滤掉光晕/提示点杂框 |
| 3 | **选中动画 settle 豁免**：只有 >1 个高置信框离格才判"移动中"；单个离格框若吸附后占位与已确认占位一致 → NoChange | TrackingAnalysisService.cs :254 | 选中悬浮不再拦截「记录」 |
| 4 | **服务层失败自动重抓**：跟踪失败（移动中/瞬态）时延迟 ~400ms 重抓重试一次，仍败才报红 | app/XiangqiAssistant/AssistantService.cs | 吃瞬态动画 |
| 5 | **Resync 多帧共识**：抓 2-3 帧占位共识滤瞬态；PlacementMatches 失败带诊断信息（diff 数）后降级重试 | TrackingAnalysisService.cs + AssistantService.cs | 应急更稳 |
| 6 | 回归：`dotnet run --project "app/XiangqiCore.Tests/XiangqiCore.Tests.csproj"` 全绿 + Android APK 编译通过 | | 测试断言见下 |

## 用户要求的实证步骤（优先做）

用户原话：「先别修复了。这样吧我下一盘棋，然后选中一个棋子。你截图来检查」——即**先真机取证再改代码**：

1. 设备已连：`adb` 在 `D:\Android\android-sdk\platform-tools\adb.exe`，序列号 `6c8ba9cb`。
2. 让用户摆好场景（选中棋子的残局/中局），然后 `adb exec-out screencap -p > screenshots/live/<名字>.png` 截图取证。
3. 至少取三类：(a) 选中棋子（动画中）(b) 未选中静止 (c) 残局。存到 `screenshots/live/`。
4. 跑下面说的诊断程序，验证根因 1/2 与预测一致（两条路径 diff、offGrid 值、杂框 conf 分布）。

## 已准备好的诊断工具

`app/XiangqiCore.Tests/diag/DiagResync.cs` 已写好（对比 Analyze-FEN 占位 vs BuildOccupancy 占位、打印行列偏移、diff 格子、offGrid），`XiangqiCore.Tests.csproj` 已加条件编译：

```bash
cd "app/XiangqiCore.Tests"
dotnet run --project XiangqiCore.Tests.csproj -p:Diag=true
```

（正常测试是 `dotnet run --project XiangqiCore.Tests.csproj`，两种模式互不干扰；Program.cs 在 Diag=true 时被排除。）

可再加一个 `boxes` 式的 conf 分布 dump（参考 Program.cs 尾部 `args.Contains("boxes")` 段落）验证选中动画下的杂框置信度。

## 测试断言注意

`Program.cs` 有这些既有断言，改完必须仍通过：

- 三张截图的 FEN 精确匹配（jj_board/jj_mid/jj_now，见 :217-222）。
- `track-svc resync mid`：jj_mid 应急重校后 FEN 必须精确等于 `2baka1nr/9/1cn1b2R1/p1C1p1p1p/9/9/r1P1P1P1P/2N1C1N2/9/2BAKAB2 w`（:167-177）。
- 我方黑 bootstrap：`马二进三` + MoveCount==1（:184-193）。
- 修复 1 会改变 `BuildOccupancy` 的 ColOffset/RowOffset 语义 → 注意 `Reset`（TrackingAnalysisService.cs:65）也用了自动偏移，开局截图的 bootstrap 断言可能需要微调阈值，**不要改截图期望 FEN**，如失败先查是不是网格拟合把开局场景弄错了。

## 关键约定（CLAUDE.md 已有，别踩）

- 不要旋转截图，reverseRows 翻转行/列即可。
- `ImageOps.PilResizeRgb` 不许动。
- 检测器输出形状 `[1,5,8400]` / `[1,8400,5]` 两种都要兼容。
- 中文注释/文案。

## 任务清单状态

已建 7 个任务（TaskList 里）：#1 诊断（in_progress）、#2-#6 修复 1-5、#7 回归。按 #1 → 实证取证 → #2…#6 → #7 顺序做。

## 完成后

跑全量测试 + `dotnet build -c Release -f net10.0-android36.0 "app/XiangqiAssistant/XiangqiAssistant.csproj"`，然后按仓库惯例提交（中文 commit message + Co-Authored-By: Claude）推送 `git@github.com:RookieApe-tao/JJChess-script.git`。
