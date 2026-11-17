# 后续计划 / Roadmap

本文件随仓库同步，换机器 `git clone` 后即可看到。内部设计文档仍在本地 `docs/`（被 `.gitignore` 排除，不入库）。

约定：完成一项就删掉对应小节，并把行为变更写进 `CHANGELOG.md`。

## ✅ 4.0.1.0 ISO 码率 / 语言 / 配置收尾已完成（2026-11-17，Win + NAS 双平台）

同一张真实 4K 原盘的远程 strm（`死侍与金刚狼 (2024) UHD原盘`，7675.4s、2V+6A+10S、
容器 60.9 Mbps）在 Windows 与 NAS 的 Emby 4.10.2.0 上各自 `DeleteMediaInfo` → `ExtractMediaInfo`：

| 平台 | 条目 | 修复前（Emby 等分） | 最终落库 |
|---|---|---|---|
| Win | 28049 | 各 30,428,576 bps | **47,283,051 / 3,584,448 bps** |
| NAS | 287304 | 各 30,428,576 bps | **47,283,051 / 3,584,448 bps** |

用户 BDInfo 真值：4K 基础层 45.20 Mbps、1080p 增强层 3.98 Mbps、视频合计 49.18 Mbps；
当前结果与真值同量级。两端日志一致：`PID count source: uniform-stats`、
`per-stream bitrate from TS byte accounting: 47.28 / 3.58 Mbps`、
`TS video PID bytes: 0x1011=6478656,0x1015=491136 (videoShare=0.8359)`、
`filled 6 audio / 10 subtitle field(s)`、`wrote 1 audio profile label(s)`，无 `probe stream mismatch`。

音频侧同轮补齐：TrueHD 与其 AC-3 核心按顺序对齐并继承语言，6 条音轨 / 10 条字幕全部拿到语言；
按轨道标题与原始文件名写出「配置」文案，TrueHD 为 `Dolby TrueHD + Dolby Atmos`。
注意分母是**全部 PID**（用户明确要求），视频拿 `视频 PID 字节 / 全部 PID 字节` 的份额。

