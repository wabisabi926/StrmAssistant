# 对外接口（豆瓣评分）

本文件只说明面向外部集成的 3 个豆瓣评分接口，主要用于 Kodi 皮肤/助手和自定义脚本。
其余接口属于插件内部（设置页、Web UI、诊断），可能随版本调整，不在此承诺兼容性。

## 认证

与 Emby API 相同，任选其一：

```http
X-Emby-Token: <api_key 或用户令牌>
```

```
?api_key=<api_key>
```

- **API Key**：等价管理员，不按用户可见性过滤。
- **用户令牌**：只返回该用户媒体库中可见条目的评分。
- 未登录或无权限时返回 Emby 的 401/403。

## 1. 按 Emby ItemId 查分

```http
GET /Douban/Ratings?Ids=27820,27821
```

- `Ids`：逗号分隔的 Emby 内部 ItemId，最多 **80** 个，无效项会被忽略。

响应：

```json
{
  "Enabled": true,
  "Ratings": [
    {
      "ItemId": "27820",
      "SubjectId": "1292052",
      "Rating": 9.7,
      "Votes": 3123456,
      "Applied": true,
      "Source": "douban",
      "CommunityRating": 9.7
    }
  ]
}
```

## 2. 按外部 ID 查分（Kodi 桥接）

```http
GET /Douban/RatingsByImdb?Ids=tt0111161,tmdb:550,douban:1292052
```

- 适合 Kodi 皮肤/助手：用 `VideoLibrary` 里的外部 ID 直接查分，无需读取 Emby for Kodi 的映射库。
- **这些 ID 只是 Kodi 端可用的查找键，评分仍来自 Emby 缓存的豆瓣快照。**
- `Ids`：逗号分隔，去重后最多 **80** 个；支持：
  - `tt0111161`（IMDb，也接受 `imdb:tt0111161`）
  - `tmdb:550`（TMDb）
  - `douban:1292052`（豆瓣 SubjectId）
- 没有 IMDb 的条目，只要 Emby 条目有 TMDb 或豆瓣 ID，也可以用对应键查询；三者都没有时请改用 `/Douban/Ratings?Ids=<Emby ItemId>`。

响应：

```json
{
  "Enabled": true,
  "Ratings": [
    {
      "Key": "tt0111161",
      "KeyType": "imdb",
      "Imdb": "tt0111161",
      "ItemId": "27820",
      "SubjectId": "1292052",
      "Rating": 9.7,
      "Votes": 3123456,
      "Applied": true,
      "Source": "douban",
      "CommunityRating": 9.7
    }
  ]
}
```

## 3. 请求刷新单条评分

```http
POST /Douban/RefreshRating?Id=27820
```

```json
{ "Scheduled": true }
```

`Scheduled=false` 表示没有排入刷新：冷却未到、缺少豆瓣 ID、豆瓣请求熔断中或条目不可见。

## 字段说明

| 字段 | 说明 |
|---|---|
| `Enabled` | 接口所需功能是否开启；`false` 时 `Ratings` 为空 |
| `ItemId` | Emby 内部条目 ID |
| `Key` | 仅接口 2：规范化后的查询键（`tt...` / `tmdb:...` / `douban:...`） |
| `KeyType` | 仅接口 2：`imdb` / `tmdb` / `douban` |
| `Imdb` | 仅接口 2：条目自身的 IMDb ID，可能为空（不是评分来源） |
| `SubjectId` | 豆瓣条目 ID |
| `Rating` / `Votes` | 豆瓣评分与评价人数 |
| `Applied` | 当前 `CommunityRating` 是否确为豆瓣分（未被 TMDB/IMDb 覆盖） |
| `Source` | 当前社区评分来源：`douban` / `tmdb` / `imdb`，空表示无显式来源 |
| `CommunityRating` | 条目当前的社区评分，供调用方校验图标与数字同源 |

行为说明：

- `/Douban/Ratings` 的 `Enabled` 在「豆瓣数据」或「社区评分来源」任一开启时为 `true`。
- `/Douban/RatingsByImdb` 只要求「豆瓣数据」开启；`Ids` 支持 IMDb / TMDb / 豆瓣键，接口内部按条目 ProviderIds 取豆瓣快照。
- 查询键只用于定位 Emby 条目；条目没有豆瓣 ID 时，仍会尝试 IMDb key 下的兜底评分快照。
- 未开启「抓取豆瓣评分」时，分数相关字段会置空（`Rating`/`Votes` 为 `null`，`Applied=false`），但 ID 映射仍会返回。
- `/Douban/RatingsByImdb` 在开启「同步豆瓣评分到 Kodi」时仍会返回分数。

## 示例

```bash
curl -H "X-Emby-Token: <api_key>" \
  "http://192.168.1.10:8096/Douban/RatingsByImdb?Ids=tt0111161,tt0068646"
```

Kodi Python 示例：

```python
import json
import urllib.request
from urllib.parse import urlencode

req = urllib.request.Request(
    "http://192.168.1.10:8096/Douban/RatingsByImdb?" + urlencode({"Ids": "tt0111161"}),
    headers={"X-Emby-Token": "<api_key>"})

with urllib.request.urlopen(req, timeout=5) as resp:
    ratings = json.load(resp)["Ratings"]
```

## 稳定性

- 这 3 个接口会尽量保持路径与字段名不变；字段只增不减。
- 路径带 `IsHidden` 的其余接口不保证兼容性，请勿依赖。
