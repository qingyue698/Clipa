# 构建说明

Clipa 不是把网页套壳：界面是一个单文件 HTML，原生能力（截图、录屏、全局热键、托盘、刘海窗口）全部由 .NET 宿主实现，两边通过 WebView2 消息桥（`window.chrome.webview.postMessage` / `chrome.webview.addEventListener("message")`）通信。

## 目录

- `app/Clipa.html` — Fluent 风格界面（主版本）
- `app/Clipa-iOS.html` — iOS/mac 风格界面（与主版本功能完全一致，仅视觉层不同）
- `host/Program.cs` — 宿主全部源码（WinForms + WebView2 + ZXing.Net）
- `host/MuseClipStation.csproj` — 项目文件（程序集名等内部标识保持 `MuseClipStation`，是数据目录与兼容性所需，不要改名）
- `host/app.ico` — 应用图标
- `host/patch_lang.py` — 发布后处理：把 exe 版本资源的语言由「语言中性」改为简体中文（等长原地修改，不影响单文件包）

## 构建步骤

前置：.NET 8 SDK。Windows 上直接构建；在 Linux/macOS 上需保留 csproj 里的 `EnableWindowsTargeting`。

```bash
# 1. 选择风格，把页面放进宿主目录并改名为 app.html（宿主把它作为嵌入资源打包）
cp app/Clipa.html host/app.html        # 或 app/Clipa-iOS.html

# 2. 完整版（自包含运行时，约 79 MB）
cd host
dotnet publish -c Release -o publish-full
python3 patch_lang.py publish-full/MuseClipStation.exe

# 3. 轻量版（依赖系统 .NET 8 运行时，约 28 MB）
dotnet publish -c Release -p:SelfContained=false -p:EnableCompressionInSingleFile=false -o publish-lite
python3 patch_lang.py publish-lite/MuseClipStation.exe
```

产物改名为 `Clipa-完整版.exe` / `Clipa.exe` 后分发。体积自检：完整版应约 79 MB；若只有十几 MB，说明打成了资源缺失的包，运行会无界面。

## 打包 iOS 风格版时注意

两版若装在同一台机器上，需要隔离数据目录、程序集名与自启注册表名（改为 `ClipaIOS`），否则会互相覆盖窗口位置与设置。

## 桥协议（摘要）

页面 → 宿主：`ready`、`pin`、`dragWindow`、`hideWindow`、`capture`、`pasteNative`、`copyFiles`、`copyPaths`、`contentSize`、`pinImage`、`ocr`、`startNativeDrag`、`captureAffinity`、`viewImage`、`openFile`、`openPath`、`pickSaveDir`、`saveCopy`、`startRecord`、`stopRecord`、`deleteRecCache`、`autoStart`、`getFonts`、`themeBg`、`openUrl`、`deleteLocalFiles`、`setHotkey`、`scanQr`

宿主 → 页面：`captured`、`nativeFiles`、`copyFilesResult`、`ocrResult`、`qrResult`、`enterPeek`、`exitPeek`、`pinState`、`autoStartState`、`saveDirPicked`、`recordStarted`、`recorded`、`recordFailed`、`recordCanceled`、`fontList`、`deleteLocalResult`、`hotkeyResult`、`qrScanResult`、`qrPicked`

## 二维码解码

宿主 `DecodeQrCore` 是多策略管线，缺一不可：灰度变体（原图 / 高光扩展 / 大图 0.5 缩放 / 小图 2 倍放大）× 全局直方图二值化优先 × 普通 QRCodeReader 优先、多码阅读器补充 × 正常 / 反色。只用多码阅读器会对渐变、圆点等艺术二维码失效。
