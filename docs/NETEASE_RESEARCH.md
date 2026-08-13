# 网易云音乐游戏内插件 —— 调研报告

> 日期：2026-08-13 ｜ 目标：在《Chill with You: Lo-Fi Story》内实现网易云登录、搜索曲目、歌单管理、收藏/取消收藏、用游戏内置播放器组织播放与排序。
> 调研对象：ChillPatcher（GitHub: BeyondtheApex/ChillPatcher）最新 main 分支 + 用户正在用的 v1.3.2.3_UI release + 游戏本体内部 API。

---

## 一、结论速览

| 问题 | 结论 |
|---|---|
| 作者插件为什么臃肿 | 两代架构都很重：旧版 = 自建模块系统 + 深度 patch 游戏 UI；新版 = **独立后端进程 + 独立 Flutter 窗口 + 自建播放内核**（OmniMixPlayer），三层架构完全绕开游戏原生播放器 |
| 为什么不能搜索曲目 | 底层 Go 库（ChillNetease.dll）**只导出了"按关键词搜歌单"，没有导出"搜单曲"**；UI 层更只暴露了固定的关键词配置（默认"献给聪音"），不是给用户用的 |
| 为什么连不上其他歌单 | 底层 API **有** `GetUserPlaylists`（用户全部歌单）+ `GetPlaylistSongs`（任意歌单歌曲），但 UI 没做"列出我的歌单→点击打开"；只注册了"我喜欢的音乐"专辑，其他歌单要手动填 ID |
| 为什么拖拽面板卡顿/不跟手 | UI 是 **Flutter 独立透明窗口**叠在游戏上（新版）或 OneJS/HTML 渲染（旧版），独立渲染层 + IPC 通信，帧率和手感都差 |
| **我们怎么办（推荐路线）** | **游戏原生播放器 + ChillNetease.dll 复用 + 轻量 IMGUI 面板**。不做作者那种自建播放器/独立窗口，代码量小、帧率与游戏一致、UI 贴合 |

---

## 二、作者方案解剖（两代架构）

### 2.1 新版（main 分支，OmniMixPlayer）—— 最重的一代

```
OmniMixPlayer.Backend（独立进程，自建音频播放 Audio/ + HTTP + 模块系统）
        │ IPC（OmniMixPlayer.SDK/Ipc）
OmniMixPlayer.gui_flutter（独立 Flutter 窗口，无边框叠在游戏上）
        │
游戏内插件（mods/chillPatcher，Harmony patch 游戏 UI 供 IPC 通信）
```

- 音乐源模块：**Netease / QQMusic / Spotify / Bilibili / LocalFolder**（5 个）
- 完全绕开游戏原生 `MusicService`（全仓库 0 处引用）
- 网易云模块 1477 行 + 桥接 1067 行：登录（二维码）、收藏、歌单（关键词搜歌单 + ID 导入）、私人 FM、歌词、封面、8 级音质
- 许可证：**GPL v3**

### 2.2 旧版（v1.3.2.3_UI，用户正在用的版本）

```
ChillPatcher.Module.Netease（4071 行，模块化）
   ├─ QRLoginManager：二维码登录（ChillNetease.dll 原生库）
   ├─ NeteaseSongRegistry：注册 MusicInfo → 游戏 MusicRegistry/AlbumRegistry/TagRegistry
   │    （"网易云音乐收藏"专辑 → 游戏原生"音乐库/标签"UI 显示）
   ├─ NeteaseFavoriteManager：收藏/取消收藏（LikeSong/UnlikeSong）
   ├─ CoreAudioLoader：UnityWebRequestMultimedia 流式加载 AudioClip（含 FLAC 流式）
   └─ MusicUI_AlbumArt_Patch：把"播放列表按钮图标"换成专辑封面（未登录=二维码）← 用户说的"封面位置放二维码"
```

- 关键：旧版是通过 **MusicRegistry 注册体系**（专辑/标签）进入游戏 UI 的，播放是**自建 AudioClip 流式通道**（不走 MusicService 的播放列表）
- 游戏里其他 patch：MusicService_*（收藏/排除/加载/顺序）、MusicUI_*、MusicTagListUI_*（深度驱动游戏 UI）

---

## 三、底层能力清单（ChillNetease.dll，Go/go-musicfox 编译的原生库）

P/Invoke 导出（C 接口，Ansi/UTF8 JSON 返回，`NeteaseFreeString` 释放）：

