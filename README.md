# VU Volume Meter (VU Widgets)

A Windows desktop widget **VU level meter** that displays system output and microphone volume in real time. It supports multiple dials, mic gain, true mute, and restoration after monitor hot-plug / reconnection.

- Version: **v3.6.9** (built 2026-11-18)
- Runtime: Windows 10 / 11 (.NET Framework 4.x)
- Stack: C# / WinForms; audio capture via [NAudio](https://github.com/naudio/NAudio)

## Repository Layout

```
├─ 源码/            # C# sources, icons & layer assets, NAudio.dll, compile.rsp
├─ 成品/            # Compiled VU Widgets v3.6.9.exe
├─ 验证截图/        # Acceptance screenshots for each state
├─ 3.6.9升级说明.md # Change log & acceptance notes for this version
├─ LICENSE          # GPL-3.0
└─ README.md
```

## Build from Source

Use the .NET Framework built-in `csc.exe` (no Visual Studio required):

```powershell
cd 源码
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe @compile.rsp
```

The output is `VU Widgets v3.6.9.exe`.

## Run

Double-click `成品\VU Widgets v3.6.9.exe`. Before removing the program, use the widget's right-click menu to close it and clear its configuration so no system residue remains.

## Credits

- Audio capture: NAudio (MIT)
- Dial artwork references the Coolfish Desktop / XWidgets style

## License

This project is open-sourced under **GPL-3.0**; see [LICENSE](LICENSE). For non-commercial study and testing only. The author is not liable for any issues arising from its use and does not commit to future upgrades.

---

# 中文说明

一款 Windows 桌面挂件式 **VU 电平表**，实时显示系统输出与麦克风音量，支持多表盘、Mic 增益、真静音与多显示器拔插重连还原。

## 目录结构

```
├─ 源码/            # C# 源码、图标与图层素材、NAudio.dll、编译响应文件 compile.rsp
├─ 成品/            # 已编译的 VU Widgets v3.6.9.exe
├─ 验证截图/        # 各状态验收截图
├─ 3.6.9升级说明.md # 本版本改动与验收记录
├─ LICENSE          # GPL-3.0
└─ README.md
```

## 从源码编译

使用 .NET Framework 自带的 `csc.exe`（无需 Visual Studio）：

```powershell
cd 源码
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe @compile.rsp
```

编译产物为 `VU Widgets v3.6.9.exe`。

## 运行

直接双击 `成品\VU Widgets v3.6.9.exe`。清除程序前，请先在挂件右键菜单中选择关闭并清除配置，以彻底移除系统残留。

## 鸣谢

- 音频采集：NAudio（MIT）
- 表盘美工参考酷鱼桌面 / XWidgets 风格

## 许可

本项目以 **GPL-3.0** 协议开源，详见 [LICENSE](LICENSE)。仅限非商业用途的学习、测试；作者不对使用过程中出现的问题负责，也不承诺后续版本升级。
