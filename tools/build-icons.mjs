import { createRequire } from 'node:module';
import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// 传入已有 sharp 的模块路径也可生成，无须在桌面项目中安装 Node 依赖。
const require = createRequire(import.meta.url);
const sharp = require(process.argv[2] || 'sharp');
const root = fileURLToPath(new URL('../', import.meta.url));
const pack = path.join(root, 'public/wordcatcher-fluent-icon-pack');
const assets = path.join(root, 'desktop/src/WordCatcher.App/Assets');
const svg = await readFile(path.join(pack, 'icon.svg'));
const master = await sharp(svg).resize(1024, 1024).png().toBuffer();
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
const frames = [];
for (const size of sizes) {
  const png = await sharp(master).resize(size, size).png().toBuffer();
  await writeFile(path.join(pack, `icon-${size}.png`), png);
  frames.push({ size, png });
}
for (const size of [512, 1024]) {
  await writeFile(path.join(pack, `icon-${size}.png`), await sharp(master).resize(size, size).png().toBuffer());
}
await writeFile(path.join(assets, 'WordCatcher.png'), await sharp(master).resize(512, 512).png().toBuffer());

// ICO 中保存各尺寸独立 PNG 帧，供 Windows 按 DPI 选择。
const header = Buffer.alloc(6 + frames.length * 16);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(frames.length, 4);
let offset = header.length;
frames.forEach(({ size, png }, index) => {
  const entry = 6 + index * 16;
  header[entry] = size === 256 ? 0 : size;
  header[entry + 1] = size === 256 ? 0 : size;
  header.writeUInt16LE(1, entry + 4);
  header.writeUInt16LE(32, entry + 6);
  header.writeUInt32LE(png.length, entry + 8);
  header.writeUInt32LE(offset, entry + 12);
  offset += png.length;
});
await writeFile(path.join(assets, 'WordCatcher.ico'), Buffer.concat([header, ...frames.map(frame => frame.png)]));
console.log(`Generated desktop icon and ${sizes.length} ICO frames.`);