| 能力 | 函数 | 备注 |
|---|---|---|
| 二维码登录 | `NeteaseQRGetKey` / `NeteaseQRCheckStatus` / `NeteaseQRCancelLogin` | 状态码 801 等扫码 / 802 已扫待确认 / 803 成功 |
| Cookie 持久化 | `NeteaseSetCookie` / `NeteaseRefreshLogin` / `NeteaseIsLoggedIn` / `NeteaseLogout` | dataDir 落盘 cookie |
| 用户信息 | `NeteaseGetUserInfo` | 昵称/头像/VIP |
| 收藏歌曲 | `NeteaseGetLikeSongs` / `NeteaseGetLikeList` / `NeteaseLikeSong(id, like)` | ★ 用户需求"添加收藏移除" |
| **用户全部歌单** | `NeteaseGetUserPlaylists(limit, offset)` | ★ 解决"连不上其他歌单" |
| **任意歌单歌曲** | `NeteaseGetPlaylistSongs(id, getAll)` / `NeteaseGetPlaylistDetail(id)` | ★ 同上 |
| 搜索（仅歌单） | `NeteaseSearchPlaylistsByKeyword(keyword)` | ❌ 无搜单曲导出 |
| 播放 URL | `NeteaseGetSongURL(id, quality)` | 8 级音质，返回 URL/大小/格式/是否试听 |
| 歌词 | `NeteaseGetSongLyric(id)` | LRC 文本 |
| 私人 FM | `NeteaseGetPersonalFM` / `NeteaseFMTrash` | 可选 |

**音质**：standard / higher / exhigh / lossless / hires / jyeffect / sky / jymaster

---

## 四、痛点根因（对照用户反馈）

1. **"无法搜索曲目"**：DLL 只导出 `SearchPlaylistsByKeyword`（搜歌单），没导出搜单曲；UI 连搜歌单都没做成通用功能（关键词写死在配置里，默认给游戏角色"献给聪音"用）。
2. **"连不上'我喜欢的音乐'以外的歌单"**：底层有完整歌单 API，但 UI 只注册了"收藏"这一个专辑；用户想用其他歌单只能手填歌单 ID（隐藏配置 `CustomPlaylistIds`）。
3. **"插件太臃肿"**：模块系统（SDK/EventBus/Library）+ 深度 UI patch（几十个 Harmony 补丁）+ 新版本还叠了独立后端进程和 Flutter 窗口。

---

## 五、游戏原生播放器能力盘点（我们的机会）

用 GameApiDump 反射工具确认（游戏程序集 Assembly-CSharp）：

### 5.1 `Bulbul.MusicService`（播放核心，API 齐全）

| 能力 | 方法 | 对应需求 |
|---|---|---|
| 按索引播放 | `PlayMusicInPlaylist(Int32 index)` | 选曲播放 |
| 添加曲目 | `AddMusicItem(GameAudioInfo)` / `AddLocalMusicItem` | 导入网易云歌 |
| 收藏 | `RegisterFavoriteMusic` / `UnregisterFavoriteMusic` / `SetFavoriteTag` | 收藏/移除（配合 `AudioTag.Favorite`） |
| 排序 | `SwapAfter(target, origin)` | 播放列表排序 |
| 播放控制 | `PlayNextMusic` / `SkipCurrentMusic` / `Pause` / `UnPause` / `Stop` / `SetMusicProgress` / `SetShuffle` / `SetRepeat` | 完整播放控制 |
| 状态 | `CurrentPlayList` / `PlayingMusic` / `GetCurrentMusicProgress` | 当前列表/正在播放 |
| 加载 | `Load(IReadOnlyCollection<GameAudioInfo>)` | 批量加载 |

**实例获取方式（无静态单例）**：Harmony patch `AddMusicItem`/`Load` 用 `__instance` 捕获——作者旧版就是这么干的（`MusicService_RemoveLimit_Patch.CurrentInstance`）。

### 5.2 曲目模型 `Bulbul.GameAudioInfo` + 联网播放关键验证（IL 反编译结论）

- 字段：`Title` / `Credit`（歌手）/ `AudioClip` / `Tag`（AudioTag）/ `UUID` / `PathType`（AudioMode）/ `LocalPath`
- 创建：`CreateLocalFileAsync(filePath, uuid, ct)`（本地文件）与 `CreateNormal(AudioClip, tag, title, credit, uuid, ...)`（**预加载 clip**）
- **★ IL 反编译 `GameAudioInfo.GetAudioClip()`（关键结论）**：
  ```
  if (AudioClip != null) return AudioClip;          // 已加载 → 直接用（联网播放的入口！）
  if (PathType == LocalPc) AudioClip = LoadLocalFile(LocalPath, ct);
  else throw "AudioClip is not set. ...";           // Normal 且未预加载 → 报错
  ```
  → **游戏原生播放器无法"按需从 URL 加载"；但只要我们把流式加载好的 `AudioClip` 填入 `GameAudioInfo`（非空），游戏原生列表就能播放**，且 `DownloadAudioFile(uri)` 私有方法（UnityWebRequest `DownloadHandlerAudioClip` + `GetAudioType` 按 URL 判格式）证明游戏具备 URL 下载能力（reverse patch 可暴露，非必须）。
