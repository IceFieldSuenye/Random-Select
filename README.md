# 随机抽取器（Random Select）

> **AI 生成提示**
>
> 本项目的大部分代码、文档与界面素材由 AI（DeepSeek）生成或辅助生成，可能存在错误、遗漏或不合理的实现。使用前请自行评估风险；发现问题欢迎反馈。

一款面向 Windows 的随机抽取工具，支持卡池管理、加权随机、抽卡动画、历史记录、抽取报告与封面编辑。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4.svg)](#系统要求)

## 下载

最新版本：**3.76.0**

- [下载 Random-Select-3.76.0.exe](https://github.com/IceFieldSuenye/Random-Select/releases/latest)
- [全部版本与历史源码](https://github.com/IceFieldSuenye/Random-Select/releases)

> 发布包只提供 3.76.0 一个 exe；3.75.0及更早的版本以源码形式提供，不再提供 exe。

## 功能

- **卡池管理**：新建、导入、导出、复制、重命名；支持为卡池设置密码。
- **随机抽取**：单次 / 多次抽取、不重复抽取、加权概率。
- **高级抽卡规则**：保底、UP、光锥等规则，以及抽取详情与概率统计。
- **抽取动画与结果页**：抽取过程动画、结果展示与再次抽取。
- **历史记录与报告**：记录每次抽取，生成抽取报告（长图 / PDF）并分享。
- **封面编辑**：裁剪、文字、画笔、橡皮擦、图层与撤销。
- **个性化**：浅色 / 深色主题、背景模糊、动画开关、简洁 / 详细布局。
- **欢迎页与帮助**：首次启动引导，软件内提供完整帮助。

## 系统要求

- Windows 10 / 11（x64）
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（发布包为框架依赖的单文件程序）

## 使用

1. 下载 `Random-Select-3.76.0.exe` 并双击运行。
2. 首次启动时按欢迎页提示选择卡池文件夹与资源文件夹。
3. 在「选项 → 帮助」中查看卡池结构、抽取规则和各项设置的详细说明。

卡池以文件夹为单位保存，每个卡池包含名单文件与封面等资源；具体格式见软件内帮助页。

## 从源码构建

需要 .NET 10 SDK。

```powershell
dotnet build "Random Select.csproj" -c Release
```

发布为单文件：

```powershell
dotnet publish "Random Select.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## 仓库结构

| 路径 | 说明 |
| --- | --- |
| `App.xaml` / `MainWindow.xaml` | 程序入口与主窗口（根目录只留工程文件、许可证与说明） |
| `Pages/` | 主页、历史记录、抽取报告、抽取结果、抽取动画、选项、编辑封面等页面 |
| `Dialogs/` | 各类对话框、弹窗与浮动面板 |
| `Controls/` | 自定义控件与交互辅助（开关、卡片、搜索框、提示气泡等） |
| `Core/` | 抽取引擎、卡池读写、封面处理、主题、日志等核心逻辑 |
| `Interfaces/` | 页面 / 按钮宿主等接口定义 |
| `Models/` | 高级抽卡规则等数据模型 |
| `Assets/` | 图标、默认封面与程序清单 |
| `Readme/更新日志.txt` | 更新日志（编译时内嵌到程序） |
| `LICENSE` | MIT 许可证 |
| `THIRD-PARTY-NOTICES.md` | 第三方组件与开源声明 |

## 历史版本说明

- **3.76.0** 是当前最新版本；3.75.1、3.75.2 为问题修复版本，未单独备份源码，仓库根目录即 3.76.0 源码（源码已按 `Pages / Dialogs / Controls / Core` 分目录）。
- **3.9.3 之前的版本源码在早期整理时被误删，无法恢复。** 那些版本已经具备基础的随机抽取功能；由于源码缺失，不再提供对应版本。
- 每个历史版本在 Releases 中以源码压缩包形式提供，不再提供 exe。

## 开源声明

本项目**没有使用第三方开源代码**。程序仅使用 .NET 10 / WPF 框架与 Windows 系统字体，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 许可证

源代码以 [MIT License](LICENSE) 发布，Copyright (c) 2026 冰原雪奈（IceFieldSuenye）。

软件图标、默认封面、界面截图等美术素材版权归原作者所有，不在 MIT 许可范围内。

## 反馈

- 邮箱：ssssssss888777@163.com
- B站：<https://space.bilibili.com/353824557>
- 也可以在 [Issues](https://github.com/IceFieldSuenye/Random-Select/issues) 中反馈。
