# 产品展示图

- `showcase.png`：根 README 首屏主图（1600 × 940），展示生词库、划词浮标与完整查词浮窗联动。
- `feature-lookup.png`：轻形态浮窗与截屏 OCR 专题图（1440 × 700），展示极简译文、迷你卡片、划词浮标与 OCR 句中选词。
- `feature-review.png`：卡片复习与深色模式专题图（1440 × 700），展示原句目标词高亮复习与深色查词浮窗。
- `showcase.html` / `feature-lookup.html` / `feature-review.html`：三张展示图的版式源文件。
- `raw/`：由 `tools/ShowcaseCapture` 离屏渲染生成的 2× DPI 真实 WPF 界面素材（全部使用虚构演示数据，不访问个人配置、词库、剪贴板或网络服务）。

## 更新方式

在仓库根目录先重新生成真实 WPF 界面素材：

```powershell
dotnet run --project tools/ShowcaseCapture -c Release -- docs/images/raw
```

随后通过 Playwright CLI 渲染三张展示图（使用 Edge 时将 `--channel chrome` 改为 `--channel msedge`）：

```powershell
$base = Resolve-Path docs/images
npx playwright screenshot --channel chrome --viewport-size "1600,940" --color-scheme light --wait-for-selector "body[data-showcase-ready=true]" --timeout 20000 ([Uri](Join-Path $base "showcase.html")).AbsoluteUri docs/images/showcase.png
npx playwright screenshot --channel chrome --viewport-size "1440,700" --color-scheme light --wait-for-selector "body[data-showcase-ready=true]" --timeout 20000 ([Uri](Join-Path $base "feature-lookup.html")).AbsoluteUri docs/images/feature-lookup.png
npx playwright screenshot --channel chrome --viewport-size "1440,700" --color-scheme light --wait-for-selector "body[data-showcase-ready=true]" --timeout 20000 ([Uri](Join-Path $base "feature-review.html")).AbsoluteUri docs/images/feature-review.png
```

生成后需逐张检查标题、浮窗边缘、按钮、文字清晰度及各模块间距，确认无截断、遮挡或留白失衡后再提交。
