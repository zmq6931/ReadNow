# ReadNow

Windows 划词 / 截图朗读小工具（C# WPF，.NET 8）。

## 功能

- **划词朗读**：鼠标划选文字 → 光标旁弹出小窗 → 一键朗读；支持 `Ctrl+A` 全选触发
- **断点续播**：停止后记位置，下次从该句继续；自然读完自动清零
- **倍速滑块**：0.5x ~ 4.0x，朗读中拖动即时生效，全局记忆
- **截图朗读**：
  - `Alt+Q` 内置区域截图（微信式冻结多屏画面 → 框选 → OCR → 弹窗）
  - `Win+Shift+S` / 微信截图等进剪贴板后自动识别
  - `Ctrl+Alt+R` 手动识别剪贴板截图
- **中英互译**：弹窗内一键翻译（Google / MyMemory 双通道，需联网）
- **托盘控制**：划词朗读 / 截图识别独立开关，单实例运行

## 技术要点

- TTS：Windows.Media.SpeechSynthesis（OneCore 自然语音，离线，按句队列播放）
- OCR：Windows.Media.Ocr（系统内置，离线，中英文混排）
- 全局钩子：WH_MOUSE_LL 划词检测 + WH_KEYBOARD_LL `Ctrl+A` 检测
- 设置持久化：`%APPDATA%\ReadNow\settings.json`

## 运行

下载 Release 里的 `ReadNow.exe` 双击即用（需 Windows 10/11 + .NET 8 桌面运行时）。

## 构建

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
