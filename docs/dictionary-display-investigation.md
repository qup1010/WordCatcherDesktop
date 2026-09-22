# `digital` 查词差异与修复

核验日期：2026-09-22。

## 数据格式结论

上游 [README](https://github.com/ahpxex/open-dictionary) 和
[分发契约](https://github.com/ahpxex/open-dictionary/blob/main/docs/export_contracts.md)
明确说明：同一发布内容的 `distribution.jsonl.gz` 与 `distribution.sqlite.gz`
使用相同的 `distribution_entry_v5` 语义；SQLite 的 `entries.document_json`
保留完整产品词条。客户端读取这个字段的方式正确，不需要改成 JSONL。

本次读取了本机已安装 SQLite 的 metadata 和 `digital` 原始词条：

- 总词条数：84,212；SQLite 契约为 `distribution_sqlite_v1`。
- `digital` entry ID：`6a75f37d-3e48-56a5-9139-22c9eed56414`。
- 形容词 3 个义项，名词 6 个义项，共 9 个。
- 原始形容词顺序为：手指义（common）、离散数字义（core）、计算机信息时代义（core）。
- 名词的数字设备/技术义为 core；琴键义、幽默手指义为 rare。

插件截图里的简义和记忆线索在本机词条里都能找到，支持“字段展示与排序差异”这一判断。
没有取得插件实际安装的 JSONL 文件，不能据此证明两份安装包的版本或逐字内容完全相同。

## 原因与新规则

1. 旧客户端保留原始义项顺序，导致手指义排在核心技术义之前。现在按 core、common、rare
   稳定排序，同级保留原始次序，不虚构语境匹配或词频排名。
2. 旧预览共取 3 个义项，被形容词全部占用。现在每个词性独立展示至多 3 个简义；
   名词摘要不会被前一词性挤掉。
3. 旧数据映射删除 rare 义项，点击“全部”也无法恢复。现在映射保留全部义项，默认仅将
   简义用于速览；完整解释、双语例句和用法提示在展开层提供。
4. 旧默认界面铺满 learner_explanation、usage_note 和 examples。上游确实将
   learner_explanation 定义为主要解释字段，字段本身没有用错；问题是没有区分速览和深入学习。
5. 新保存/复制摘要覆盖所有词性，按优先级使用简义（缺失时回退完整解释），不受当前
   展开状态影响。不批量覆盖用户已收藏或手工编辑的旧释义。

离线查词返回该词的通用词典条目，不根据捕获句子判定具体义项，也不会将品牌名称
DigitalOcean 自动当作 digital 的新词义。上下文解释由显式的 AI 释义操作提供。

## 验证

- 将实际 `digital` 词条作为带署名的回归样本，覆盖 9 义项完整保留、优先级、词性覆盖和保存摘要。
- 覆盖纯 rare 词条、缺少 short_gloss、无结构化义项的回退、单义项展开、切词重置和异步保存串位。
- WPF 离屏渲染检查默认速览、展开列表、单义项解释，确保底部保存按钮可见。

上游内容通过以下命令获取，实际 SQLite 查询以只读连接完成：

```powershell
smart-search fetch https://github.com/ahpxex/open-dictionary --format json
smart-search fetch https://github.com/ahpxex/open-dictionary/blob/main/docs/export_contracts.md --format json
```
