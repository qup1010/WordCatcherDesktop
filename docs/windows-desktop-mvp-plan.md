# Word Catcher Windows 桌面版 MVP 实施方案

> 用途：本文件是交给实现模型/开发者的执行规格。除非遇到无法继续的技术阻塞，否则不要扩展范围、替换技术栈或自行加入云同步、OCR、跨平台等功能。

## 1. 项目结论

做一个仅供个人使用的 Windows 桌面伴侣应用，而不是替代现有浏览器扩展。

核心交互：

```text
在任意应用选中文字
  → 按全局快捷键 Alt+Q
  → 程序复制并读取选区
  → 短词优先查询 Open Dictionary 离线词典
  → 未命中或长文本再调用在线翻译
  → 鼠标附近展示词典/翻译卡片
  → 用户保存
  → 先写入本地 SQLite 词库
  → 后台尝试同步 Anki
  → Anki 离线时保留待同步任务
```

浏览器扩展暂时保持独立。桌面版 MVP 不做扩展通信，但数据模型要为以后通过 Native Messaging 共享本地词库留出空间。

## 2. 目标与验收结果

MVP 完成后应满足：

1. 应用启动后常驻系统托盘，不显示多余主窗口。
2. 在记事本、Word、常见 PDF 阅读器、微信和 VS Code 中选中文字后，按 `Alt+Q` 可以获取文字。
3. 获取成功后，在当前鼠标附近显示翻译卡片。
4. 英文单词优先使用 `ahpxex/open-dictionary` 离线查询，不调用在线 API；未命中或选中长文本时回退在线翻译。
5. 支持下载或从本地文件安装官方 `distribution.sqlite.gz` / `distribution.sqlite` 词典工件。
6. 卡片可以保存、关闭、请求 AI 详解；`Enter` 保存，`Esc` 关闭。
7. 点击保存后，即使 Anki 没启动，单词也立即出现在桌面词库中，并显示“待同步”。
8. Anki 在线时自动写入；离线任务可以在主窗口手动重试。
9. 应用重启后，词库、词典安装状态和待同步任务仍存在。
10. 可以搜索、编辑、删除词条，并导出 JSON 备份。
11. 不需要管理员权限，不持续监听键盘内容，不上传使用日志。

## 3. 明确不做的内容

以下内容不属于 MVP：

- macOS、Linux 或移动端。
- 鼠标松开后自动弹窗。
- OCR、截图翻译和屏幕持续识别。
- 云同步、登录、账号和多设备同步。
- 浏览器扩展与桌面端共享数据库。
- App Store / Microsoft Store 上架、安装包签名和自动更新。
- 自动获取任意软件中的完整段落上下文。
- 背单词、间隔重复或测验系统。
- 多人使用、团队词库和遥测平台。
- 任意插件系统。
- 自行生成、修改或重新分发 Open Dictionary 数据。

实现模型不得因为“以后可能需要”而提前实现这些功能。

## 4. 固定技术栈

### 4.1 基础技术

- .NET 10 LTS。
- C# 14。
- WPF，目标 `net10.0-windows`。
- x64 优先；开发阶段不要求 ARM64。
- MVVM：`CommunityToolkit.Mvvm`。
- SQLite：`Microsoft.Data.Sqlite`，直接 SQL，不引入 Entity Framework。
- JSON：`System.Text.Json`。
- HTTP：单例 `HttpClient`，通过依赖注入提供。
- 托盘图标：复用 `System.Windows.Forms.NotifyIcon`，项目启用 `UseWindowsForms`。
- 日志：`Microsoft.Extensions.Logging`；写入本地滚动文件可以自行实现轻量 provider，禁止把日志发送到网络。
- 测试：xUnit。

.NET 10 是当前 LTS，支持到 2028 年 11 月；WPF 仅运行于 Windows，符合本项目边界：

- <https://dotnet.microsoft.com/platform/support/policy>
- <https://learn.microsoft.com/dotnet/desktop/wpf/whats-new/net100>

### 4.2 不采用

- 不采用 Electron、Tauri、WinUI 3 或 MAUI。
- 不采用 WebView 承载 React 页面。
- 不采用数据库 ORM。
- 不在 MVP 中启动本地 HTTP Server。

## 5. Solution 与目录结构

以下是初始 MVP 的历史目录规划。桌面版现在维护为独立仓库，2026-10-02 已将源码、测试和解决方案移到根目录；当前结构与命令以 [开发说明](development.md) 为准。

