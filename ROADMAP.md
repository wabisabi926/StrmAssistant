# 后续计划 / Roadmap

本文件随仓库同步，换机器 `git clone` 后即可看到。内部设计文档仍在本地 `docs/`（被 `.gitignore` 排除，不入库）。

约定：完成一项就删掉对应小节，并把行为变更写进 `CHANGELOG.md`。

## 待办

### 1. TheIntroDB `mapping` 集序转换（4.0.0.9 的 P2 遗留）

**背景**：TheIntroDB v4 支持 `mapping=default|official|dvd|alternate|absolute|regional`，用于把调用方的季集号换算成 TVDB default 序。当前只按 Emby 里存的季集号查询，DVD/绝对序的剧集可能取到别的集的数据。

**为什么还没做**：当前测试库中没有 DVD/绝对序样本（均为 `Aired`），无法实机验证——按项目约定不做无法真机验证的改动。

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

### 3. 修复 `test-linux` CI 构建（阻塞 4.0.1.0 发版）

**背景**：2026-11-17 给 `test.yml` 加了 ubuntu-latest job（与 Windows 跑同一套 509 项单测），
构建插件步骤失败（约 8 秒即退出，exit 1）。

**已做**：`ILRepack.exe`（net6.0 托管程序）在 Linux 需要 `dotnet` 前缀（已修）；
`ILRepackExe` 路径按 OS 选择分隔符（已修）；构建失败时会把 error 行写成 check-run 注解。

**怎么查**：job 日志 API 匿名 403，用网页 UI 打开失败 job 看日志，或
`gh run view <run-id> --log-failed`（需 gh 登录）。注解接口匿名可读：
`GET /repos/wabisabi926/StrmAssistant/check-runs/{id}/annotations`。

**候选原因**（未验证）：MSBuild 在 Unix 下对 csproj 里 `Tokenizer\win\libsimple.so` 这类
反斜杠 Include 的处理；`python`（资源检查目标，ContinueOnError 不应阻断）；
ILRepack 在 Linux 上的行为差异。

**验收**：`test` 与 `test-linux` 两个 job 都绿。

### 4. NAS 真盘验证（发版门槛，不是代码工作）

用真实 4K 原盘（别人的 300 段盘）在 Linux Emby 上跑一次提取：
日志出现 `probe summary: playlist=..., N/N clips, probe=2V/...`、`corrected bitrate`，
且无 `probe stream mismatch`；客户端媒体信息两路视频（4K + 1080p）、总码率接近真实值。

### 5. BDMV 目录支持（下一版）

合成 BDMV 目录（占位 CLPI/index）入库后 Emby 未给出流/码率/时长，无 BlurayMounter 日志。
占位 CLPI 可能让 BDInfo 扫描失败，**不足以判定不支持**——先用真实 BDMV 目录复测；
若仍为空，评估插件侧 `HostMounter` 路径：复用本仓 MPLS/STN 解析只补章节/语言，不引入 BDInfo。
测试台：`.workbuddy-ai/iso-test/build_bdmv_folder.py`。

### 6. ISO 采样精度与弱网（P1）

- 单流码率改按样本内各 PID 字节占比分配（当前按 ffprobe 比例缩放）；
- 远程采样预算自适应（弱网降到 32MB/128 段），需真机弱网验证。

## 观察项（4.0.0.9 上线后）

任务日志收尾三行用于判断是否需要下一个版本：

1. `Mixed-source merges`：长期为 0 说明“完整源优先”足够；若频繁出现，说明两源都经常不完整，需要评估补全策略
2. `Request failures: status 400`：出现即说明 ID 组合还有边界情况（例如某类媒体不接受当前参数组合）
3. `disabled for the rest of this run`：说明某源在该网络下连续失败被熔断，需要评估超时与降级阈值

## 已知限制

- `no_intro` / `no_outro` 标记未参与决策：实测存在反例（TheIntroDB 有片头而 IntroDb 标记无片头），信任它会漏标
- 混源拼接（两源各缺一半时）无法避免，已记录 `mixed` 计数与同字段差值，超过 30s 打 Warn
