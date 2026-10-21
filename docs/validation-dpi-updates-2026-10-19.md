# 显示适配、安装器与更新验证（2026-10-19）

> 历史记录：本次记录验证的是当时的 Velopack 安装和更新实现。当前方案已切换为 Inno Setup，现行流程见 [安装、升级与插件分发说明](installation-and-updates.md)。

开发分支：`codex/dpi-installer-updates`，基于 `origin/main` 的 `2186c70`。

## 已验证

- Release 配置全套测试：192 项通过（Core 14、Infrastructure 71、App 107），编译无警告。
- 主窗口工作区约束覆盖 100%、125%、150%、200% 缩放；新增小屏浮窗尺寸检查。
- 原生 WPF 查词页在 125%、150%、200% 下离屏渲染；检查中英文编辑与全选行为。
- 主窗口 640 × 480 DIP 下导航收起、页面滚动与保存设置按钮可见；恢复宽窗口后侧栏正常展开。
- 发布后的 win-x64 主程序通过资源读取确认内嵌 `PerMonitorV2` manifest。
- Velopack CLI 验证启动钩子在 WPF 初始化之前执行，成功生成 win-x64 安装器、便携包、完整更新包和 feed。
- 使用 SDK 的 `TestVelopackLocator` 与本地发布源在全新隔离目录中，从模拟已安装 0.3.1 发现 0.3.2、下载和校验完整更新包，并进入待重启状态。未调用安装或重启 API。
- 自动更新相关测试覆盖：检查并下载、完成通知、下载后等待重启请求、未保存设置阻止重启、失败重试、取消下载、独立便携版回退下载页面，以及关闭自动更新后的初始设置状态。

## 本地产物

最终预览包位于 `artifacts/release-preview-0.3.2/`：

- `WordCatcherDesktop-win-Setup.exe`
- `WordCatcherDesktop-win-Portable.zip`
- `WordCatcherDesktop-0.3.2-full.nupkg`
- `releases.win.json`、`assets.win.json`、`RELEASES`

布局预览位于 `artifacts/validation/lookup-150.png` 和 `artifacts/validation/compact-settings.png`。这些是演示数据生成物，不提交进源码。

## 待实机验收

本地 0.3.2 为预览打包版本，尚未推送新 tag 或发布到 GitHub。未运行 Setup，未执行真实卸载或跨版本重启安装。安装器未配置代码签名；笔记本实际清晰度和多显示器移动需要在目标设备上检查。

浏览器端本轮完成更新渠道调查，未更改插件源码。解压目录依然采用覆盖文件后重新加载；商店分发和 Firefox 自托管更新方案见 [安装与升级说明](installation-and-updates.md)。
