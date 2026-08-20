# CatClawMusic.Plugins.LxSource

猫爪音乐（CatClawMusic）的 **lx 源兼容插件**：内嵌 Jint JS 引擎运行 lx-music 自定义源 .js 脚本（在线导入 / 本地导入），接入网易云 / QQ / 酷我 / 酷狗等音乐源，独立于宿主应用编译与交付。

## 形态

- 实现宿主 `IOnlineMusicPlugin`：搜索（脚本 `musicSearch`）/ 播放直链（脚本 `musicUrl`）/ 歌词（原文 + 译文 + 罗马音三流，脚本 `lyric`）/ 封面（脚本 `pic`）/ 多音质（128k / 320k / FLAC）；
- 实现宿主 `IViewContributorPlugin`：向发现页贡献「LX 源音乐」入口页面（脚本导入 + 音源选择 + 搜索播放）；
- 实现宿主 `ILyricsProviderPlugin`：宿主歌词兜底链按 `RemoteId`（形如 `lx:netease:12345`）路由到本插件；
- **音源 = lx-music 自定义源 .js 脚本**：内嵌 [Jint](https://github.com/sebastienros/jint) 4.x JS 引擎（以嵌入资源分发，单 .ccp 自包含），在沙箱里执行 lx-music 客户端自定义源脚本（如 [render_api.js](https://fastly.jsdelivr.net/gh/Huibq/keep-alive/render_api.js)、长青SVIP 等），支持**在线导入**（URL 拉取）与**本地导入**（文件选择器）；
- 构建产出独立 DLL，Release 自动复制为 `CatClawMusic.Plugins.LxSource.ccp`，在宿主「插件管理 → 添加 → 本地 / 网络安装」中导入并启用后即生效，**零宿主改动**。

## 工作原理

### lx-music 自定义源协议

脚本通过 `globalThis.lx` SDK（`EVENT_NAMES` / `request` / `on` / `send` / `utils` / `env` / `version`）声明源与能力，插件按 `musicUrl` / `lyric` / `pic` / `musicSearch` action 分发请求。插件实现完整 SDK：`request` 支持 GET/POST + 对象 body（自动 JSON 序列化）+ JSON 响应解析；`utils.buffer` 与 `utils.crypto`（md5/sha256/hmacSha256/randomBytes/base64 已就绪，AES/RSA 待后续）；`console` no-op（脚本常用 console.log 调试）。

脚本异步初始化（async IIFE + `checkUpdate` + `send(inited)`）由 `engine.ExecuteAsync` 正确 drain 微任务；若脚本检测到新版发 `updateAlert` 且不发 `inited`，插件报告更新提示。

### 平台码映射

插件注册平台标识 `lx`，歌曲 Id 用复合形式 `source:id`（如 `netease:12345`），`RemoteId` 为 `lx:source:id`。脚本用 lx 短码（`wy/kw/kg/tx/mg`），插件做短码↔全名双向映射（netease↔wy / qq↔tx / kuwo↔kw / kugou↔kg / migu↔mg）。源 chips 在脚本加载后**动态重建**为脚本声明的各源。

## 使用

1. 准备一个 lx-music 自定义源 .js 脚本地址（在线）或本地 .js 文件（如 [render_api.js](https://fastly.jsdelivr.net/gh/Huibq/keep-alive/render_api.js)）；
2. 猫爪音乐 → 插件管理 → 添加 → 选择 `CatClawMusic.Plugins.LxSource.ccp` 并启用；
3. 发现页 → LX 源音乐：
   - **在线导入**：填写 .js 地址 → 点击「在线导入」（成功后显示声明的源数）；
   - **本地导入**：点击「本地导入」→ 文件选择器选 .js 文件；
   - 也可「清除」卸载当前脚本；
4. 选择音源（自动 = 脚本声明的第一个源；或指定具体源）→ 搜索 → 点歌播放。

音质在页面右上角循环切换（128k → 320k → FLAC）；配置持久化在 `{LocalApplicationData}/CatClawMusic.Maui/lx_source_config.json`（记录音质、默认源、脚本地址/路径）。

> ⚠️ lx-music 自定义源协议多数脚本只声明 `musicUrl`（仅解析播放直链，不搜索）。此类脚本导入后搜索不可用（插件提示「当前脚本不支持搜索」），但播放页可通过宿主聚合搜索的其它来源取歌后，由本插件按 `RemoteId` 路由解析直链。若脚本声明了 `musicSearch`，则本插件页面内可搜索。

## 已知限制

- 歌单 / 排行榜 / 私人漫游：协议无对应能力，未实现；
- FLAC 分段直链（`url` 为数组）取第一段播放，完整拼接需播放器分段支持；
- 登录 / 红心同步：匿名访问，未实现 `LikeSongAsync`；
- **`utils.crypto` 的 AES/RSA 暂未实现**（md5/sha256/hmacSha256/randomBytes/base64 已就绪）：需要平台签名加密的 .js 暂不可用，普通 wrapper 脚本（render_api 类、长青SVIP 等）不受影响。

## 构建

```bash
dotnet build -c Release
```

产物：`bin/Release/net10.0/CatClawMusic.Plugins.LxSource.ccp`（约 3 MB，含嵌入的 Jint + Acornima 程序集）

> 依赖：需要宿主仓库 `CatClawMusic` 中的 `CatClawMusic.Core` 工程（接口与模型定义，本项目以相对路径 `..\CatClawMusic\CatClawMusic.Core` 引用）。页面引用 `Microsoft.Maui.Controls` 10.0.20（与宿主一致）与 `CommunityToolkit.Mvvm`，均不随插件分发（`CopyLocalLockFileAssemblies=false`，由宿主提供）。Jint/Acornima 以嵌入资源随 .ccp 分发（宿主不提供，运行期由插件 `[ModuleInitializer]` 注册的 `AssemblyResolve` 从资源流加载）。

## 测试

```bash
dotnet run -c Release --project tools/SmokeTest
```

覆盖：lx-music-api-server 协议客户端解析（interval 三种形态 / 分段直链 / 封面数组兜底 / 三流歌词合并与 ±100ms 容差 / 网易冒号时间标签变体 / code≠0）、插件程序集端到端（反射加载真实 DLL + 嵌入 Jint 经 AssemblyResolve 加载 + 脚本 musicUrl 分发 + 平台码映射）、**真实混淆脚本加载（长青SVIP v1.2.0：inited + 声明 4 源 kg/tx/wy/kw + 全支持 musicUrl）**，50 项断言全过。

## 协议

[MIT](LICENSE)