原始规划是在仓库根目录新增 `desktop/`，不改造现有浏览器扩展目录：

```text
desktop/
  WordCatcher.Desktop.sln
  src/
    WordCatcher.App/
      App.xaml
      App.xaml.cs
      Windows/
      Views/
      ViewModels/
      Controls/
      Themes/
      Interop/
    WordCatcher.Core/
      Models/
      Enums/
      Interfaces/
      UseCases/
    WordCatcher.Infrastructure/
      Database/
      Dictionary/
      Translation/
      Anki/
      Settings/
      Security/
      Logging/
  tests/
    WordCatcher.Core.Tests/
    WordCatcher.Infrastructure.Tests/
  README.md
```

依赖方向必须保持：

```text
WordCatcher.App → Core + Infrastructure
Infrastructure → Core
Core → 不依赖 App 或 Infrastructure
```

## 6. 核心领域模型

### 6.1 枚举

```csharp
enum SyncStatus
{
    Pending,
    Syncing,
    Synced,
    Retryable,
    Failed
}

enum TranslationStatus
{
    Idle,
    Translating,
    Succeeded,
    Failed
}

enum LookupSource
{
    OpenDictionary,
    Ai
}
```

### 6.2 主要模型

```csharp
sealed record CaptureResult(
    string SelectedText,
    string SourceProcess,
    string SourceWindowTitle,
    Point CursorPosition,
    DateTimeOffset CapturedAt);

sealed record TranslationResult(
    string Word,
    string Reading,
    string PartOfSpeech,
    string Definition,
    string ContextTranslation,
    string? MemoryHook,
    LookupSource Source,
    string? SourceEntryId,
    string? SourceSchemaVersion);

sealed record SaveCardCommand(
    CaptureResult Capture,
    TranslationResult Translation);
```

关键约束：

- 本地保存成功与 Anki 同步成功是两个独立事实。
- UI 不允许用一个 `bool SavedToAnki` 同时表达本地保存和同步状态。
- 同一个标准词形只有一条 `words` 记录，但可以有多条 `occurrences` 语境记录。
- 重新保存同一个词时新增语境，并用最新非空释义更新词条。

## 7. SQLite 数据设计

数据库位置：

```text
%LocalAppData%\WordCatcher\wordcatcher.db
```

启动时自动建库并执行按版本编号排列的 SQL migration。不要依赖 `EnsureCreated` 一类不可演进方案。

### 7.1 `words`

```sql
CREATE TABLE words (
  id                TEXT PRIMARY KEY,
  normalized_word   TEXT NOT NULL,
  display_word      TEXT NOT NULL,
  language          TEXT NOT NULL DEFAULT '',
  reading           TEXT NOT NULL DEFAULT '',
  part_of_speech    TEXT NOT NULL DEFAULT '',
  definition        TEXT NOT NULL,
  memory_hook       TEXT NOT NULL DEFAULT '',
  created_at_utc    TEXT NOT NULL,
  updated_at_utc    TEXT NOT NULL
);

CREATE UNIQUE INDEX ux_words_identity
ON words(language, normalized_word);
```

`normalized_word` MVP 规则：`Trim()` 后使用 `ToLowerInvariant()`。不要在 MVP 中实现词形还原器。

### 7.2 `occurrences`

```sql
CREATE TABLE occurrences (
  id                   TEXT PRIMARY KEY,
  word_id              TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
  selected_text        TEXT NOT NULL,
  sentence             TEXT NOT NULL DEFAULT '',
  context_translation  TEXT NOT NULL DEFAULT '',
  source_process       TEXT NOT NULL DEFAULT '',
  source_window_title  TEXT NOT NULL DEFAULT '',
  source_uri           TEXT NOT NULL DEFAULT '',
  lookup_source        TEXT NOT NULL DEFAULT '',
  source_entry_id      TEXT NOT NULL DEFAULT '',
  source_schema_version TEXT NOT NULL DEFAULT '',
  captured_at_utc      TEXT NOT NULL
);

CREATE INDEX ix_occurrences_word_id ON occurrences(word_id);
```

MVP 无法可靠获得完整句子时，`sentence` 使用选中文字或空字符串，不伪造上下文。

### 7.3 `sync_jobs`

