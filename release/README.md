# 发布包（Release）

APK 本体超过 GitHub 100 MB 单文件上限，**不入库**，通过 GitHub Release 附件发布：
<https://github.com/RookieApe-tao/JJChess-script/releases>

## 版本列表

### v1.0 — 2026-08-17

| 项 | 值 |
|----|----|
| 文件 | `xiangqi-assistant-v1.0-arm64.apk` |
| 大小 | 156,093,319 字节（约 149 MB） |
| SHA-256 | `bcc9985b63f4d311a9b48bfa365d1f0694252f6da5a62067273033612eb6f418` |
| 签名 | debug keystore（`androiddebugkey`，正式分发前请换正式签名） |
| 架构 | arm64-v8a（含 x86_64 模拟器库） |
| 最低 Android | 8.0（API 26） |

来源构建：`dotnet build -c Release -f net10.0-android36.0 app/XiangqiAssistant/XiangqiAssistant.csproj`
产物路径：`app/XiangqiAssistant/bin/Release/net10.0-android36.0/com.xiangqiassistant.app-Signed.apk`

## 校验方法

```bash
# Windows (PowerShell)
Get-FileHash xiangqi-assistant-v1.0-arm64.apk -Algorithm SHA256

# Linux / macOS
sha256sum xiangqi-assistant-v1.0-arm64.apk
```

## 安装方法

1. 下载 APK 到手机；
2. 文件管理器点开 → 允许「安装未知来源应用」；
3. 安装后按 [README 使用教程](../README.md) 授权并使用。

## 发布新版本步骤

1. 更新 `app/XiangqiAssistant/XiangqiAssistant.csproj` 的 `ApplicationDisplayVersion` / `ApplicationVersion`；
2. `dotnet build -c Release -f net10.0-android36.0 app/XiangqiAssistant/XiangqiAssistant.csproj`；
3. 复制签名 APK 到 `release/`，命名 `xiangqi-assistant-v<版本>-arm64.apk`；
4. 计算并更新本文件的 SHA-256 与大小；
5. 在 GitHub Releases 新建 tag（如 `v1.0`），上传该 APK 作为附件；
6. commit + push 本仓库（`.gitignore` 已排除 `release/` 下的 APK，只提交本说明文件）。