**发版状态**：**已发布**。提交 `80013d6` → `Test` run `44023582529`（Windows + Linux 双 job）
全绿 → tag `v4.0.1.0` → `Build Plugin` run `44024073093` 成功 → Release
[`StrmAssistantPlus v4.0.1.0`](https://github.com/wabisabi926/StrmAssistant/releases/tag/v4.0.1.0)
已创建，资产 `StrmAssistantPlus_v4.0.1.0.dll`（8,210,432 B）。
CHANGELOG / README 已按最终行为更新；**不要移动或重打该 tag**。详见本地 `docs/handoff.md`。

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

**✅ 根因已确认（2026-11-17 取证，非推测）**：不是 ILRepack。
是 `Resource.Embedder` 2.2.0 的 MSBuild Task（`SatelliteAssemblyEmbedderTask`，挂在
`CoreGenerateSatelliteAssemblies` 之后）在 Linux 上解析自身 `ResourceEmbedder.Core.dll` 路径出错：

```
MSBUILD : error : Failed injecting code. Could not find file '/home/runner/work/StrmAssistant/StrmAssistant/StrmAssistant/home\runner\.nuget\packages\resource.embedder\2.2.0\tasks\netstandard2.0\ResourceEmbedder.Core.dll'.
MSBUILD : error : Failed to inject required code into assembly: .../obj/Release/net6.0/StrmAssistant.dll
Build FAILED. (2 Error(s))
```

它用 `$(MSBuildThisFileDirectory)..\tasks\$(TaskFolder)\...` 拼路径，Unix 下反斜杠不作分隔符、
开头的 `/` 又丢了，于是把包根当相对路径拼到项目目录后 → 找不到 DLL → 编译阶段即失败。
日志里没有 `StrmAssistant: ILRepack merge ->`，说明 ILRepack 根本没跑到；
之前 `d7e6894`/`54627ec` 修的 ILRepack 路径不是本次失败点。

**为什么注解只有 exit code**：工作流的 grep 是 `error [A-Z]+[0-9]+|error MSB|error NETSDK`，
匹配不上 `MSBUILD : error :` 这种行 → 没有生成注解。取证需带凭据调 jobs logs API
（本机 `git credential fill` 有缓存 token；匿名 403），详见本地 `docs/handoff.md` 专节。

**已修复（2026-11-17）**：`StrmAssistant.csproj` 把 `Resource.Embedder` 的 PackageReference 加上
`Condition="'$(OS)'=='Windows_NT'"` —— Linux 构建不引入该包、跳过卫星注入，Windows 发布构建
（`build.yml` 跑 windows-latest）行为不变。`test.yml` 的 grep 正则补上 `MSBUILD : error` /
`Failed injecting` / `Build FAILED`，让今后这类失败直接进注解。
本地用 `-p:OS=Unix` 模拟过 Linux 构建：跳过 `Finished embedding cultures`、ILRepack 正常、0/0。

构建修复后 CI 又暴露一个被 Windows-only CI 掩盖的用例缺陷：`VersionIdentityTests` 硬编码
`@"D:\TV\Show\..."`，Linux 上 `\` 不是分隔符 → `Path.GetDirectoryName` 返回空串 →
`RejectsDifferentFolder` 两个目录判等（期望 False 得 True）。已改 `Path.Combine`（`072389a`），
生产代码无问题（路径来自 Emby）。

**✅ CI 复验通过（run `43998522801`）**：`test` 与 `test-linux` 两 job 均 success；
Linux job `Build succeeded` + ILRepack 跑到 + 509/509 passed。本待办完成。

### 4. NAS 真盘验证（发版门槛）— ✅ 已完成（2026-11-17，合成 300 段盘，本地 ISO + 远程 strm 双路径）

**环境**：内网 NAS 的 Emby 4.10.2.0（Linux）。本机无 Docker、WSL 被禁，故用本仓自建的
300 段无缝分支合成 ISO（119,197,696 B，600s，2 路视频 4K+1080p + TrueHD）代替真实盘——
它正是当初复现「>256 段被拒」的样本，验的是同一条 Linux 采样链路。
通过 SMB 放文件 + Emby API 建库/触发，全程可复现（手册见本地 `docs/dev/nas-verify-manual.md`）。

**断言全部通过**（日志原文 + API 落库双向核对）：

| 断言 | 结果 |
|---|---|
| 原生挂载不可用 → 进程内 UDF 兜底 | ✅ `native mount not supported ... falling back to in-process UDF` |
| playlist 300 段 | ✅ `selected playlist 00000.MPLS: 300 play items` |
| 均匀采样 | ✅ `probe summary: ... 256/300 clips` |
| 探测出 2 路视频 | ✅ `probe summary: ... probe=2V/2A/0S` |
| 码率按正片字节修正 | ✅ `corrected bitrate from title bytes: 1.6 Mbps (117964800 bytes / 600.0s, 2 video stream(s))` |
| 无少流告警 | ✅ `probe stream mismatch` 命中 0 |
| 落库媒体信息 | ✅ `Container=mpegts`、`RunTimeTicks=6000000000`(600s)、2 路 hevc 视频(3840x2160 + 1920x1080) + TrueHD/AC-3 |
| 临时文件落点 | ✅ `/vol1/@appdata/embyserver/cache/temp/strmassistant_iso/`（Emby cache，非 /tmp） |

**注意**：`TotalBitrate` 字段经 API 读为 None，但日志的 `corrected bitrate` 与 `-mediainfo.json`
落盘证明修正逻辑执行了；API 层码率字段的呈现差异不阻塞本验证（与本机 Windows 行为一致）。
**远程 strm（ISO over HTTP Range）同日补验通过**：本机起 `0.0.0.0` Range 服务器，strm 指向
`http://<LAN IP>:18101/multi300.iso`；注意 strm 入库必须触发 `Scan media library` 计划任务
（`Items/Refresh` 发现不了新 strm）。日志 `remote title sample saved` / `remote iso probing via
title sample` / `probe summary: 256/300 clips, probe=2V/2A/0S` / `corrected bitrate 1.6 Mbps`，
无 mismatch，落库 mpegts/600s/2 路 hevc。测试用临时库/文件已清理，**ISO 测试库与文件保留在 NAS**
（`strmassistant-iso-test`），便于复测；验证用 API Key 不入库。

### 5. BDMV 目录支持（下一版）

合成 BDMV 目录（占位 CLPI/index）入库后 Emby 未给出流/码率/时长，无 BlurayMounter 日志。
占位 CLPI 可能让 BDInfo 扫描失败，**不足以判定不支持**——先用真实 BDMV 目录复测；
若仍为空，评估插件侧 `HostMounter` 路径：复用本仓 MPLS/STN 解析只补章节/语言，不引入 BDInfo。
测试台：`.workbuddy-ai/iso-test/build_bdmv_folder.py`。

### 6. ISO 采样精度与弱网（P1）

- ~~单流码率改按样本内各 PID 字节占比分配~~ ✅ 已完成（`TsByteAccounting`）：ffprobe 对 mpegts 的
  视频流一律给 `bit_rate=N/A`，Emby 只能把容器码率**等分**给各路视频，带画中画的原盘会把 PiP
  报成和主画面同码率（实测样本两路视频字节 214080 : 78912 ≈ 73% : 27%，界面显示 50% : 50%）。
  现按探测输入的 TS PID 字节占比还原真实比例，分母含全部基本流（用户明确要求，不要改成视频 PID 之和）；
  PMT 声明视频 PID 数与探测视频流数不一致时跳过，宁可不改。日志
  `per-stream bitrate from TS byte accounting: …`。
- ✅ 单段长 m2ts 的头尾采样会低估视频占比（`29.60 / 5.05` vs BDInfo `45.20 / 3.98`）：
  探测样本保持不动，另写沿全片 32 窗口的 `.stats.ts` 只供 PID 字节统计（约 8MB）。
  两端最终 `47.28 / 3.58`，见文首完成记录。
- 远程采样预算自适应（弱网降到 32MB/128 段），需真机弱网验证。

## 观察项（4.0.0.9 上线后）

任务日志收尾三行用于判断是否需要下一个版本：

1. `Mixed-source merges`：长期为 0 说明“完整源优先”足够；若频繁出现，说明两源都经常不完整，需要评估补全策略
2. `Request failures: status 400`：出现即说明 ID 组合还有边界情况（例如某类媒体不接受当前参数组合）
3. `disabled for the rest of this run`：说明某源在该网络下连续失败被熔断，需要评估超时与降级阈值

## 已知限制

- `no_intro` / `no_outro` 标记未参与决策：实测存在反例（TheIntroDB 有片头而 IntroDb 标记无片头），信任它会漏标
- 混源拼接（两源各缺一半时）无法避免，已记录 `mixed` 计数与同字段差值，超过 30s 打 Warn