- `AudioMode` 只有 `LocalPc` / `Normal` —— 无"URL 模式"，联网播放 = 预加载 AudioClip 喂进列表

### 5.3 播放列表 UI `Bulbul.Mobile.MusicPlayListViewMobile`

- `Setup(IReadOnlyList<GameAudioInfo>, FacilityMusic)` 设置列表
- `OnChangeOrderForReasonDragged` —— **原生拖拽排序事件**（游戏自带拖拽排序！）
- `MusicImport` / `MusicRemove` / `ScrollToPlayingMusic` / `MusicPlayListDragReorderManipulator`

**结论：游戏原生播放器"播放列表 + 拖拽排序 + 收藏 + 随机循环"全部现成**，把网易云歌曲转成 `GameAudioInfo` 塞进 `MusicService` 即可，UI 和交互全免费。

### 5.4 已知限制（作者旧版补丁说明）

- **`AddMusicItem` 有 100 首上限**（`_allMusicList.Count >= 100` 拒绝）→ Harmony prefix 跳过该检查即可（作者已实现该补丁逻辑，GPL 需合规处理或自写）
- 100 首限制是"本地导入音乐"列表的上限；走 MusicRegistry 专辑体系（作者旧版路线）无此限制但有其他复杂度

---

## 六、推荐技术路线（草案 v2 —— 用户纠正后）

> 用户纠正：① **不要下载到缓存，联网播放**；② **歌单同步**：显示并选择账号创建的歌单 + 收藏的歌单（替代手填 ID）。

```
┌─ 游戏内插件（ChillAI 同款技术栈：BepInEx 5 + Harmony + IMGUI + Win32 键鼠）─┐
│                                                                            │
│  网易云数据源（C# P/Invoke ChillNetease.dll，自写桥接）                     │
│    ├─ 二维码登录（IMGUI 面板内嵌二维码，ChillNetease.dll QR API）           │
│    ├─ ★ 账号歌单同步：GetUserPlaylists 列出「我创建的 + 我收藏的」歌单      │
│    │    （名称/曲数/封面）→ 点击加载 GetPlaylistSongs                        │
│    └─ 搜索单曲（★ 需要补：走网易云 Web API weapi 搜索，见"待验证"）         │
│                                                                            │
│  ★ 联网播放（核心，v2 变更：不落盘）                                        │
│    GetSongURL(320k mp3) → UnityWebRequestMultimedia.GetAudioClip(           │
│        url, mp3, streamAudio=true)  边下边播 → 拿到流式 AudioClip           │
│    → GameAudioInfo.CreateNormal(clip, ...)  AudioClip 非空                  │
│    → MusicService.AddMusicItem（Harmony 去 100 首限制）                     │
│    → PlayMusicInPlaylist(index) → 游戏原生播放（GetAudioClip 直接返回 clip）│
│                                                                            │
│  控制面板 UI（IMGUI 轻量实现，F8 类开关）                                   │
│    搜索框 / 歌单列表（同步自账号）/ 播放队列 / 登录二维码 / 收藏按钮         │
└────────────────────────────────────────────────────────────────────────────┘
```

### 播放队列的分工（用户草案确认后）

- **播放队列显示/排序/收藏** → 全部交给**游戏原生** `MusicPlayListView`（CurrentPlayList + 拖拽排序 + AudioTag.Favorite）
- IMGUI 面板只管：登录二维码、歌单选择、搜索、点歌入队、收藏按钮（同步服务端）
- 面板内可再加一个"待加载队列"（点歌后 AudioClip 未就绪时显示加载中），就绪后移交游戏列表

### 联网播放的格式策略

| 音质 | 格式 | 方案 |
|---|---|---|
| standard / higher / **exhigh(320k)** | mp3 | ✅ Unity 原生流式（streamAudio），首选默认 |
| lossless / hires | flac | ⚠️ Unity 不解码 FLAC —— 默认降级 exhigh；无损后续可加（参考作者 ChillFlacDecoder 思路，GPL 合规） |

### 为什么比作者方案好

