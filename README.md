# CatClawMusic.Plugins.LxSource

猫爪音乐（CatClawMusic）的 **lx 源兼容插件**：一个服务器地址接入任意 lx-music-api-server 协议的音乐源（网易云 / QQ / 酷我 / 酷狗 / 咪咕 / 哔哩哔哩 等），独立于宿主应用编译与交付。

## 形态

- 实现宿主 `IOnlineMusicPlugin`：搜索 / 播放直链 / 歌词（原文 + 译文 + 罗马音三流）/ 封面 / 多音质（128k / 320k / FLAC），宿主聚合搜索与发现页直接可用；
- 实现宿主 `IViewContributorPlugin`：向发现页贡献「LX 源音乐」入口页面（服务器配置 + 脚本源配置 + 音源选择 + 搜索播放）；
- 实现宿主 `ILyricsProviderPlugin`：宿主歌词兜底链按 `RemoteId`（形如 `lx:netease:12345`）路由到本插件，播放页直接显示 lx 在线歌词（含译文与罗马音）；
- **两种接入方式，可单独或互补**：
  - **服务器地址**：兼容 [lx-music-api-server](https://github.com/lyswhut/lx-music-api-server) 协议，一个地址接入网易云/QQ/酷我/酷狗/咪咕/哔哩哔哩等；
  - **自定义源脚本（.js）**：内嵌 [Jint](https://github.com/sebastienros/jint) JS 引擎运行 lx-music 客户端自定义源脚本（如 [render_api.js](https://fastly.jsdelivr.net/gh/Huibq/keep-alive/render_api.js) 这类），播放直链优先走脚本。
- 构建产出独立 DLL，Release 自动复制为 `CatClawMusic.Plugins.LxSource.ccp`，在宿主「插件管理 → 添加 → 本地 / 网络安装」中导入并启用后即生效，**零宿主改动**。

## 工作原理

### 服务器地址模式（lx-music-api-server 协议）

lx-music-api-server 协议（参考 [lyswhut/lx-music-api-server](https://github.com/lyswhut/lx-music-api-server)）只依赖 5 个纯 GET 端点：

| 端点 | 用途 | 关键参数 |
|------|------|---------|
| `/ping` | 连接测试 | — |
| `/search` | 搜索（type=song） | name / page / limit / source |
| `/songurl` | 播放直链 | id / source / br(128·320·flac) |
| `/pic` | 封面 | id / source / size |
| `/lyric` | 歌词（lrc/tlyric/rlyric 三流） | id / source / time |

### 自定义源脚本模式（.js）

内嵌 Jint 4.x JS 引擎（以嵌入资源分发，单 .ccp 自包含），在沙箱里执行 lx-music 客户端自定义源脚本。脚本通过 `globalThis.lx` SDK（`EVENT_NAMES` / `request` / `on` / `send` / `utils` / `env` / `version`）声明源与能力，插件按 `musicUrl` / `lyric` / `pic` / `musicSearch` action 分发请求。

> ⚠️ lx-music 自定义源协议多数脚本只声明 `musicUrl`（仅解析播放直链，不搜索）。脚本只解析 URL 时，需配合服务器地址搜歌——播放直链走脚本、搜索走服务器，互补可用。

### 混合分发与平台码映射

插件在宿主注册平台标识 `lx`，歌曲 Id 用复合形式 `source:id`（如 `netease:12345`），`RemoteId` 为 `lx:source:id`——宿主歌词路由（`LyricsService` 按 `{platform}:{onlineId}` 前缀分发）、播放页封面等均兼容。脚本用 lx 短码（`wy/kw/kg/tx/mg`），服务器用全名（`netease/kuwo/kugou/qq/migu`），插件做双向映射：**播放直链 / 歌词 / 封面优先走脚本（若脚本声明了对应源与 action），失败回落服务器**；搜索优先脚本 `musicSearch`（若声明），否则走服务器。

## 使用

1. 部署一个 lx-music-api-server（自建或使用公共镜像），或准备一个 lx-music 自定义源 .js 地址（如 `https://fastly.jsdelivr.net/gh/Huibq/keep-alive/render_api.js`）：

   ```bash
   docker run -d --name lx-api -p 3000:3000 lyswhut/lx-music-api-server
   ```

2. 猫爪音乐 → 插件管理 → 添加 → 选择 `CatClawMusic.Plugins.LxSource.ccp` 并启用；
3. 发现页 → LX 源音乐：
   - **服务器地址**卡：填写地址 → 测试连接 → 保存；
   - **自定义源脚本（可选）**卡：填写 .js 地址 → 加载（成功后显示声明的源数）；
4. 选择音源（自动 = 服务器默认；或指定 netease / qq / kuwo / kugou / migu / bilibili 等）→ 搜索 → 点歌播放。

音质在页面右上角循环切换（128k → 320k → FLAC）；配置持久化在 `{LocalApplicationData}/CatClawMusic.Maui/lx_source_config.json`。

## 已知限制

- 歌单 / 排行榜 / 私人漫游：lx-music-api-server 协议无对应端点，未实现；
- FLAC 分段直链（`url` 为数组）取第一段播放，完整拼接需播放器分段支持；
- 登录 / 红心同步：lx 源匿名访问，未实现 `LikeSongAsync`；
- **脚本源 `utils.crypto` 的 AES/RSA 暂未实现**（md5/sha256/hmacSha256/randomBytes/base64 已就绪）：需要平台签名加密的 .js 暂不可用，普通 wrapper 脚本（如 render_api 类）不受影响。

## 构建

```bash
dotnet build -c Release
```

产物：`bin/Release/net10.0/CatClawMusic.Plugins.LxSource.ccp`（约 3 MB，含嵌入的 Jint + Acornima 程序集）

> 依赖：需要宿主仓库 `CatClawMusic` 中的 `CatClawMusic.Core` 工程（接口与模型定义，本项目以相对路径 `..\CatClawMusic\CatClawMusic.Core` 引用）。页面引用 `Microsoft.Maui.Controls` 10.0.20（与宿主一致）与 `CommunityToolkit.Mvvm`，均不随插件分发（`CopyLocalLockFileAssemblies=false`，由宿主提供）。Jint/Acornima 以嵌入资源随 .ccp 分发（宿主不提供，运行期由插件 `AssemblyResolve` 从资源流加载）。

## 测试

```bash
dotnet run -c Release --project tools/SmokeTest
```

本地模拟 lx-music-api-server 服务，覆盖协议客户端解析（interval 三种形态 / 分段直链 / 封面数组兜底 / 三流歌词合并与 ±100ms 容差 / 网易冒号时间标签变体 / code≠0 处理）、插件程序集端到端链路（反射加载真实 DLL）、**Jint 脚本引擎全链路（嵌入 Jint 加载 / 脚本 inited 声明 / musicUrl 分发 / 平台码映射 / 真实插件 GetPlayUrlAsync 走脚本）**，51 项断言全过。

## 协议

[MIT](LICENSE)
