# 桌面版展示图

- `showcase.png`：根 README 使用的 1600 × 1000 展示图。
- `showcase.html`：版式源文件，使用本目录两张真实 WPF 界面图。
- `showcase-library.png` / `showcase-lookup.png`：真实生词库和查词浮窗的离屏渲染，使用虚构演示数据。

所有文字和数据均用于展示，不加载个人词库、密钥或配置，不发起翻译或 Anki 请求，也不读取剪贴板。展示图未改变应用本身的界面。

## 更新方式

在仓库根目录执行：

```powershell
dotnet run --project tools/ShowcaseCapture -c Release -- docs/images
```

然后用 Playwright CLI 渲染版式。需要可用的 Playwright 与本机 Chrome；使用 Edge 时将 `--channel chrome` 改为 `--channel msedge`。

```powershell
$showcasePage = ([Uri](Resolve-Path docs/images/showcase.html).Path).AbsoluteUri
npx playwright screenshot --channel chrome --viewport-size "1600,1000" --color-scheme light --wait-for-selector "body[data-showcase-ready=true]" --timeout 20000 $showcasePage docs/images/showcase.png
```

图像加载和字体就绪后，页面才设置 `data-showcase-ready`。生成后检查标题、正文、浮窗、窗口边缘和底部文案，避免截到展开动画中间状态或遮挡重要内容。

界面发生变化时先重新生成两张 WPF 原图，再导出整张展示图；原图和版式源文件随展示图一同提交。
