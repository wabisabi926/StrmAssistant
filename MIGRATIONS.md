# 配置迁移策略

本文约束设置结构变更、旧配置兼容与迁移代码的退场规则。目标是：用户升级不丢设置，同时不让迁移代码无限累积。

- 当前版本线：4.0.0.1（2026-11-11）。
- **直接升级最低版本：3.0.0.78**。
- 当前状态：4.0 起不再内置旧布局迁移；旧配置由启动保护拦截，要求先安装 3.0.0.78 完成迁移。

## 现行机制

1. `OptionsSchema` 在插件配置目录记录 `StrmAssistant_options_schema.json`，当前 `CurrentVersion = 1`（与 3.0.0.78 的结构相同）。
2. 版本文件缺失或损坏时视为当前结构；版本高于当前插件（降级）时不回写、不降版本。
3. `LegacyConfigGuard` 只检查选项 JSON 的**根级旧键**（如 `GeneralOptions`、`UnlockIntroSkip`），不会把新版嵌套结构误判为旧配置。
4. 命中旧键时插件拒绝加载，日志给出明确提示：先安装 3.0.0.78 并启动一次完成迁移，再升级 4.0。
5. 未命中时正常加载，并在初始化成功后写入 schema v1。
6. 中文搜索索引方案检查（`MigrateChineseSearchOptions`）与配置布局无关，继续保留，每次启动执行。
7. **新增可选字段不视为需要迁移的结构变更**：如 4.0.0.1 的“刮削媒体库”“包含电影”，只加字段、不搬路径，保持 schema v1，不写迁移；旧配置缺字段时使用默认值。只有移动、重命名或改变旧字段语义才升级 schema 并写迁移。

## 已删除迁移（4.0.0.0）

| 迁移 | 范围 | 行数 |
|---|---|---|
| `MetadataEnhanceOptionsMigration` | 元数据增强平铺 → 9 个分组；3.0.0.55 / 3.0.0.74 评分调整 | 485 |
| `MediaInfoExtractOptionsMigration` | 媒体信息提取平铺 → 分组；截图后处理 v2；HDR 分组（3.0.0.62） | 343 |
| `ExperienceEnhanceOptionsMigration` | 体验增强平铺 → 分组；界面功能组 v2 | 326 |
| `IntroSkipOptionsMigration` | 片头片尾平铺 → 3 个分组 | 178 |
| `PluginOptionsMigration` | 主设置首页平铺 → 分组 | 166 |
| `NetworkProxyOptionsMigration` | 网络与直链跨文件搬入独立 Tab | 108 |

另外删除了插件级旧迁移：`MigrateLegacyOptionGroups`（GeneralOptions/ModOptions 拍平、分钟字段、配置迁移目录）与 `MigrateImageCaptureExcludeContainers`。合计约 1600 行迁移代码及对应测试。

## 升级支持窗口

- 低于 3.0.0.78 的用户：先安装 3.0.0.78，启动一次完成配置迁移，再升级 4.0。
- 3.0.0.78 用户没有 schema 文件，属于正常情况：缺失视为当前结构，由根级旧键检测区分真正的旧布局。
- 4.0 起新配置统一记录 schema v1。

## 旧配置保护边界

- 只检查根级键；新版嵌套结构中的同名字段不会误报。
- JSON 损坏不按旧配置处理，交给 Store 自身解析逻辑；不会因此拒绝启动。
- 如果手工用旧 JSON 文件覆盖当前配置，必须同时删除 `StrmAssistant_options_schema.json`，否则版本门会认为配置已是当前结构而跳过保护。
- 通过插件「配置导入」恢复设置不受影响：它按当前属性路径写值，不替换文件布局。

## 未来结构变更

4.0 之后如果再次调整配置结构（移动/重命名/改语义；仅新增可选字段除外）：

1. `OptionsSchema.CurrentVersion` +1。
2. 新增迁移，并以 `OptionsSchema.StoredVersion < CurrentVersion` 作为执行条件。
3. 迁移必须幂等、带 flag 与 `*.legacy.bak`，缺失文件时安全降级，日志记录迁移键数量。
4. 在本文件登记范围、结构变化、引入版本与预计退役版本。
5. 只保留当前 major 线内的迁移；跨 major 升级仍以「最低直接升级版本」为准。

## 发版检查清单

- [ ] 本次是否移动/重命名设置路径或改变旧字段语义？是则递增 `OptionsSchemaVersion`、补迁移并登记本文件；仅新增可选字段不升 schema。
- [ ] 最低直接升级版本是否与 README / Release Notes 一致？
- [ ] 旧配置保护是否仍能识别被删除迁移覆盖的旧布局？
- [ ] 全量测试与实机启动（含缺失 schema、已有 schema 两条路径）通过。

## 明确不做

- **不为了减少代码行数合并设置分组或跨页搬设置**：这会改变 JSON 路径，反而制造新迁移。
- **不做纯表驱动的迁移重构**：收益是行数，风险是用户设置静默丢失。
- **不在补丁版本里静默提高最低升级版本**：升级门槛变化必须进 major 的 Release Notes。
