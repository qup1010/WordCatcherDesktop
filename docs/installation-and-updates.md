# 安装、升级与插件分发

## Windows 桌面版

桌面安装与更新使用 [Velopack](https://docs.velopack.io/)，应用 SDK 和构建工具固定为 1.2.161。

- `WordCatcherDesktop-win-Setup.exe`：当前用户安装，无需管理员权限，创建开始菜单和桌面快捷方式。
- `WordCatcherDesktop-win-Portable.zip`：完整解压运行的便携包；保留包内的启动入口和更新器文件。
- `*-full.nupkg`、`releases.win.json` 等：GitHub 更新源，和对应版本的安装器一起发布。

程序安装目录为 `%LOCALAPPDATA%\WordCatcherDesktop`；个人数据保留在现有 `%LOCALAPPDATA%\WordCatcher`。两者使用不同目录，防止安装器或更新器替换程序文件时影响数据库、词典和配置。

应用不会在启动时或后台自动检查更新。用户在「设置 → 关于与更新」点击「检查更新」后，安装器版本才会检查并下载可用更新；检查状态只在此时显示。下载完成后点击「重启并安装更新」，应用会先确认当前编辑、保存、导出、词典安装和手动同步操作已经结束，再正常关闭并替换程序。下载支持取消，失败后可重新手动检查。便携版可从 GitHub Release 下载新版便携包。

GitHub 标签发布工作流会在 Windows runner 上生成 `WordCatcherDesktop-win-Setup.exe` 和 `WordCatcherDesktop-win-Portable.zip`，并将两者连同更新 feed 上传到 GitHub Release。首次发布 Velopack 更新源后，安装器版本可通过应用内手动检查获取后续更新。旧版独立便携 ZIP 仍需手动下载新版。

安装器目前未配置代码签名证书。Windows 实机安装、卸载及跨版本重启更新应在独立测试环境验收，再发布给其他用户。

## 高 DPI 与小屏幕

应用 manifest 显式启用 PerMonitorV2，在不同显示器缩放比例之间移动时由 WPF 重绘。主窗口在创建和 DPI 改变时按当前显示器的工作区限制窗口与最小尺寸。较窄窗口将导航收为图标栏，并让词条列表与详情按比例分配空间。正文采用 Display 文本格式，搜索框使用连续细边框显示焦点。

开发验收覆盖 100%、125%、150%、200% 缩放，并检查外接屏和笔记本屏之间移动窗口。离屏图像用于布局检查；真实多屏切换和笔记本显示效果需要在对应设备上验收。

参考：[Microsoft 的 DPI 感知声明](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process)。

## 浏览器插件

当前插件由 GitHub 发布 ZIP，通过 Chrome / Edge 的「加载已解压扩展」或 Firefox 临时加载来使用；插件保持手动更新，下载新版 ZIP、覆盖扩展目录并在浏览器扩展管理页重新加载。本项目不配置插件自动升级。

| 浏览器 | 手动更新方式 |
| --- | --- |
| Chrome / Edge 已解压目录 | 下载新版 ZIP、覆盖固定扩展目录，并在扩展管理页重新加载 |
| Firefox 临时加载 | 下载新版扩展包后重新加载；临时扩展会在浏览器关闭后失效 |

GitHub ZIP 不会自动替换浏览器中的扩展文件；更新由用户主动下载并覆盖。
