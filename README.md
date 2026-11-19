# VU 音量表（VU Widgets）

一款 Windows 桌面挂件式 **VU 电平表**，实时显示系统输出与麦克风音量，支持多表盘、Mic 增益、真静音与多显示器拔插重连还原。

- 当前版本：**v3.6.9**（构建于 2026-11-18）
- 运行环境：Windows 10 / 11（.NET Framework 4.x）
- 技术栈：C# / WinForms，音频采集依赖 [NAudio](https://github.com/naudio/NAudio)

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
