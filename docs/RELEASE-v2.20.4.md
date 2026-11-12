# 2.20.4 分发记录

- 应用版本：2.20.4 / build 20261111；分享封装日期：2026-11-12。
- 平台：Apple 芯片 arm64，macOS 26 或更新；实测 macOS 26.6.2。
- 文件：`Snacks-Archive-2.20.4-macOS26-arm64.dmg`，221,878,021 bytes。
- DMG SHA256：`2b02e2b48ef0bdc7dce838701ac985153dd094bfc9ece95404187ec69254fbe5`。
- 原样保留的业务 DLL SHA256：`cb1fca7bfdcf625e5c92ae3077abaca3253e995d12b01e750fae8133d25afa15`。
- 分享包 ASAR SHA256：`68ff441df1244e888821d0aaf48f924daf4d858370471ad13bfbee51e42ac9f4`。

安装包由已验收的 2.20.4 App 封装，补入 MP4Box 及 27 个递归依赖，将其默认路径改为 App 内相对路径，并将最低系统标记改为 26.0。公开源码纳入同一功能改动；新增通用构建脚本与文档不曾用于声称逐字节复现该既有 DMG。重新编译时 SDK、依赖版本、Git 元数据和签名差异都会影响哈希。

已验证：99 个 Mach-O 均含 arm64，无非系统绝对动态链接依赖；禁止访问 Homebrew 目录后媒体工具仍可运行；合成视频编码、MP4Box 重封装通过；隔离后端 health/首页正常，7 张新建业务表为空；严格签名、DMG 校验、挂载复制后 728 项文件/链接/权限比对通过。

签名是 ad-hoc，未 Developer ID 签名、未 Apple 公证。为保护正在工作的正式实例，本次分享包验收没有额外启动第二个 Electron 图形界面，也没有在接收者 Mac 上实测。第三方库可能保留编译前缀字符串；运行不依赖构建者的 Homebrew 目录。无用户任务、数据库、素材或凭据随包分发。

下载与具体打开步骤见[本仓库 Releases](https://github.com/shiziyang2000-bit/snacks-archive/releases)。
