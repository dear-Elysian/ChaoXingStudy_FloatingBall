# 超星学习通悬浮球

一个面向 Windows 的学习通桌面悬浮球。程序复用用户在内嵌网页中的学习通登录状态，集中查看课程作业、随堂练习、考试、主题讨论和消息，并按需手动刷新。

程序以只读展示为主，不会自动提交作业或自动刷课。AI 帮做功能目前仅适用于作业和随堂练习，可将生成的答案填入页面，内容应由用户检查并自行决定是否提交。使用 AI 功能需要自行填写 API 接口和 API Key。项目开发过程中使用了 DeepSeek-V4.1-Flash 和 Kimi-K3。

> 项目当前仍在开发中，功能和使用方式可能变化。完整需求、目录约定和技术设计见[方案设计文档](./方案设计(未全部完成).md)。

## 功能概览

- WPF 桌面外壳和 WebView2 界面，提供悬浮球、登录窗口、主面板和设置。
- 通过 Java 核心读取课程相关信息；可查看作业、随堂练习、考试、主题讨论和消息。
- 登录后可按设置自动刷新，也可手动刷新；支持按课程和模块调整展示与刷新范围。
- 可配置兼容 OpenAI 风格接口的 AI 服务，用于辅助填写作业和练习答案。
- 便携式打包：随包提供裁剪后的 Java 运行时和自包含 .NET 程序，不要求使用者另装 Java 或 .NET。

## 免责声明

- 本项目仅供学习交流和技术演示使用，请勿用于违法违规用途。
- 使用本程序所产生的后果由使用者自行承担。

## 下载与运行

从仓库的 [Releases](https://github.com/dear-Elysian/ChaoXingStudy_FloatingBall/releases) 下载对应版本的 ZIP，解压后运行：

```text
学习通悬浮球/app/launcher.exe
```

首次启动后，程序会在 `学习通悬浮球/data/` 下创建本地数据目录。该目录包含账号会话、设置、浏览器配置和日志等个人数据，请勿公开或发送给他人。

### 运行要求

- Windows 10/11 x64（当前默认发布目标为 `win-x64`）。
- Microsoft Edge WebView2 Runtime。大多数 Windows 10/11 系统已安装；若缺失，程序会提示打开微软官方下载页。安装后重新启动程序。
- 正常使用需要能够访问学习通服务。AI 功能还需要用户自行配置可用的 API 服务和 API Key。

发布包自带 Java 运行时和 .NET 运行时。仓库源码开发环境的要求见下文。

## 从源码构建

使用 Git 克隆源码：

```powershell
git clone https://github.com/dear-Elysian/ChaoXingStudy_FloatingBall.git
Set-Location .\ChaoXingStudy_FloatingBall
```

### 开发环境

- Windows x64
- .NET 10 SDK（WPF 项目目标框架为 `net10.0-windows`）
- JDK 17 或更高版本，且 `java`、`jlink` 可从 `PATH` 调用
- Apache Maven，且 `mvn` 可从 `PATH` 调用
- Microsoft Edge WebView2 Runtime

### 构建便携包（也可直接从 Releases 下载）

在项目根目录打开 PowerShell，运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

脚本依次执行 Maven Java 构建、.NET 自包含单文件发布、`jlink` 运行时裁剪，并把资源组装到：

```text
学习通悬浮球/
└── app/
    ├── launcher.exe
    ├── core/core.jar
    ├── jre/
    └── resources/
```

构建失败时脚本会停止并报告对应步骤。**脚本会先删除整个 `学习通悬浮球/` 输出目录再重建。** 如果这个目录中有需要保留的数据或文件，请先备份；不要把该目录当作日常运行数据的唯一存放位置。

构建成功后，可在本机验证 `app\launcher.exe`，再将干净的 `学习通悬浮球` 文件夹压缩为 ZIP，通过 GitHub Releases 发布。分享前确认压缩包中没有 `data/`、账号会话或其他个人数据。

可选参数示例：

```powershell
.\scripts\package.ps1 -OutDir '学习通悬浮球' -Rid 'win-x64'
```

## 本地数据与隐私

- 登录会话由程序加密后保存在本机；AI API Key 使用当前 Windows 用户加密后保存在本机。
- 程序还会在 `data/` 中保存账户元信息、设置、缓存、浏览器 Profile、日志及数据快照。
- `data/` 已列入 `.gitignore`。不要提交或分发其中的文件，即使文件名或内容看起来是缓存、JSON 或加密数据。
- AI 服务地址、模型及 Key 由使用者自行配置；请求会发送到设置中指定的服务。

## 仓库结构

```text
app/
  core/core.jar              Java 核心构建产物，开发运行所需
  resources/                 网页界面与程序图标
scripts/
  package.ps1                Windows 便携包构建脚本
src/
  core-java/                 Java 核心及 Maven 配置
  shell-csharp/              WPF 外壳及 WebView2 集成
方案设计(未全部完成).md       需求与技术方案
```

## 问题反馈

请通过 GitHub Issues 提交问题，并附上 Windows 版本、程序版本和复现步骤。分享日志或截图前，请检查并遮盖账号、Cookie、个人信息及 API Key。
