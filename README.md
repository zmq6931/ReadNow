# ReadNow

Windows 划词 / 截图朗读小工具（C# WPF，.NET 8）。单文件绿色 exe，常驻系统托盘。

## 功能

- **划词朗读**：鼠标划选文字 → 光标旁弹出小窗 → 一键朗读；`Ctrl+A` 全选同样触发
- **断点续播**：停止时记住位置，下次从该句继续；自然读完自动清零
- **倍速滑块**：0.5x ~ 4.0x，朗读中拖动即时生效，设置全局记忆（重启保留）
- **一键复制**：弹窗内「📋 复制」按钮，把识别/选取的全部文字复制到剪贴板
- **中英互译**：弹窗内一键翻译（Google / MyMemory 双通道，需联网）
- **截图朗读**：
  - `Alt+Q` 内置区域截图（微信式冻结多屏画面 → 框选 → OCR → 弹窗）
  - `Win+Shift+S`、微信 `Alt+A` 等截图进剪贴板后自动识别
  - `Ctrl+Alt+R` 手动识别剪贴板截图
- **托盘控制**：划词朗读 / 截图识别独立开关（截图开关同时控制快捷键）；操作有气泡反馈
- **单实例运行**：重复启动自动拦截，避免钩子叠加

## 技术要点

- TTS：Windows.Media.SpeechSynthesis（OneCore 自然语音，离线，按句队列播放 + 句级书签续播）
- OCR：Windows.Media.Ocr（系统内置，离线，中英文混排）
- 全局钩子：WH_MOUSE_LL 划词检测 + WH_KEYBOARD_LL `Ctrl+A` 检测
- 设置持久化：`%APPDATA%\ReadNow\settings.json`
- 异常日志：`%APPDATA%\ReadNow\error.log`

## 运行

下载 [Releases](../../releases) 里的 `ReadNow.exe` 双击即用（需 Windows 10/11 + .NET 8 桌面运行时）。

## 构建

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## 已知限制

- 在以管理员身份运行的窗口中划词无法取词（Windows 权限隔离）
- 内置框选截图基于冻结画面，多屏 DPI 不一致时裁剪位置可能略有偏差
- 部分截图工具（如微信）放入剪贴板的图片格式特殊，自动识别可能失败，可用 `Alt+Q` 替代