| 维度 | 作者（旧/新） | 我们 |
|---|---|---|
| 播放内核 | 自建（AudioClip 流式 / 独立进程） | **游戏原生 MusicService** |
| 播放列表/排序/收藏 UI | 自建 + 深度 patch 游戏 UI（几十个补丁） | **游戏原生，零成本** |
| 控制面板 | Flutter 独立窗口（卡顿）/ OneJS HTML | **游戏内 IMGUI（同帧渲染，无卡顿）** |
| 代码量 | 网易云模块 4000+ 行 + UI 框架数千行 | 预估 800-1500 行 |
| 拖拽组件 | 需要（还不好用） | **不需要**（排序用游戏原生的） |

### 可拖动组件问题的结论（用户提问）

作者组件（Flutter/OneJS）帧率低、不跟手的根因是**独立渲染层 + IPC**。我们的面板根本不需要那种东西：
- 面板是**游戏内 IMGUI**，和游戏同一帧渲染（帧率 = 游戏帧率），不存在不跟手
- 若面板需要拖动：IMGUI 里记录鼠标增量移动窗口即可（约 20 行），低成本
- 样式（颜色/边框）全部自绘，完全按喜好来
- **结论：不用作者的组件，自写轻量 IMGUI 面板**

---

## 七、许可证合规

- **ChillPatcher：GPL v3** —— 若复制其代码（MusicService 补丁、P/Invoke 桥接等），本项目需以 GPL v3 开源并保留版权声明（项目公开在 GitHub，合规成本低）
- **go-musicfox（ChillNetease.dll 来源）：MIT** —— 复用 DLL 需保留 MIT license 声明（release 包 licenses/ 目录已含）
- 建议：P/Invoke 桥接**自写**（接口签名是 API 事实，不受版权保护），Harmony 补丁逻辑参考思路自写；ChillNetease.dll 以"二进制依赖"方式分发并附 license

---

## 八、待验证项 / 风险（实现阶段第一步实测）

1. **ChillNetease.dll 独立加载**：不依赖 ChillPatcher 其他部分能否 P/Invoke 初始化（依赖 MSVC 运行时，游戏目录已有 vcruntime140 等；需确认 go-musicfox dataDir 结构）
2. **★ 流式 AudioClip 入原生列表**（v2 核心验证）：UnityWebRequest streamAudio 加载的 AudioClip 填进 CreateNormal → AddMusicItem → PlayMusicInPlaylist，游戏是否正常播放/显示（mp3 必然可行，实测确认 UI 与播放状态同步）
3. **歌单同步**：GetUserPlaylists 返回的"我创建/我收藏"歌单是否带够展示信息（名称/曲数/封面，PlaylistInfo 字段已确认）
4. **搜索单曲方案**：① C# 实现网易云 weapi 搜索（AES/MD5/RSA 加密，~100 行）＋登录 cookie；② 自编译 Go 辅助库加 `SearchSongs` 导出（需 Go 工具链）；③ 其他开源 C# 网易云 SDK。首选 ①
5. **100 首限制补丁**：确认 `_allMusicList.Count >= 100` 检查点与补丁方式（作者已有先例，自写）
6. **试听限制**：VIP 歌曲 GetSongURL 返回 isTrial，需提示或降级
7. **登录态持久化**：cookie 落盘位置与跨会话恢复
8. **FLAC 降级**：默认 exhigh mp3；无损用户可选后续加解码器

---

## 九、里程碑规划（草案）

| 阶段 | 内容 | 验收 |
|---|---|---|
| M1 桥接验证 | P/Invoke ChillNetease.dll：初始化/登录/获取歌单/获取歌曲 URL | 控制台/日志能打出用户歌单列表 |
| M2 播放导入 | 下载一首歌 → CreateLocalFileAsync → AddMusicItem（去 100 限制）→ 播放 | 游戏内能听到网易云歌曲，原生列表显示 |
| M3 控制面板 | IMGUI 面板：登录二维码 / 歌单列表 / 搜索框 / 收藏按钮 | 游戏内完成登录→选歌单→播放全流程 |
| M4 打磨 | 搜索单曲、收藏状态同步、缓存管理、设置项、发布 | 朋友从零安装可用 |

---

## 十、调研信息来源

- ChillPatcher 仓库完整克隆：`tools/ChillPatcher-src`（main）+ `tools/ChillPatcher-v1323`（v1.3.2.3_UI worktree）
- 用户本地 release 包：`D:\SBeam\...\BepInEx\plugins\chillWithNetease\ChillPatcher_1.3.2.3\`
- 游戏程序集反射 dump：GameApiDump 工具
