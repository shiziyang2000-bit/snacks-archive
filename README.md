# Snacks 归档版

面向本地视频素材的扫描、转码与归档工具。提供中文界面、持久化队列、归档记录和有界并行处理。

这是基于 [Derek Morris / derekshreds 的 Snacks v2.20.1](https://github.com/derekshreds/Snacks/tree/v2.20.1) 制作的独立派生版本，**不是上游官方发行版，也不代表上游作者**。原项目 MIT 许可证与署名完整保留，来源见 [UPSTREAM](docs/UPSTREAM.md)。

当前版本：**2.20.4**。

## 下载与安装

从本仓库 [Releases](https://github.com/shiziyang2000-bit/snacks-archive/releases) 下载 DMG、中文安装说明和 SHA256 校验清单。

| 平台 | 本分支状态 |
| --- | --- |
| Apple 芯片 Mac、macOS 26 或更新 | 当前提供的 arm64 DMG；已在 macOS 26.6.2 验证媒体工具与隔离后端启动 |
| Intel Mac、Windows、Linux / Docker | 保留上游相关源码；本分支未完成对应安装包实测，不提供兼容承诺 |

DMG 包含 FFmpeg、ffprobe、自包含 .NET 10 运行时，以及 Sony MP4 特定元数据处理所需的 MP4Box 与依赖，不需要用户安装 Homebrew 或 .NET。拖入“应用程序”后运行。

当前 DMG 是 **ad-hoc 本地签名，未使用 Apple Developer ID，也未经过 Apple 公证**。首次被系统拦截时，可在尝试打开后进入“系统设置 → 隐私与安全性 → 仍要打开”。组织策略或其他 Gatekeeper 提示可能仍会阻止运行；不要全局关闭系统安全保护。详见随包安装说明。

## 开始使用

首次安装会创建空队列和当前用户自己的配置，不包含开发者的任务、素材或账号。已用过同名归档版时，会继续使用该机器已有的用户数据。

1. 先复制少量素材作为测试，确认输出格式、编码和文件处理参数。
2. 通过程序内的文件夹选择器选择自己的素材目录。
3. 检查队列、完成与失败记录，再逐步处理正式素材。

**归档模式支持原位替换源文件。** 是否替换以及是否保留额外备份取决于任务设置；不要将“有异常保护”理解为“不会修改原片”。处理重要素材前保留独立备份，并检查输出位置与替换选项。

macOS 用户数据位于 `~/Library/Application Support/Snacks-Archive/`，与上游 Snacks 的资料目录隔离。构建脚本不会启动、安装或更新正在运行的应用。

## 相对上游的主要更改

- 独立的“Snacks 归档版”身份与简体中文 / English 菜单、界面。
- 面向文件夹的归档任务、持久化历史、受保护的原位替换事务与恢复处理。
- 有界并行与持续补位；确认属于单文件的权限、锁定等异常单独记录，避免阻塞整个目录。
- 实时归档容量、转码时长与统计覆盖率显示，以及文件定位等原生桌面操作。
- 对已识别的 Sony MP4 `rtmd` / 时间码关联路径进行针对性保留；不承诺支持所有相机或元数据布局。
- 分发包使用包内 MP4Box 路径；macOS 26 最低要求与本包原生依赖一致。

上游视频、音频、集群与第三方服务功能的源码仍保留；归档版的实测重点是本地 Apple 芯片 Mac 工作流。

## 构建与测试

需要 .NET 10 SDK、Node.js 22+。先运行源码检查：

```bash
npm --prefix electron-app ci
npm --prefix electron-app run check
dotnet test Snacks.Tests/Snacks.Tests.csproj --configuration Release
```

Apple 芯片 Mac 的打包还需要 macOS 26+、Xcode 命令行工具、GPAC，以及自行取得并解包的上游官方 v2.20.1 arm64 App 资源：

```bash
export SNACKS_ORIGINAL_APP="/path/to/upstream/Snacks.app"
export SNACKS_MP4BOX="$(brew --prefix gpac)/bin/MP4Box"
bash build-cn-mac.sh --check
bash build-cn-mac.sh
```

每次打包使用独立临时构建目录，默认只做 ad-hoc 签名，不公证、不推送 GitHub、不安装 App。完整参数、资源来源与验证范围见 [构建说明](docs/BUILDING.md)。本仓库不携带 node_modules、媒体工具二进制、SDK 或运行资料。

现有前端测试共 38 项。初始公开准备验证了这些测试、新增 MP4Box 依赖打包与合成媒体处理；没有把静态语法检查称为完整重建。已发布 DMG 的来源与实测边界见 [2.20.4 分发记录](docs/RELEASE-v2.20.4.md)。

## 贡献与许可

问题与改进建议请在[本仓库 Issues](https://github.com/shiziyang2000-bit/snacks-archive/issues)提出。提交日志或配置前请去掉媒体路径、服务地址、账号与凭据。

Snacks 原代码：Copyright (c) 2026 Derek Morris，采用 [MIT License](LICENSE)。归档版修改作为同一 MIT 项目的派生代码公开；FFmpeg、GPAC、Electron、.NET 与其他第三方组件仍适用各自许可证。上游截图、图标和字体来自原项目，不作本项目原创声明。
