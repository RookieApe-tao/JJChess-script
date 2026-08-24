# 象棋助手 (Xiangqi Assistant)

JJ 象棋（`cn.jj`）悬浮窗助手：截屏识别棋盘 → 转 FEN → 本地 Pikafish 引擎计算 → 以**中文着法**显示最优下一步。全部在手机本地运行，无需联网。

> ⚠️ 本项目仅供学习计算机视觉与 UCI 引擎对接原理，请勿用于线上对局作弊。

## 下载安装

见 **[Releases](https://github.com/RookieApe-tao/JJChess-script/releases)** 页面，下载最新的 `xiangqi-assistant-vX.Y-arm64.apk` 安装（需 arm64-v8a 设备、Android 8.0+）。

## 使用教程

### 1. 首次启动与授权（三步）

打开 App 后点 **「授权并启动悬浮窗」**，依次完成三项权限：

| 权限 | 弹窗名称 | 操作 |
|------|----------|------|
| 悬浮窗 | 在其他应用上层显示 | 找到「象棋助手」→ 允许 |
| 通知 | 通知 | 允许（前台服务需要） |
| 录屏 | 立即开始投屏 | 点「立即开始」（返回 App 后自动弹出） |

三项都通过后，通知栏出现「悬浮窗运行中」，屏幕左上角出现悬浮窗。

### 2. 日常使用流程

1. 打开 JJ 象棋，进入对局；
2. 点悬浮窗的 **「新局」** —— 标定当前这一局的棋盘（每局开始点一次即可）；
3. 轮到你走棋时，点 **「分析」**；不需要建议、只想记账走子时，点 **「记录」**（不跑引擎，更快）；
4. 悬浮窗显示中文着法（如 `炮二平五`）+ 引擎评分 + 当前手数，背景变绿 = 识别成功；变红 = 识别失败，稍等 1 秒重试。

### 3. 悬浮窗按钮说明

| 按钮 | 作用 |
|------|------|
| **新局** | 每局开始时点一次，重新标定棋盘基线 |
| **我方：红 / 我方：黑** | 切换你执哪方。执黑时点它，然后重新点「新局」 |
| **分析** | 截屏 → 识别 → 算出最优着法（点击后先显示「分析中....」） |
| **记录** | 只跟踪走子并累计手数，不跑引擎，比「分析」快 |
| **应急** | 记录混乱时的补救：放弃历史，全量识别当前画面重建基线，手数清零后继续跟踪 |
| **标题栏拖动** | 按住「象棋助手 ▾」一栏可拖动悬浮窗位置 |
| **标题栏短按** | 折叠/展开按钮区（只留结果行，不挡棋盘） |
| **×** | 关闭悬浮窗并停止服务 |

### 4. 结果状态颜色

- ⚪ 白色：空闲
- 🟡 黄色：识别中（约 1–3 秒）
- 🟢 绿色：成功，显示着法与评分
- 🔴 红色：识别/引擎出错，重试即可

### 5. 常见问题

- **识别不准 / 变红**：确认已点「新局」；确认执方设置正确；JJ 象棋界面停留在完整棋盘画面。
- **记录/手数乱了**：点 **「应急」** 重校——对当前画面做全量识别、重建基线、手数清零。适合中局跟踪错乱时恢复（「新局」只能用于开局附近）。
- **换了一台手机不识别**：当前棋盘标定针对 1440×3200 分辨率硬编码（见 `app/XiangqiAssistant/JjCalibration.cs`），其他分辨率需重新标定。
- **完全卡住**：通知栏 dismissing 掉服务通知，或重启 App。

## 从源码构建

需要 .NET 10 SDK + MAUI Android 工作负载（`dotnet workload install android`）：

```bash
# 核心库（纯 Windows 可测）
dotnet build "app/XiangqiCore/XiangqiCore.csproj"

# 端到端测试（记谱 + 识别 + 引擎）
dotnet run --project "app/XiangqiCore.Tests/XiangqiCore.Tests.csproj"

# Android APK（Release，debug 签名）
dotnet build -c Release -f net10.0-android36.0 "app/XiangqiAssistant/XiangqiAssistant.csproj"

# 部署到已连接设备
dotnet build -t:Run -f net10.0-android36.0 "app/XiangqiAssistant/XiangqiAssistant.csproj"
```

Python 参考实现：`python phase0/recognize.py <截图.png> --layout phase0/jj_layout.json --debug`

## 目录结构

| 目录 | 说明 |
|------|------|
| `app/XiangqiCore/` | .NET 10 识别 + 引擎管线类库（无 Android 依赖，Windows 可测） |
| `app/XiangqiAssistant/` | .NET MAUI Android 悬浮窗 App（成品） |
| `app/XiangqiCore.Tests/` | 控制台端到端测试 |
| `phase0/` | Python 参考实现（`recognize.py`）与棋盘标定 `jj_layout.json` |
| `engines/` | 预编译 Pikafish（GPL v3）+ NNUE 权重 |
| `xiangqi-pwa-offline/` | 第三方 PWA（MIT），ONNX 模型与 NNUE 的上游，勿改动 |
| `release/` | 发布包说明（APK 本体在 GitHub Release） |

## 技术管线

截屏 → YOLO 检测棋子（ONNX, 640×640）→ 裁剪到棋盘 → 多比例分类器（ONNX, 64×64, 7 类）→ 红黑判色 → 落格 → 合法性自动纠正 → FEN → Pikafish UCI → 中文记谱（车马炮、前后/中、进退平、红汉字黑数字）。

详细架构与非显而易见的坑见 [CLAUDE.md](CLAUDE.md)。

## 许可证

- 本项目代码：仅供学习交流。
- Pikafish 引擎：**GPL v3**（`engines/Copying.txt`）。
- 识别模型与 NNUE 权重：来自 [xiangqi-pwa-offline](https://github.com/dffge552/xiangqi-pwa-offline)（MIT）。
