# 构建 Snacks 归档版

本文适用于本仓库 2.20.4。默认构建不安装 App、不读取既有队列、不公证，也不发布任何远程制品。上游的 Windows、Docker 和其他平台代码仍保留，但本分支只验收 Apple 芯片 macOS 26+ 安装包。

## 工具与源码检查

- .NET 10 SDK（不是仅运行时）。
- Node.js 22 或更新版本，以及 npm；Electron 43 由锁文件安装。
- macOS 打包使用 Apple 芯片、macOS 26+、Python 3.9+、Xcode 命令行工具。

从仓库根目录执行：

```bash
npm --prefix electron-app ci
npm --prefix electron-app run check
dotnet test Snacks.Tests/Snacks.Tests.csproj --configuration Release
```

`npm run check` 执行 Electron 入口语法、版本同步与现有 38 项前端/契约测试。测试文件不要求启动 Electron GUI。后端测试使用测试工作目录；不要将 `SNACKS_WORK_DIR` 指向已有生产资料。

## Apple 芯片 macOS DMG

当前分发包依赖的 FFmpeg/ffprobe 原生组件最低系统标记是 macOS 26.0，不是旧 Info.plist 曾写的 11.0。不能仅修改 plist 就声称支持旧系统。

准备以下本地输入：

1. 自行取得[上游官方 v2.20.1 arm64 发行包](https://github.com/derekshreds/Snacks/releases/tag/v2.20.1)，解包得到 `Snacks.app`。脚本只读其 FFmpeg、OCR 动态库、字典和已有许可文件，不启动它，也不复用其中的业务 DLL。
2. 在构建机器安装 GPAC（例如 `brew install gpac`），或提供已有的 arm64 MP4Box 路径。脚本不自动安装或升级 Homebrew 软件。
3. 准备上述 SDK 和 Node 工具链。首次 `npm ci` 和 .NET restore 可能联网获取依赖；离线构建需要先准备匹配缓存。
4. 收集媒体资源与 .NET 等第三方组件需随二进制保留的许可/声明。可通过 `SNACKS_EXTRA_LICENSES` 传入本地目录；原 App 未附许可时，不能假定脚本会凭空补齐完整重分发材料。

```bash
export SNACKS_ORIGINAL_APP="/path/to/upstream/Snacks.app"
export SNACKS_MP4BOX="$(brew --prefix gpac)/bin/MP4Box"
# 可选：现成的第三方许可证/声明目录
# export SNACKS_EXTRA_LICENSES="/path/to/third-party-licenses"
bash build-cn-mac.sh --check
bash build-cn-mac.sh
```

`--check` 仅核对本机系统、输入 App 版本、工具和 MP4Box 架构，不编译、不下载、不打包；其成功不等同于完整构建成功。

`build-cn-mac.sh` 和 `build-mac.sh` 都转到 `scripts/build-archive-mac.sh`。每次构建：

1. 在 `.build-archive/run-XXXXXXXX/` 新建独立目录，复制指定上游媒体资源。
2. 用当前源码发布自包含 `osx-arm64` 后端；中间文件也在本次目录，不复用已安装 App 的后端或用户资料。
3. 将 MP4Box 及所有递归非系统动态库复制到独立 `gpac/`，改写为 `@loader_path`，逐个检查 arm64、重签名并验证依赖闭合。缺失依赖或同名冲突会停止构建。
4. 运行源码检查；收集项目 MIT、Electron/Chromium、tree-kill、本地 GPAC 依赖和显式额外提供的许可材料。
5. 用生成的资源配置创建 ad-hoc 签名 DMG，并写入 `SHA256SUMS.txt`。输出默认位于本次目录的 `dist/`；脚本不会覆盖非空的指定输出目录。

可覆盖参数：

| 环境变量 | 含义 |
| --- | --- |
| `SNACKS_ORIGINAL_APP` | 必填，上游 v2.20.1 arm64 App 路径 |
| `SNACKS_MP4BOX` | MP4Box 可执行文件；缺省时尝试 `brew --prefix gpac` |
| `SNACKS_DOTNET`、`SNACKS_NODE`、`SNACKS_NPM` | 工具可执行文件；缺省从 PATH 查找 |
| `SNACKS_BUILD_WORK` | 本地构建工作根目录 |
| `SNACKS_ARCHIVE_OUTPUT` | 必须为空的最终输出目录 |
| `SNACKS_EXTRA_LICENSES` | 额外随包保留的许可/声明目录 |

也可以单独检查 MP4Box 打包步骤：

```bash
python3 electron-app/scripts/bundle-gpac-mac.py /path/to/new-gpac-bundle \
  --mp4box "$(brew --prefix gpac)/bin/MP4Box" \
  --licenses /path/to/new-gpac-licenses \
  --manifest /path/to/gpac-manifest.json
```

只修改目标目录，不改源 MP4Box 或 Homebrew；输出 manifest 不写绝对源路径。第三方二进制可能仍含编译时前缀字符串，重新定位动态链接并不会改写其所有内部常量。核心 MP4Box 媒体操作应在没有 Homebrew 访问能力的隔离环境再次验证。

## 签名与公证

默认构建显式禁用发布和 Apple 账号环境，只有 ad-hoc 签名。不要使用上游作者的身份或证书；Windows 中原作者 `publisherName` 默认值也已移除。

仓库保留 `electron-app/scripts/notarize.js` 供维护者自行配置可信签名流程；只有显式 `SNACKS_NOTARIZE=1` 且提供 `APPLE_ID`、`APPLE_APP_SPECIFIC_PASSWORD`、`APPLE_TEAM_ID` 时才会提交。支持的默认 Mac 构建入口会清除这些变量，因此不自动公证。真实凭据只能保存在本机或维护者自己的 CI secrets，不能提交。

检查一个已完成签名/公证的 App：

```bash
codesign --verify --deep --strict "/path/to/Snacks 归档版.app"
xcrun stapler validate "/path/to/Snacks 归档版.app"
```

严格签名校验成功不表示已经公证，也不能保证通过每台机器的 Gatekeeper 策略。

## 独立开发实例

需要验证后端时，使用全新的工作目录和独立回环端口：

```bash
SNACKS_WORK_DIR="$(mktemp -d /tmp/snacks-dev.XXXXXXXX)" \
ASPNETCORE_ENVIRONMENT=Production \
ASPNETCORE_URLS=http://127.0.0.1:16767 \
SNACKS_SCAN_Enabled=false \
dotnet run --project Snacks/Snacks.csproj --no-launch-profile
```

确认端口未占用，不使用既有生产工作目录。Electron 使用 `app.getPath("appData")/Snacks-Archive`；仅改 HOME 或传 `--user-data-dir` 不保证与正在运行的归档版隔离。不要为了检查公开源码而同时启动共享资料目录的第二个 GUI 实例。

## 其他平台与上游脚本

Windows 的 `build-installer.bat`、`run-electron-dev.bat` 等作为上游参考保留，会使用 `ffmpeg.exe` / `ffprobe.exe`；本分支没有完成 Windows 安装包和 MP4Box 分发验收。`create-cert.bat` 仅创建通用开发证书，不能代表上游作者或可信公众签名。

`build-and-export.bat` 已改为只构建/导出本地 `snacks-archive` 镜像，`build-and-export-dev.bat` 只构建本地 dev 标签，均不会 Docker push。`deploy-compose.yml` 与 `unraid/snacks.xml` 明确标注为使用原上游镜像的参考模板，不是本分支的容器发行物。

GitHub CI 只在手动或 PR 时运行，权限为 contents: read，不含发布、部署、注册表写入或签名账号。

## Release checklist

- 确认版本同步、前端检查和适用后端测试通过。
- 复核公开文件与源码归属；不携带任务数据库、日志、素材路径、账号、配置或本机开发资料。
- 确认媒体工具全部依赖随包携带，核对架构与真实最低系统要求；保留对应第三方许可材料。
- 在新资料目录验证运行；不要接触正在工作的生产实例。
- 验证签名、DMG 校验与挂载复制，写明是否公证、实测平台和没有验证的部分。
- 手动决定并执行 Release 上传；本构建脚本和 CI 不会自动发布。

初始公开准备只验证了语法、现有前端测试和新增 MP4Box 打包/运行步骤，没有重编整个最终 DMG。已发布 2.20.4 DMG 的独立验收范围见 [分发记录](RELEASE-v2.20.4.md)。
