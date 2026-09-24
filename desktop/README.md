# Word Catcher Windows 桌面版 MVP

Word Catcher 是一款专为个人深度阅读与语言积累设计的 Windows 桌面伴侣应用。在任意应用程序（记事本、VS Code、Edge/Chrome、Word、PDF 阅读器等）中选中文字，按下全局快捷键即可在光标旁展示词典与翻译卡片，并支持一键保存至本地词库与无缝后台离线同步至 Anki。

## 核心特性

快捷键可在「设置 → 取词快捷键」点击「录入快捷键」后直接按下组合键，无需手写名称。
支持 Ctrl / Alt / Shift / Win 配合字母、主键盘数字或 F1–F24；Esc 取消，Tab 离开，
切换页面或窗口失去焦点也会退出录入。「恢复默认」填入 Alt+Q，点击「保存所有设置」后生效。
新组合被占用时保留原快捷键，录入期间暂停取词触发，结束后恢复此前的启用状态。

- **全局快捷键屏幕取词**：默认 `Alt+Q` 快速获取选中文字，自动识别当前进程与窗口标题，取词完成后自动恢复原有剪贴板内容（支持文本、富文本、图片与文件列表）。
- **离线词典优先**：短词优先检索本地 [ahpxex/open-dictionary](https://github.com/ahpxex/open-dictionary) 词典（遵循 `distribution_entry_v5` 与 `distribution_sqlite_v1` 契约），无需网络连接，秒级响应。
- **智能词形还原**：内置基于原项目的 Lemmatizer 词形还原器，查询屈折变形（复数、时态、比较级、副词等）自动命中词元原形。
- **在线翻译回退**：未命中离线词典或选中文本为长句时，默认先调用设置中选择的免费机翻服务（Microsoft 或 Google）；该服务不可用时会尝试另一机翻服务，仍不可用时再回退到 OpenAI 兼容接口。也可以手动强制请求“AI 语境详解”。在线翻译会将选中文字发送给实际处理请求的服务；使用 AI 详解时还会发送可获取的句子上下文和来源窗口标题。
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

## 打包与 GitHub Release

仓库已配置 GitHub Actions。推送版本标签后，GitHub 会自动执行测试，并构建无需安装 .NET Runtime 的 Windows x64 单文件压缩包：

```powershell
git tag v0.2.1
git push origin v0.2.1
```

构建完成后，在 GitHub 的 `Releases` 页面下载 `WordCatcher-v0.2.1-win-x64.zip`，解压到固定目录并运行 `WordCatcher.App.exe` 即可。两台常见的 Intel/AMD Windows 电脑都使用 `win-x64` 包；如果目标设备是 Windows ARM，需要另行增加 `win-arm64` 构建。

发布包是自包含的，目标设备不需要安装 .NET SDK 或 .NET Runtime。首次运行后，离线词典可以在应用设置中下载安装。

每台设备都会在本机保存自己的设置和词库数据库：

`%LocalAppData%\WordCatcher\`

应用当前不会自动把本地 SQLite 词库同步到 GitHub 或云盘。推荐在两台设备上分别安装 Anki 和 AnkiConnect，并让它们连接同一个 Anki 账号；Word Catcher 保存的卡片会进入同一个 Anki 牌组，再由 Anki 负责跨设备同步。若只需要迁移一次本地数据，可在「生词库」页面导出 JSON 备份；当前版本的 JSON 主要用于备份，不能替代实时同步。

## 离线词典安装

### 查词卡片的展示规则

同一发布内容的 JSONL 与 SQLite 使用相同词条契约。卡片默认按词性显示简义，核心义
优先于常用义；记忆线索可以单独展开。点击「展开全部词性与义项」查看完整义项列表，
展开后直接阅读各义项的学习者解释、双语例句和用法提示。少见义不会在读取时被删除。
词典没有结构化义项时回退显示普通释义，只有少见义的词条也会提供可读摘要。

保存和复制使用覆盖各词性的释义摘要，不受卡片展开状态影响。已有收藏不会批量改写。
关于 JSONL / SQLite 的核验过程和 `digital` 示例，见
[查词差异与修复说明](../docs/dictionary-display-investigation.md)。

1. 右键托盘图标选择「设置」，或双击打开主窗口切换到「设置与词典」Tab。
2. 方式一（在线下载）：默认使用官方来源，展开「词典高级选项」可修改下载地址，点击「下载词典」，应用将流式下载官方发布的 `distribution.sqlite.gz`，解压校验通过后原子替换就绪。
3. 方式二（本地文件）：展开「词典高级选项」，点击「从文件安装」，选择已下载的词典压缩包或 SQLite 文件完成导入。

## 已知限制与使用说明

1. **权限边界**：应用以普通用户权限运行。根据 Windows 安全机制（UIPI），普通权限程序无法向以“管理员身份运行”的高权限目标窗口发送模拟输入（Ctrl+C）。若在管理员程序中取词，请以管理员身份启动本应用或在目标程序手动复制。
2. **剪贴板恢复**：取词过程会进行剪贴板快照并在读取后尽力恢复。常见文本、HTML、RTF、图片与文件列表均可无损还原；某些应用程序私有的延迟渲染专有格式可能无法完全还原。
3. **敏感区域**：取词机制为用户主动按下 `Alt+Q` 触发，请勿在密码输入框或涉密场景触发取词。

## 数据备份

### 词库浏览与整理

- 默认显示单词和一行简义，可勾选「紧凑列表」隐藏摘要；拖动中间分隔线调整列表与详情宽度。
- 详情先展示释义摘要，点击「完整释义」展开全文；语境记录按条折叠，展开后查看原句、译文和来源。
- 输入单词、释义或语境后自动搜索（300ms 防抖），`Ctrl+F` 聚焦搜索，搜索框内 `Esc` 清空。结果按最近更新排序，每批 100 条，点击「加载更多词条」继续浏览。
- 编辑时暂时锁定搜索和词条切换，避免丢失草稿；单词与释义不能为空，保存失败会保留修改内容。
- 从托盘重新打开词库会刷新收藏；正在编辑时保留当前草稿。

- 列表支持「全部词条／待同步／同步失败」筛选，筛选与语境搜索在分页前执行。
- 删除词条后可以在列表下方「撤销删除」，恢复全部语境和原有同步状态；本次运行中的多次删除可以依次撤销。删除的词条暂存在本地数据库中，不参与正常查询、导出和待处理同步；本地删除不会删除已写入 Anki 的笔记。

### 界面与交互

- 查词按钮显示「存入生词库 → 保存中 → 已收藏」，已收藏的单词可保存新语境；收藏、Anki 排队与复制／朗读反馈分别展示。
- 浮窗优先展示常用释义和原句／语境翻译，完整义项展开后直接展示解释、例句和用法；长句原文放在可滚动内容区。
- 主窗口查词结果在较窄区域自动改为单栏，没有语境或记忆线索时释义占满宽度。
- 设置页的词典来源、AI 连接参数和 Anki 高级选项默认折叠。底部显示未保存状态，切换页面或隐藏到托盘会保留当前设置草稿，点击「保存所有设置」后生效。

本地数据库位于：
`%LocalAppData%\WordCatcher\wordcatcher.db`
主窗口「生词库」页左下方提供「导出备份 (JSON)」按钮，可随时将全部词条及语境导出为可读的 JSON 归档文件。

## 数据署名与许可声明

- **Open Dictionary**：离线词典格式与衍生数据归属于 [ahpxex/open-dictionary](https://github.com/ahpxex/open-dictionary) 项目，遵循 [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/) 许可协议。
- **English Wiktionary**：词典源数据来自 English Wiktionary 及其贡献者。
- **Wiktextract**：数据提取与结构化解析基于 [Wiktextract](https://github.com/tatuylonen/wiktextract) 工具。
- **应用代码**：本桌面应用程序源代码与离线词典数据独立授权。
