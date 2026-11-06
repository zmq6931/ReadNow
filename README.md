# ReadNow

Windows 划词 / 截图朗读小工具（C# WPF，.NET 8）。单文件绿色 exe，常驻系统托盘。

## 功能

- **划词朗读**：鼠标划选文字 → 光标旁弹出小窗 → 一键朗读；`Ctrl+A` 全选同样触发
- **键盘流操作**：弹窗弹出即聚焦「朗读」按钮——空格/回车播放暂停，`Esc` 停止关闭；拖完倍速滑块焦点自动回到朗读键
- **精确暂停续播**：暂停即冻结在当前发音位置，继续时一词不差；换新内容/变速/重读时按句级书签续播
- **倍速滑块**：0.5x ~ 4.0x，朗读中拖动**原地变速**（按播放进度估算位置，不跳回句首），设置全局记忆
- **一键复制**：弹窗内「📋 复制」按钮，把识别/选取的全部文字复制到剪贴板
- **中英互译**：弹窗内一键翻译（Google / MyMemory 双通道，需联网）
- **截图朗读**：
  - `Alt+Q` 内置区域截图（微信式冻结多屏画面 → 框选 → OCR → 弹窗）
  - `Win+Shift+S`、微信 `Alt+A` 等截图进剪贴板后自动识别
  - `Ctrl+Alt+R` 手动识别剪贴板截图
- **功能开关三件套**：弹窗内复选框与托盘菜单双向同步、重启保留——划词朗读 / 截图识别 / 开机自动启动（注册表 Run 键）
- **托盘找回界面**：右键托盘图标 →「显示界面」，或双击图标，随时重新弹出主界面（Esc 关窗、开关全关也不怕找不到）
- **单实例运行**：重复启动自动拦截；操作均有托盘气泡反馈

## 技术要点

- TTS：Windows.Media.SpeechSynthesis（OneCore 自然语音，离线，按句队列播放 + MediaPlayer 精确暂停）
- OCR：Windows.Media.Ocr（系统内置，离线，中英文混排）
- 全局钩子：WH_MOUSE_LL 划词检测 + WH_KEYBOARD_LL `Ctrl+A` 检测
- 焦点抢占：AttachThreadInput 借道前台线程，保证弹窗弹出即可键盘操作
- 设置持久化：`%APPDATA%\ReadNow\settings.json`；异常日志：`%APPDATA%\ReadNow\error.log`

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