```sql
CREATE TABLE sync_jobs (
  id                  TEXT PRIMARY KEY,
  word_id             TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
  occurrence_id       TEXT NOT NULL REFERENCES occurrences(id) ON DELETE CASCADE,
  target              TEXT NOT NULL DEFAULT 'anki',
  status              TEXT NOT NULL,
  attempts            INTEGER NOT NULL DEFAULT 0,
  last_error          TEXT NOT NULL DEFAULT '',
  next_attempt_at_utc TEXT NULL,
  created_at_utc      TEXT NOT NULL,
  updated_at_utc      TEXT NOT NULL
);

CREATE INDEX ix_sync_jobs_status ON sync_jobs(status, next_attempt_at_utc);
```

每次保存必须在一个数据库事务中完成：

1. upsert `words`；
2. insert `occurrences`；
3. insert `sync_jobs(status = 'Pending')`；
4. commit；
5. commit 成功后才允许后台尝试 Anki。

### 7.4 `app_meta`

用于保存 schema 版本：

```sql
CREATE TABLE app_meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
```

所有 SQL 命令必须参数化。连接启动时执行：

```sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA busy_timeout = 5000;
```

## 8. Open Dictionary 离线词典

### 8.1 兼容目标

MVP 必须兼容 [ahpxex/open-dictionary](https://github.com/ahpxex/open-dictionary) 当前正式发布格式：

- 产品内容契约：`distribution_entry_v5`；
- SQLite 包装契约：`distribution_sqlite_v1`；
- 首选工件：`distribution.sqlite.gz`；
- 也允许用户选择已经解压的 `distribution.sqlite`；
- 默认下载地址：`https://github.com/ahpxex/open-dictionary/releases/latest/download/distribution.sqlite.gz`。

官方 SQLite 已经包含完整产品行、查询索引和结构化子表，因此桌面端不要把约 8.4 万条词典数据再次导入用户词库数据库。应用数据库与词典数据库必须分离：

```text
%LocalAppData%\WordCatcher\wordcatcher.db
%LocalAppData%\WordCatcher\dictionary\distribution.sqlite
%LocalAppData%\WordCatcher\dictionary\dictionary-meta.json
```

其中：

- `wordcatcher.db` 可读写，保存用户自己的词条、语境和同步任务；
- `distribution.sqlite` 只读打开，整个文件可原子替换；
- 不在官方词典文件中创建表、索引或写入用户状态；
- 不将词典 DB attach 到长期持有的用户 DB 连接。

官方契约资料：

- <https://github.com/ahpxex/open-dictionary>
- <https://github.com/ahpxex/open-dictionary/blob/main/docs/export_contracts.md>

### 8.2 安装与更新

实现 `IDictionaryInstaller`：

```csharp
interface IDictionaryInstaller
{
    Task InstallFromUrlAsync(Uri url, IProgress<DictionaryInstallProgress> progress, CancellationToken ct);
    Task InstallFromFileAsync(string path, IProgress<DictionaryInstallProgress> progress, CancellationToken ct);
    Task RemoveAsync(CancellationToken ct);
}
```

安装流程：

1. 下载到词典目录下的随机 `.download` 临时文件，不把完整压缩包读进内存。
2. 若为 `.gz`，使用 `GZipStream` 流式解压到随机 `.sqlite.tmp`。
3. 打开临时 SQLite，执行 `PRAGMA query_only = ON` 和 `PRAGMA integrity_check`。
4. 校验必需表：`metadata`、`entries`、`pos_groups`、`meanings`、`meaning_examples`。
5. 读取并 JSON 反序列化 `metadata.value_json`；校验 `distribution_schema_version` 为受支持的 `distribution_entry_v5`，`sqlite_schema_version` 为 `distribution_sqlite_v1`。不要直接把带 JSON 引号的 `value_json` 与裸字符串比较。
6. 校验 `entries` 至少有一条记录，并读取 definition/headword language 元数据。
7. 校验通过后关闭所有连接，使用 `File.Move(..., overwrite: true)` 在同一文件系统内原子替换正式文件。
8. 更新 `dictionary-meta.json`，记录安装时间、词条数、契约版本、定义语言、源 URL 和可用的 SHA256。
9. 取消、下载失败、解压失败或校验失败时删除临时文件，旧词典继续可用。

实现要求：

- 显示下载字节数、解压阶段和校验阶段；没有 Content-Length 时显示不确定进度。
- 下载允许取消。
- 设置页提供“安装/更新”“从本地文件安装”“删除词典”。
- 删除前二次确认；删除词典不删除用户已经收藏的词。
- 应用运行中更新词典时，先释放旧的只读连接，再原子替换，再建立新连接。
- MVP 不要求自动静默更新；由用户手动点击更新。

### 8.3 查询服务

实现：

```csharp
interface IOfflineDictionaryService
{
    bool IsInstalled { get; }
    Task<OfflineDictionaryEntry?> LookupAsync(string text, CancellationToken ct);
}
```

查询顺序：

1. `Trim()` 输入；空字符串直接返回未命中。
2. 只对不超过 2 个空白分隔 token 的输入走离线词典，长文本直接交给在线翻译。
3. 生成候选词：原文小写结果在最前，然后使用从现有 `lib/open-dict/lemmatizer.ts` 移植并测试的词形还原候选。
4. 对候选按顺序执行参数化查询：

```sql
SELECT document_json
FROM entries
WHERE headword_language_code = $language
  AND normalized_headword = $word
LIMIT 1;
```

5. 英文查询的 `$language` 使用工件实际的 headword language code；不要硬编码一个未经元数据确认的值。
6. 命中后反序列化 `document_json` 为完整 `distribution_entry_v5` C# 模型。

不要只依赖本项目浏览器插件当前的精简 TypeScript 类型。正式 v5 还包含：

- `schema_version`、`entry_id`、`normalized_headword`；
- `headword_language` 与 `definition_language`；
- `headword_summary`、`study_notes`、`etymology_note`、`etymologies`；
- 每个词性组的 `proper_name`、forms、pronunciations、relations；
- 每个义项的 `priority`、`learner_explanation`、`usage_note`、双语 examples。

解析模型对未来新增字段保持宽容，但上述 MVP 使用的字段类型不正确时应报告“词典版本不兼容”，不能静默展示错误数据。

### 8.4 展示与折叠规则

必须遵守 Open Dictionary 的产品契约：

- `learner_explanation` 是主要释义；`short_gloss` 只是快速扫描辅助字段。
- 义项顺序保持工件中的顺序。
- 默认展示 `core` 和 `common`，折叠 `rare`。
- 如果过滤后一个义项都没有，必须回退展示全部 `rare` 义项；不能把生僻词显示成空结果。
- 发音优先展示 US，其次 UK，再次任一可用 IPA；保留 US/UK 标签。
- 显示词性组 summary、usage note、双语例句和 memory hook。
- 完整词条可以展开；快捷卡默认只展示前几个主要义项。

将命中结果映射为可保存的 `TranslationResult` 时：

- `Word` = `headword`；
- `Reading` = 按上述规则选出的主要 IPA；
- `PartOfSpeech` = 第一个有可显示义项的词性；
- `Definition` = 按词性组合前 3～5 个主要 `learner_explanation`，不可优先用短 gloss 替代；
- `MemoryHook` = `memory_hook`；
- `ContextTranslation` = 选中内容只有一个单词时允许为空；
- `Source` = `LookupSource.OpenDictionary`。
- `SourceEntryId` = `entry_id`；
- `SourceSchemaVersion` = `schema_version`。

### 8.5 查词路由

引入 `ILookupService` 统一编排，不要让 ViewModel 自己决定调用顺序：

```csharp
interface ILookupService
{
    Task<TranslationResult> LookupAsync(
        CaptureResult capture,
        bool forceAi,
        CancellationToken ct);
}
```

规则：

```text
词典已安装 + 短词 + 非 forceAi
  ├─ 命中 → 立即返回 Open Dictionary 结果，不调用 API
  └─ 未命中 → 调用在线翻译

长文本 / 词典未安装 / forceAi
  └─ 调用在线翻译
```

离线命中卡片提供“AI 语境详解”按钮，它以 `forceAi = true` 重新查询。AI 结果替换当前预览，但不能自动覆盖已经保存的词条。

### 8.6 许可与署名

Open Dictionary 的数据工件依据 CC BY-SA 4.0 发布，是 Wiktionary 的衍生数据。即使当前只是个人使用，也要在 README 和应用“关于”页保留：

- Open Dictionary 项目链接；
- English Wiktionary 及贡献者署名；
- Wiktextract 项目链接；
- CC BY-SA 4.0 许可证链接；
- 数据与应用代码采用不同许可证的说明。

许可原文：<https://github.com/ahpxex/open-dictionary/blob/main/LICENSE-DATA.md>

不得把下载的词典数据提交进本仓库。

## 9. 设置与密钥

普通设置保存到：

```text
%LocalAppData%\WordCatcher\settings.json
```

至少包含：

```json
{
  "hotkey": "Alt+Q",
  "explainLanguage": "简体中文",
  "translation": {
    "baseUrl": "https://api.openai.com/v1",
    "model": "",
    "timeoutSeconds": 30
  },
  "dictionary": {
    "enabled": true,
    "downloadUrl": "https://github.com/ahpxex/open-dictionary/releases/latest/download/distribution.sqlite.gz"
  },
  "anki": {
    "enabled": true,
    "url": "http://127.0.0.1:8765",
    "deckName": "Word Catcher",
    "noteTypeName": "Word Catcher"
  },
  "ui": {
    "closePopupAfterSave": true
  }
}
```

API Key 不得明文写入 JSON。使用 Windows DPAPI（`ProtectedData`，CurrentUser 范围）加密后写入单独的 `secrets.dat`。日志中禁止输出 API Key、Authorization header 或完整翻译请求体。

## 10. 全局快捷键与取词实现

### 10.1 快捷键注册

使用 Win32 `RegisterHotKey` / `UnregisterHotKey`：

- 默认 `Alt+Q`。
- 使用 `MOD_NOREPEAT` 防止按住按键连续触发。
- 通过隐藏窗口或主窗口 `HwndSource.AddHook` 处理 `WM_HOTKEY`。
- 注册失败时明确提示快捷键被占用，并允许用户修改。
- 应用退出和快捷键修改时必须注销旧快捷键。

### 10.2 触发时需要先采集的信息

模拟复制前先记录：

- `GetForegroundWindow()` 返回的源窗口句柄；
- 窗口标题；
- 进程名；
- `GetCursorPos()` 鼠标屏幕坐标；
- `GetClipboardSequenceNumber()`；
- 当前剪贴板快照。

不要等翻译完成后才读取源窗口，此时前台窗口可能已经变成自己的弹窗。

### 10.3 剪贴板复制流程

实现一个 `ISelectionCaptureService`，同一时间只允许一个捕获任务。建议流程：

1. 使用 `SemaphoreSlim(1, 1)` 防止重复触发。
2. 等待 Alt、Ctrl、Shift、Win 修饰键释放，最长 500ms。
3. 备份常见剪贴板格式：UnicodeText、Text、HTML、RTF、Bitmap、FileDrop。
4. 使用 Win32 `SendInput` 发送一次 Ctrl+C 的 key down / key up 序列。
5. 每 25ms 检查一次 `GetClipboardSequenceNumber()`，最长等待 1000ms。
6. 序号变化后在 STA UI 线程读取 Unicode 文本。
7. 文本 `Trim()`，拒绝空文本、纯空白和超过 5000 字符的内容。
8. 读取完成后恢复剪贴板快照。
9. 无论成功、超时还是异常，都在 `finally` 中尽力恢复剪贴板。

剪贴板被其他程序占用时最多进行 5 次短退避重试。禁止无限循环。

已知限制必须写进 README：自定义剪贴板格式和延迟渲染格式只能尽力恢复；MVP 主要保证文本、富文本、图片和文件列表。

### 10.4 权限边界

- 应用默认以普通用户权限运行。
- 普通权限程序通常不能向以管理员权限运行的目标程序可靠发送输入；遇到这种情况给出说明，不要求用户长期以管理员运行本应用。
- 不安装低级键盘钩子，不记录按键内容。
- 全局快捷键仅接收明确注册的组合键。
- 不对密码框做自动操作；由于 MVP 是用户主动触发，应在说明中提醒不要在敏感字段使用。

### 10.5 UI Automation 的位置

UI Automation 不作为第一版阻塞项。完成核心 MVP 后，可增加一个非必需增强：

1. 先尝试读取当前焦点元素的 Selection/Text Pattern；
2. 成功则直接获得选中文字和可用的选区范围；
3. 失败则退回 Ctrl+C 捕获；
4. 该增强不得改变 `ISelectionCaptureService` 对外接口。

## 11. 在线翻译服务

### 11.1 接口

```csharp
interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(
        CaptureResult capture,
        CancellationToken cancellationToken);
}
```

在线翻译部分只实现一个 OpenAI-compatible provider；短词查词由第 8 节的离线词典优先处理：

- 用户配置 Base URL、API Key、模型名和释义语言。
- 使用 `/chat/completions`；如果目标端点已经确定支持 `/responses`，也不要在 MVP 同时维护两套协议。
- 要求模型仅返回 JSON，不把 Markdown 直接展示为词条。
- 使用 `System.Text.Json` 严格反序列化并校验必要字段。
- 最多重试一次临时网络错误；401、403、400 不重试。
- 每次请求有明确超时并支持取消。

结构化输出目标：

```json
{
  "word": "词条原形",
  "reading": "读音，可为空",
  "partOfSpeech": "词性，可为空",
  "definition": "当前语境下的简明释义",
  "contextTranslation": "选中文本或句子的翻译",
  "memoryHook": "可选记忆提示"
}
```

如果选中的是长句而不是单词：

- `word` 保留原选中文本的简短形式或原文；
- `definition` 使用整体翻译；
- 不强制生成读音和词性。

失败时保留弹窗和原文，显示可重试错误，不自动保存不完整结果。

### 11.2 与现有插件的关系

可以参考现有：

- `lib/types.ts` 的词条字段；
- `lib/ai.ts` 的提示词和错误分类；
- `lib/anki.ts` 的字段名、HTML 转义和卡片模板。
- `lib/open-dict/types.ts`、`db.ts` 的快速展示思路；
- `lib/open-dict/lemmatizer.ts` 的候选词生成规则及测试。

不要从 WPF 项目直接运行 TypeScript，也不要通过 Node 子进程调用现有代码。应把必要规则移植为 C#，并用测试保证输出一致。

## 12. 本地优先保存与 Anki 同步

### 12.1 保存语义

点击“保存”时：

1. 禁用保存按钮，防止重复提交；
2. 执行 SQLite 事务；
3. 事务成功后 UI 立即显示“已保存，等待同步”或“已保存”；
4. 将同步任务交给后台 `AnkiSyncWorker`；
5. 不等待 Anki 响应才确认本地保存。

SQLite 写入失败才算保存失败。Anki 离线绝不能让保存失败。

### 12.2 AnkiConnect 客户端

实现 `IAnkiClient`：

```csharp
interface IAnkiClient
{
    Task<int> CheckVersionAsync(CancellationToken ct);
    Task EnsureDeckAndModelAsync(CancellationToken ct);
    Task<long> AddNoteAsync(Word word, Occurrence occurrence, CancellationToken ct);
}
```

协议：向配置的本地 URL POST：

```json
{
  "action": "version",
  "version": 6,
  "params": {}
}
```

要求：

- 默认地址 `http://127.0.0.1:8765`。
- 单次调用超时 8 秒。
- 连接拒绝、超时、连接重置归类为 `Retryable`。
- 模板字段不匹配、请求参数错误归类为 `Failed`。
- duplicate 视为已经同步，任务设为 `Synced`，避免永久重试。
- 所有文本进入 Anki 前进行 HTML 转义。
- Source 字段只写明确获得的来源，不伪造 URL。

卡片字段和笔记类型优先与现有插件保持一致：

```text
Word
Reading
PartOfSpeech
Definition
MemoryHook
Sentence
SentencePlain
SentenceTranslation
Source
```

可以直接移植 `lib/anki.ts` 中的 `ANKI_FIELDS`、模板、CSS 和字段构造规则。MVP 至少确保牌组、Note Type 和一个可复习模板存在；四模式模板可以移植，但不得因此阻塞本地词库功能。

### 12.3 后台同步器

使用应用内单实例后台 worker，不要为每个任务启动线程：

- 应用启动后扫描 `Pending` 和到期的 `Retryable`。
- 新保存任务通过 `Channel<Guid>` 或信号量唤醒 worker。
- 每次只同步一条，避免并发调用单线程的 AnkiConnect。
- 成功：`Synced`，清空错误。
- 可重试错误：`Retryable`，`attempts + 1`。
- 确定性错误：`Failed`，不自动重试。
- 自动重试间隔：30 秒、2 分钟、10 分钟，之后停止自动重试但保留手动重试。
- 应用退出时取消 worker，不阻塞退出超过 2 秒。

手动“全部重试”必须把选中的 `Failed/Retryable` 任务重新设为 `Pending` 并唤醒 worker。

## 13. UI 规格

### 13.1 翻译弹窗 `LookupWindow`

特性：

- 无系统标题栏、圆角、适度阴影、Topmost。
- 宽度约 420 DIP，最大高度不超过当前工作区的 70%。
- 出现在捕获时鼠标右下方；越界时向左或向上翻转。
- 使用 WPF DIP 与屏幕物理像素转换，必须在不同 DPI 显示器间正确定位。
- 捕获完成后允许激活窗口，因为原选区内容已经保存。
- 查离线词典时应立即完成或显示极短 loading；调用 AI 时显示明确的在线生成状态。
- 错误时显示简短错误、重试和关闭。
- 离线词典成功时显示来源标识、词、US/UK 发音、词性、按优先级折叠的释义、例句、用法说明和记忆提示。
- 在线翻译成功时显示词、读音、词性、释义、句子翻译、记忆提示。
- 保存后显示本地状态和 Anki 状态。

快捷键：

- `Enter`：保存；保存完成后按设置决定是否关闭。
- `Esc`：关闭。
- `Ctrl+Enter`：强制使用 AI 语境详解。

### 13.2 主窗口 `LibraryWindow`

MVP 使用单窗口三页或三个 Tab：

1. **词库**：搜索、按更新时间排序、查看释义和最近语境、编辑、删除。
2. **待同步**：显示 Pending/Retryable/Failed，展示最后错误，单条或全部重试。
3. **设置**：快捷键、Open Dictionary 安装/更新/删除与状态、API 配置、释义语言、Anki 配置和连接测试。

删除词条时依赖外键级联删除 occurrences 和 sync jobs。必须二次确认。

### 13.3 托盘菜单

至少包含：

- 打开词库
- 立即取词
- 重试 Anki 待同步任务
- 暂停/启用快捷键
- 设置
- 退出

关闭主窗口只隐藏到托盘；托盘“退出”才真正结束进程。

## 14. 应用生命周期

- 使用命名 `Mutex` 保证单实例。
- 第二次启动时 MVP 可以直接退出；如果容易实现，可通知已有实例打开主窗口，但这不是阻塞项。
- 启动顺序：创建目录 → 加载设置 → 建用户库/migration → 验证已安装词典 → 创建 DI 容器 → 启动 worker → 注册快捷键 → 创建托盘图标。
- 任一步失败都写日志并显示可理解的错误；数据库 migration 失败时不得继续运行并破坏数据库。
- 不默认开机启动。设置开机启动属于 MVP 后的小功能。

## 15. 错误分类与用户文案

不要把所有异常都显示为“操作失败”。至少区分：

- 未获取到选中文字。
- 目标程序权限高于当前应用，无法复制。
- 剪贴板正忙，请重试。
- 离线词典尚未安装。
- 词典下载、解压或完整性校验失败（旧版本继续可用）。
- 词典契约版本不受支持。
- 翻译服务未配置。
- 翻译服务鉴权失败。
- 翻译服务超时/网络不可达。
- 本地词库写入失败。
- Anki 未启动或 AnkiConnect 不可达（待同步，不是保存失败）。
- Anki 模板或字段配置错误（同步失败，可在待同步页查看）。

日志记录技术细节，UI 显示简短信息。日志中的选中文字默认只记录长度和哈希，不记录全文。

## 16. 测试要求

### 16.1 单元测试

至少覆盖：

- 单词标准化和去重。
- 保存事务同时写入三张表。
- 同一个词保存多个语境不会创建重复 word。
- `distribution_entry_v5` 完整模型反序列化。
- core/common/rare 折叠，以及全 rare 时回退展示全部义项。
- US/UK/任意发音选择顺序。
- 词形还原候选顺序与现有插件测试一致。
- 离线命中不调用在线翻译，未命中和 forceAi 会调用。
- Anki 离线时本地保存成功且任务为 Retryable/Pending。
- duplicate 被标记为 Synced。
- 确定性 Anki 错误被标记为 Failed。
- 重试退避计算。
- HTML 转义和 Anki 字段构造。
- JSON 翻译结果校验。
- 设置读写和损坏文件回退。

### 16.2 集成测试

使用临时 SQLite 文件和假的 `HttpMessageHandler`：

- 完整执行保存 → 同步成功。
- 保存 → Anki 离线 → 重启服务 → 手动重试 → 成功。
- 翻译 API 返回非法 JSON。
- 数据库 migration 从空库到当前版本。
- 使用小型 fixture SQLite 验证按 `normalized_headword` 查询和 `document_json` 解析。
- 模拟 `.sqlite.gz` 安装、完整性校验、原子替换和取消后保留旧词典。

### 16.3 手工兼容性矩阵

在以下应用至少各验证三次：

| 应用 | 获取短词 | 获取句子 | 剪贴板恢复 | 弹窗位置 |
|---|---:|---:|---:|---:|
| Windows 记事本 | 必须 | 必须 | 必须 | 必须 |
| Chrome/Edge | 必须 | 必须 | 必须 | 必须 |
| Word | 必须 | 必须 | 必须 | 必须 |
| 常用 PDF 阅读器 | 必须 | 必须 | 必须 | 必须 |
| 微信 | 尽力 | 尽力 | 必须 | 必须 |
| VS Code | 必须 | 必须 | 必须 | 必须 |

自动化测试不能替代此矩阵，因为跨应用复制是 MVP 最大的不确定性。

## 17. 实施阶段与交付顺序

实现模型必须按下面顺序工作，每阶段保持可构建、可测试。

### 阶段 1：工程骨架

- 创建 solution 和三个生产项目、两个测试项目。
- 配置依赖注入、日志、数据目录和单实例。
- 创建空主窗口和托盘菜单。
- 验证 `dotnet build`、`dotnet test`。

### 阶段 2：用户数据库与本地词库

- migration runner。
- repository 和保存事务。
- 词库列表、搜索、编辑、删除。
- JSON 导出。
- 完成数据库单元/集成测试。

### 阶段 3：Open Dictionary 离线词典

- 实现完整 `distribution_entry_v5` 模型。
- 实现 `.sqlite.gz` 下载、本地安装、流式解压、校验和原子替换。
- 实现只读查询、词形还原、义项折叠与展示映射。
- 设置页完成安装、更新、删除和进度展示。
- 加入 CC BY-SA 署名。

### 阶段 4：全局快捷键和选区捕获

- `RegisterHotKey`。
- 剪贴板快照、`SendInput`、超时和恢复。
- 来源窗口、进程和鼠标坐标采集。
- 临时结果弹窗先只显示原文。
- 完成手工兼容性矩阵第一轮。

### 阶段 5：查词路由与在线翻译

- 设置页 API 配置和 DPAPI 密钥存储。
- OpenAI-compatible 请求、严格 JSON 解析和错误分类。
- 实现离线优先、未命中回退和强制 AI 的 `ILookupService`。
- 翻译弹窗完整状态。

### 阶段 6：Anki 离线队列

- AnkiConnect 客户端。
- 模型/牌组确保逻辑。
- 后台同步 worker、状态和重试。
- 待同步页面。
- 验证 Anki 关闭、启动和中途退出三种情况。

### 阶段 7：收尾

- 多显示器/DPI 定位。
- 快捷键冲突提示。
- 错误文案和日志脱敏。
- README：安装、配置、已知限制、数据备份。
- 完整构建、测试和手工矩阵。

## 18. Definition of Done

只有同时满足以下条件才能宣布 MVP 完成：

- `dotnet build -c Release` 成功且无编译错误。
- `dotnet test -c Release` 全部通过。
- 普通用户权限下可以运行。
- 可以从官方 release URL 安装 `distribution.sqlite.gz`，应用重启后仍可离线查询。
- 离线查询常见原形和至少一组词形变化能命中正确词条。
- 一个只有 rare 义项的 fixture 仍能显示释义。
- 离线命中时断网也能出结果，且不会调用 AI API。
- 离线未命中时会按配置回退在线翻译。
- 关闭 Anki 后保存 3 个词，主窗口立即可见且均为待同步。
- 重启应用后 3 个词仍存在。
- 启动 Anki 并手动重试后任务变为已同步。
- Anki 中没有因重复重试产生的重复卡片。
- 原剪贴板为文本、图片、文件列表时，完成一次取词后仍可正常粘贴原内容。
- Chrome、Word、PDF 阅读器、VS Code 的短词取词通过。
- 未配置 API、API 401、API 超时、Anki 离线都有明确且不同的反馈。
- 没有 API Key 或 Authorization header 出现在日志中。
- README 清楚列出已知限制。
- README 和“关于”页包含 Open Dictionary、Wiktionary、Wiktextract 与 CC BY-SA 4.0 署名。

## 19. 后续路线（不在本次实现）

MVP 稳定后按价值排序考虑：

1. UI Automation 优先取词和上下文，剪贴板作为回退。
2. 浏览器扩展通过 Native Messaging 把卡片交给桌面词库。
3. 支持导入 `distribution.jsonl.gz`，供复用现有插件已经下载的 JSONL 工件。
4. OCR 截图翻译。
5. 自动开机启动和轻量自动更新。

不要在 MVP 阶段提前实现这些项目。
