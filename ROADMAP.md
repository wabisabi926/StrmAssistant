# 后续计划 / Roadmap

本文件随仓库同步，换机器 `git clone` 后即可看到。内部设计文档仍在本地 `docs/`（被 `.gitignore` 排除，不入库）。

约定：完成一项就删掉对应小节，并把行为变更写进 `CHANGELOG.md`。

## 待办

### 1. TheIntroDB `mapping` 集序转换（4.0.0.9 的 P2 遗留）

**背景**：TheIntroDB v4 支持 `mapping=default|official|dvd|alternate|absolute|regional`，用于把调用方的季集号换算成 TVDB default 序。当前只按 Emby 里存的季集号查询，DVD/绝对序的剧集可能取到别的集的数据。

**为什么还没做**：本机测试库 9 部剧全部是 `DisplayOrder = Aired`，没有 DVD/绝对序样本，无法实机验证——按项目约定不做无法真机验证的改动。

**实现要点**：
- 仅在走 `tvdb_id` 定位时传 `mapping`；`tmdb_id` / `imdb_id` 路径不传
- `Series.DisplayOrder` 映射：`Aired → official`、`Dvd → dvd`、`Absolute → absolute`
- 服务端对不支持的序会返回 400，需回退为不带 `mapping` 重试一次
- 单集已有 `tvdb_episode_id` 时本身已精确 pin，可不传 `mapping`

**验收标准**：找一部 DVD 或绝对序的剧（可临时改 `DisplayOrder` 并刷新元数据），对比加/不加 `mapping` 时同一集取到的片头时间是否对应同一集；日志确认无 400。

### 2. 404 响应体 `code` 分类（4.0.0.9 的 P2 遗留）

**背景**：v4 的 404 带 `code`：`no_submissions`（该集没人提交）、`episode_not_found`（季集不存在）、`media_not_found`（媒体 id 解析不到）。当前统一按“无数据”处理，日志里无法区分是覆盖不足还是元数据 id 有问题。

**实现要点**：读取错误响应体解析 `code`，分别计数并在任务汇总输出；`media_not_found` 过高说明 id 映射有问题，值得单独提示。

**触发条件**：4.0.0.9 跑几天后，若日志显示大量 404 且需要判断是哪一类，再做。

**注意**：Emby 的 `IHttpClient` 对非 2xx 抛 `HttpException`，错误体需要从异常/响应流单独读取，不能依赖 `response.StatusCode` 分支。

## 观察项（4.0.0.9 上线后）

任务日志收尾三行用于判断是否需要下一个版本：

1. `Mixed-source merges`：长期为 0 说明“完整源优先”足够；若频繁出现，说明两源都经常不完整，需要评估补全策略
2. `Request failures: status 400`：出现即说明 ID 组合还有边界情况（例如某类媒体不接受当前参数组合）
3. `disabled for the rest of this run`：说明某源在该网络下连续失败被熔断，需要评估超时与降级阈值

## 已知限制

- `no_intro` / `no_outro` 标记未参与决策：实测存在反例（TheIntroDB 有片头而 IntroDb 标记无片头），信任它会漏标
- 混源拼接（两源各缺一半时）无法避免，已记录 `mixed` 计数与同字段差值，超过 30s 打 Warn
