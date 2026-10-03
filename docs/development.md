# 桌面版开发说明

用户安装与使用请先阅读 [README](../README.md)。本文件说明当前源码布局、验证方法及打包方式。以下命令均从仓库根目录执行。

## 环境

- Windows 10 / 11，x64。
- .NET 10 SDK；项目使用 WPF、Windows Forms 托盘和 Win32 API，测试也需要 Windows。
- Node.js 与 sharp 仅用于重新生成图标，不是应用运行或常规 .NET 构建的依赖。

## 目录约定

```text
WordCatcherDesktop/
├── WordCatcher.Desktop.sln       # 应用与三个测试项目
├── nuget.config                 # NuGet 源配置
├── src/
│   ├── WordCatcher.App/          # WPF 窗口、主题、ViewModel、取词、托盘
│   ├── WordCatcher.Core/         # 模型、接口、词形还原与挖空算法
│   └── WordCatcher.Infrastructure/ # 数据库、词典、翻译、Anki、密钥存储
├── tests/
│   ├── WordCatcher.App.Tests/
│   ├── WordCatcher.Core.Tests/
│   ├── WordCatcher.Infrastructure.Tests/
│   └── Fixtures/                # 可提交的回归样本与数据署名
├── tools/
│   ├── AcceptanceProbe/         # Windows 取词与剪贴板验收工具
│   ├── ShowcaseCapture/         # 演示数据的真实 WPF 界面离屏渲染
│   └── build-icons.mjs          # 图标生成脚本
├── assets/icons/                # 图标源文件与各尺寸资源
├── docs/                        # 开发说明、调查和验收记录
├── .github/workflows/           # 标签发布流程
└── artifacts/                   # 本地打包与临时输出，不提交
```

桌面版是独立仓库，因此源码不再嵌套在 `desktop/` 中。运行时使用的 ICO/PNG 位于 `src/WordCatcher.App/Assets/`，与图标源资源分开。程序集声明统一放在 `Properties/AssemblyInfo.cs`。不要把真实词库、配置、密钥或下载的词典放进源码目录。

移动目录时只移动跟踪的源码与资源；原 `desktop/` 中可能留下被忽略的旧 `bin/obj`。这些是本地生成物，不属于当前项目结构，关闭旧程序后可自行清理。

## 构建、运行与测试

```powershell
dotnet restore WordCatcher.Desktop.sln
dotnet build WordCatcher.Desktop.sln -c Release --no-restore
dotnet test WordCatcher.Desktop.sln -c Release --no-restore
dotnet run --project src/WordCatcher.App/WordCatcher.App.csproj --no-restore
```

解决方案仅定义 `Debug` 和 `Release`。使用自定义配置以避免覆盖正在运行的程序时，请直接构建项目、逐个运行测试项目；不要给解决方案传入不存在的配置。

若网络不可用且依赖已完整缓存，可临时从本机 NuGet 缓存恢复，例如：

```powershell
dotnet restore WordCatcher.Desktop.sln --source "$env:USERPROFILE/.nuget/packages" -p:NuGetAudit=false
```

该命令只适用于缓存已有全部依赖的情况，不修改仓库默认 NuGet 源。

## Windows 验收工具

```powershell
dotnet run --project tools/AcceptanceProbe -- --clipboard-self-test
```

此模式创建独立窗口站与桌面，验证空剪贴板、多格式快照、再次复制及序号/所有者校验，不触碰交互桌面的剪贴板。创建隔离环境失败时中止。它不能替代 PDF、Word、浏览器和真实 Anki 的验收。2026-10-02 复测在打开隔离剪贴板时失败，当前不能将此命令视为已通过的原生验收；详情见 [本轮验证记录](repository-organization-2026-10-02.md)。

不加该参数时进入手动取词探针。先从托盘退出 Word Catcher，避免默认热键冲突，再执行：

```powershell
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
dotnet run --project tools/AcceptanceProbe -- artifacts/capture-result.json
```

根据控制台提示，在测试文档中选择文字并触发快捷键。输出可能包含选中文字和来源信息，不提交到 Git。

## 图标生成

编辑 `assets/icons/icon.svg` 后，在已能解析 sharp 的环境中运行：

```powershell
node tools/build-icons.mjs
```

也可以将已安装的 sharp 模块绝对路径作为第一个参数。脚本更新 `assets/icons/` 的多尺寸 PNG 和应用内嵌的 ICO/PNG；这些资源需要随源码提交。

## 打包与发布

本地打包：

```powershell
dotnet publish src/WordCatcher.App/WordCatcher.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/publish
Compress-Archive -Path artifacts/publish/* -DestinationPath artifacts/WordCatcher-local-win-x64.zip
```

发布工作流见 [release.yml](../.github/workflows/release.yml)：推送 `v*` 标签后，先运行测试，再构建自包含 x64 包并创建 GitHub Release。标签值决定发布版本。发布前需要完成真实场景验收，确认版本号与标签一致；测试通过本身不代表验收或发布完成。

## 产品展示图

README 展示图位于 `docs/images/showcase.png`，WPF 原图由 `tools/ShowcaseCapture` 生成。版式源文件与导出命令见 [展示图说明](images/README.md)。生成工具使用演示数据，不访问个人设置、词库、剪贴板或网络服务。

## Git 与文档约定

- 跟踪源码、测试、测试样本、图标和构建配置。
- 忽略 `bin/obj`、`TestResults`、覆盖率、`artifacts`、本机数据库、密钥、日志、`.env` 和 `.spec-workflow`；保留可公开的 `.env.example` 模板。
- `.gitignore` 不会自动取消已跟踪文件。核查是否有生成物误入版本库时，使用 `git ls-files -ci --exclude-standard`。
- 用户可执行的步骤放在根 README；实现约定放在本文件；调查与验收记录保留在 [文档索引](README.md)，不要混入用户入门流程。
- 历史记录保留原测试数量和结果，旧路径需要说明，不能将待验证项目写成已完成。
- `.editorconfig` 统一 UTF-8（无 BOM）、缩进和换行，`.gitattributes` 固定文本换行及二进制资源类型。
