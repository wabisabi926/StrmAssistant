# 贡献指南

## 环境

- .NET SDK：插件目标 `net6.0`，测试目标 `net8.0`，两个 SDK 都要装
- 一个可重启的 Emby 4.9.1.90+ 实例（当前开发基线 4.10.1.0）

## 构建与测试

```bash
# 构建（关闭自动部署，避免往 Emby 目录写错文件名）
dotnet build StrmAssistant/StrmAssistant.csproj -c Release -p:StrmAssistantPluginOutputDir=

# 部署：把版本化产物复制成插件实际加载的文件名
cp StrmAssistant/bin/Release/net6.0/StrmAssistantPlus_v<版本>.dll <Emby>/programdata/plugins/StrmAssistantPlus.dll

# 测试
dotnet test StrmAssistant.Tests/StrmAssistant.Tests.csproj -c Release
```

两个顺序约束：

1. **先构建插件，再跑测试**。测试工程引用的是构建产物，不是源码；顺序反了测的是旧 DLL。
2. **不要依赖自动部署**。csproj 的 `PostBuildMerge` 会把合并结果复制成 `$(TargetFileName)`（即 `StrmAssistant.dll`），
   而插件实际加载的文件名是 `StrmAssistantPlus.dll`。两者并存时 Emby 会同时加载，出现重复插件。
   需要自动部署时显式传 `-p:StrmAssistantPluginOutputDir=<plugins 目录>`，并自行清理多余副本。

测试工程已在 `StrmAssistant.sln` 中，IDE 可直接构建与运行；CI 的 `Build Plugin` 也会在构建后执行 `dotnet test`，
测试不通过不会进入发布步骤。

## 版本号与标签

- 版本号只在**准备发版**时提升，四处（`AssemblyVersion` / `FileVersion` / `Version` / `InformationalVersion`）保持一致。
- 某个版本**已打过 tag 就不能复用**：后续改动另起新版本号，避免标签内容与实际产物不一致。
- 打标签后**核对它指向的提交**（`git show <tag> --no-patch --format="%h %s"`）。
  本仓历史上出现过两个标签指向同一个旧提交的情况，只看「标签存在」会误判。
- `CHANGELOG.md` 的版本段要与代码版本同步；README 的「插件版本」表格同理。

## 提交前自查

- [ ] `dotnet build` **0 警告 0 错误**
- [ ] 构建输出 `[OK] resx keys consistent`
- [ ] `dotnet test` 全绿（当前基线：174 通过 / 2 跳过 —— 跳过的是需要真实 ISO 样本的用例）
- [ ] 涉及 Emby 内部行为的改动，在真机上验证过，并把日志原文贴进提交信息
- [ ] 新增选项同步了三语 resx（Designer 会自动补）

> 显示类改动验证时注意：已打开的 Emby 网页会缓存条目数据，普通强刷也可能无效，
> 用无痕窗口或清除站点数据后再看；判断是否服务端问题时，先比对新旧两个查询接口的返回。

## 提交信息

Conventional Commits 前缀：`feat` / `fix` / `perf` / `refactor` / `docs` / `chore` / `ci`。

正文说清三件事：

1. **改了什么**（行为层面的变化，不是"改了某文件"）
2. **为什么**（尤其是"看起来可以那样做但不行"的取舍）
3. **怎么验证的**（日志、SQL 结果、页面状态码 —— 贴原文）

示例：

```
perf: 队列批处理内存回收

task 列表超过 100 条即剔除已完成的 task，否则闭包捕获的 BaseItem 会一直活到
Task.WhenAll 返回，大批量刮削期间 RSS 持续上涨。

验证：追更队列实跑 48 条，'Clear Item Queue Stopped' 后 175ms 出现
'MemoryCleaner [SetProcessWorkingSetSize]: ... RSS 1 MB (freed 71 MB)'
```

## 不要做的事

- **不要合并外部主分支**。对方把 SDK 从 `mediabrowser.server.core 4.9.1.90` 降回 `4.8.10.0`，
  还带了闭源二进制；本仓已分叉 190+ 提交，合并等于回退。
- **不要给 `StrmAssistant.Tests` 加 `ProjectReference`**，会触发 ILRepack 并把半成品部署到 Emby。
- **不要用空字符串"省略"分组标题**，会渲染成没有标题的折叠块（`[DisplayNameL]` 的 resx 值为空串时，
  页面上是一块没有标题的缩进区域）。要去掉标题必须把那层容器拆掉。
- **涉及视觉的改动（对齐/缩进/配色）先截图确认再落码**，只凭 JSON 结构推断视觉会翻车。

## 代码组织

- **纯逻辑放 `Common/`，不要粘在 Emby 实体上**。参数拼装、字符串判定、URL 处理这类逻辑一旦依赖
  `BaseItem` / `Plugin.Instance` 就无法单测，而静默失败的缺陷恰恰集中在这里。
  现有先例：`VersionIdentity`（版本身份）、`UrlSanitizer`（日志脱敏）、`ProbeCommand`（外部命令参数）。
- **外部命令（ffmpeg / ffprobe）的参数必须可单测**。参数写错不会抛异常，只会静默不生效——
  例如把绝对流索引当成视频序号、输出文件扩展名不合法，都不会报错。
- **不要裸写 `_ = Task.Run(...)`**，用 `BackgroundTaskRunner.Run(name, body)`：异常会被记录、运行数量可查。
- **补丁状态不代表功能可用**。`PatchBase` 的 `Applied` 只说明调用了 `Prepare`；底层补丁是否真的打上，
  看 `PatchTracker` 的错误列表与启动日志里的「部分补丁未能应用」告警。

## 破坏性操作

删除文件、批量删条目这类不可逆动作，必须满足三条：

1. **先有只读预览**：`GET /Items/{Id}/DeleteVersionPreview?DeleteParent=true` 列出将删除的条目与跳过原因，
   预览本身不产生任何副作用。
2. **判不明就跳过**：候选归属无法确定时宁可少删（例如文件名相似但分辨率不同），并把跳过原因写进日志。
3. **日志留痕**：实际删除的每个路径都要有 Info 日志，便于事后追溯。

## 文档

内部设计文档（架构说明、ADR、开发者手册、历史实施记录）**保留在本地 `docs/`，不发布到仓库**，
已在 `.gitignore` 中忽略。修改代码时如果发现文档与实现不符，直接在本地更新即可，不要提交 `docs/`。
