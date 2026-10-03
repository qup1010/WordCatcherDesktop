# 桌面图标资源

`icon.svg` 是图标源文件，`icon-*.png` 是各尺寸导出资源。图标沿用青绿色底色、浅色词卡、小写衬线字母 `a` 与琥珀色划线。

Windows 托盘与任务栏分别使用独立尺寸的 ICO 帧。图形主体约占画布 95%，ICO 包含 16、20、24、32、40、48、64、96、128、256 像素帧。

生成方式：在仓库根目录执行 `node tools/build-icons.mjs`，需提供 sharp；也可将已安装的 sharp 模块绝对路径作为第一个参数。生成时需要 Windows 上的 Georgia 字体，以保持字形一致。

脚本同时更新本目录 PNG 和 `src/WordCatcher.App/Assets/` 中的 ICO/PNG。源文件与导出图标均随代码提交；不要只替换某一尺寸。
