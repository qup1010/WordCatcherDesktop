# Word Catcher Windows 桌面版 MVP

Word Catcher 是一款专为个人深度阅读与语言积累设计的 Windows 桌面伴侣应用。在任意应用程序（记事本、VS Code、Edge/Chrome、Word、PDF 阅读器等）中选中文字，按下全局快捷键即可在光标旁展示词典与翻译卡片，并支持一键保存至本地词库与无缝后台离线同步至 Anki。

## 核心特性

- **全局快捷键屏幕取词**：默认 `Alt+Q` 快速获取选中文字，自动识别当前进程与窗口标题，取词完成后自动恢复原有剪贴板内容（支持文本、富文本、图片与文件列表）。
- **离线词典优先**：短词优先检索本地 [ahpxex/open-dictionary](https://github.com/ahpxex/open-dictionary) 词典（遵循 `distribution_entry_v5` 与 `distribution_sqlite_v1` 契约），无需网络连接，秒级响应。
- **智能词形还原**：内置基于原项目的 Lemmatizer 词形还原器，查询屈折变形（复数、时态、比较级、副词等）自动命中词元原形。
- **在线 AI 翻译回退**：未命中离线词典或选中文本为长句时，自动回退调用 OpenAI 兼容接口，并支持手动强制请求“AI 语境详解”。
- **本地优先与 Anki 离线队列**：点击保存立即写入本地 SQLite 词库并创建同步任务；若 Anki 未启动，卡片标记为“待同步”，并在 Anki 启动后自动写入，支持单并发与指数退避重试（30秒 / 2分钟 / 10分钟）。
- **轻量常驻系统托盘**：应用启动后常驻系统托盘，不侵入用户桌面；需要时双击托盘图标打开生词库管理主窗口。
- **安全与隐私**：API Key 使用 Windows DPAPI（`ProtectedData`，CurrentUser）本地加密存储，日志全程脱敏，不记录任何密钥与取词全文。

## 目录结构

```text
desktop/
  WordCatcher.Desktop.sln
  src/
    WordCatcher.Core/              # 领域模型、枚举、接口与核心算法（词形还原、Cloze挖空）
    WordCatcher.Infrastructure/    # SQLite数据库、Open Dictionary离线词典、OpenAI翻译、Anki客户端、DPAPI
    WordCatcher.App/               # WPF表现层、Win32 P/Invoke全局热键与剪贴板捕获、系统托盘
  tests/
    WordCatcher.Core.Tests/        # 核心算法单元测试
    WordCatcher.Infrastructure.Tests/ # 数据库事务、词典解压校验、AI与Anki集成测试
  README.md
```

## 构建与运行

### 运行环境要求

- Windows 10 / 11 (x64)
- .NET 10.0 SDK (或运行已编译产物无需独立安装 SDK)

### 编译构建

```powershell
# 编译 Release 版本
dotnet build desktop/WordCatcher.Desktop.sln -c Release

# 执行全部自动化测试
dotnet test desktop/WordCatcher.Desktop.sln -c Release
```

### 启动应用

```powershell
dotnet run --project desktop/src/WordCatcher.App/WordCatcher.App.csproj
```

应用启动后将常驻在右下角系统托盘，图标为应用默认图标。

## 离线词典安装

1. 右键托盘图标选择「设置」，或双击打开主窗口切换到「设置与词典」Tab。
2. 方式一（在线下载）：在「官方下载源 URL」输入框保留默认地址，点击「在线下载并安装」，应用将流式下载官方发布的 `distribution.sqlite.gz`，解压校验通过后原子替换就绪。
3. 方式二（本地文件）：点击「从本地工件安装 (.sqlite.gz)」，选择已下载的词典压缩包或 SQLite 文件完成导入。

## 已知限制与使用说明

1. **权限边界**：应用以普通用户权限运行。根据 Windows 安全机制（UIPI），普通权限程序无法向以“管理员身份运行”的高权限目标窗口发送模拟输入（Ctrl+C）。若在管理员程序中取词，请以管理员身份启动本应用或在目标程序手动复制。
2. **剪贴板恢复**：取词过程会进行剪贴板快照并在读取后尽力恢复。常见文本、HTML、RTF、图片与文件列表均可无损还原；某些应用程序私有的延迟渲染专有格式可能无法完全还原。
3. **敏感区域**：取词机制为用户主动按下 `Alt+Q` 触发，请勿在密码输入框或涉密场景触发取词。

## 数据备份

本地数据库位于：
`%LocalAppData%\WordCatcher\wordcatcher.db`
主窗口「生词库」页左下方提供「导出备份 (JSON)」按钮，可随时将全部词条及语境导出为可读的 JSON 归档文件。

## 数据署名与许可声明

- **Open Dictionary**：离线词典格式与衍生数据归属于 [ahpxex/open-dictionary](https://github.com/ahpxex/open-dictionary) 项目，遵循 [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/) 许可协议。
- **English Wiktionary**：词典源数据来自 English Wiktionary 及其贡献者。
- **Wiktextract**：数据提取与结构化解析基于 [Wiktextract](https://github.com/tatuylonen/wiktextract) 工具。
- **应用代码**：本桌面应用程序源代码与离线词典数据独立授权。
