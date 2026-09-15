# ChillNetease — 网易云音乐游戏内插件

在《Chill with You: Lo-Fi Story》游戏内直接使用网易云音乐：登录、浏览歌单、搜索、播放，全程走**游戏原生播放器**，无外挂窗口、无卡顿。

基于 BepInEx 5 + Harmony + Unity IMGUI，音频通过游戏原生 `MusicService` **联网流式加载**（不缓存到本地）。

## 功能

- **F6 面板**：歌单分组标签页（我创建的 / 我收藏的 / 链接导入，`→` 切换）、歌曲列表、搜索、登录二维码
- **二维码扫码登录**（登录态持久化，下次启动免登录）
- **联网流式播放**：UnityWebRequest 流式加载 → 游戏原生播放器，绕过游戏 100 首导入上限
- **歌单整体注入**游戏原生播放列表，游戏切歌键（上一首/下一首）正常切换，随机模式随机跳
- **链接导入歌单本地保留**：导入的歌单存到本机（`BepInEx/config/ChillNetease.playlists.json`），重启自动恢复注入，面板里可单独删除（行尾 `✕` 或 Delete 键）
- **大歌单保护**：注入游戏播放列表的曲目数有上限（默认 1000，`BepInEx/config` 可调），播放/切歌接近窗口末尾自动续上后续歌曲，随机模式在全量歌单中随机——数千首歌的歌单也不再掉帧
- **游戏内搜索**（支持 Ctrl+V 粘贴中文歌名）
- **鼠标滚轮**滚动列表

## 安装

前置要求：

1. 游戏本体（Steam: Chill with You: Lo-Fi Story）
2. **BepInEx 5**（5.4.x，x64）：若游戏根目录下没有 `BepInEx` 文件夹，先下载 BepInEx 5 —— Windows 64 位用 [`BepInEx_win_x64_5.4.23.5.zip`](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip)（其它版本见 [BepInEx releases](https://github.com/BepInEx/BepInEx/releases)），把包里的 `BepInEx/`、`doorstop_config.ini`、`winhttp.dll` 放到游戏根目录。

步骤：

1. 下载最新 release：`ChillNetease-vX.Y.Z.zip`
2. 解压得到 `chillWithNetease` 文件夹
3. 把整个 `chillWithNetease` 文件夹复制到 `<游戏目录>\BepInEx\plugins\` 下
4. 启动游戏，按 **F6** 打开面板

> 最终结构应为（记住：这三个 DLL 必须在同一个文件夹里）：
>
> ```
> <游戏目录>/BepInEx/plugins/chillWithNetease/ChillNetease/
> ├── ChillNetease.Plugin.dll   (BepInEx 插件)
> ├── ChillNetease.dll          (go-musicfox 编译的原生桥接库, MIT)
> └── QRCoder.dll               (二维码生成, MIT)
> ```

> **首次启动慢 / F6 没反应？**
> 插件文件未签名，杀毒软件（Windows Defender 等）首次扫描可能让游戏启动后 1~2 分钟才加载完插件——表现为画面已进游戏但按 F6 无反应，稍等再按即可。若每次启动都慢，把**游戏目录**加入杀毒软件白名单（Windows 安全中心 → 病毒和威胁防护 → 排除项 → 添加文件夹）。
>
> 等过之后仍然没反应？见下面的 **按 F6 没反应？** 小节。

## 使用

| 按键 | 功能 |
| --- | --- |
| F6 | 开关面板 |
| ↑ / ↓ | 移动选择（也支持鼠标滚轮） |
| Enter | 打开歌单 / 播放歌曲；搜索框内输入后按 Enter = 搜索 |
| ← | 返回上一层 |
| → | 歌单页切换分组（我的 / 收藏 / 导入） |
| Delete | 歌单页"导入"分组下，删除选中的导入歌单 |
| Ctrl+V | 搜索框粘贴（中文歌名） |
| 鼠标 | 点击行 = 选择并激活；点标签/搜索按钮直接操作；导入歌单行尾 `✕` = 删除 |

登录：未登录时 F6 会显示二维码 → 手机网易云 App 扫码 → 自动进入歌单。登录态保存在本机（go-musicfox cookie），下次启动免登录。

## 按 F6 没反应？

按顺序试：

1. **先在游戏画面里点一下，再按 F6。** 插件通过 Win32 读取按键，只在游戏是前台窗口时才响应——点过浏览器、QQ 之后必须重新点回游戏。
2. **笔记本试 `Fn + F6`。** 不少笔记本把 F6 设成了多媒体键。
3. **等 1~2 分钟。** 插件未签名，杀毒软件首次扫描会拖慢加载（见上面的说明）。
4. **确认没装两份。** `BepInEx\plugins` 下若出现两个 `ChillNetease.Plugin.dll`，会互相冲突。
5. **确认三个 DLL 在同一个文件夹里。** 拆开放的话，面板能打开但登录、播放都不可用。

还是不行，把下面这些**连同本仓库地址** https://github.com/V2tin19/ChillNetease 一起丢给 AI 问，比自己翻日志快得多：

- **`BepInEx\LogOutput.log` 的内容** —— 游戏根目录的 `BepInEx` 文件夹里，排查一切问题的第一站
- 一句现象描述：进游戏后按 F6 完全没反应 / 面板闪一下就没了 / 等两分钟也没反应
- 有的话再附上诊断报告（见下）

### 一键生成诊断报告

下载 [`tools/support-collect.ps1`](tools/support-collect.ps1) 保存到游戏文件夹里（放哪一层都行），右键 →「使用 PowerShell 运行」，它会在**桌面**生成 `ChillNetease-诊断报告.txt` —— BepInEx 版本、插件文件是否齐全、日志关键行、杀软白名单状态都在里面，还会自动给出判读结论。

> 右键没有「使用 PowerShell 运行」：在任意位置按住 `Shift` + 右键 →「在此处打开 PowerShell 窗口」，粘贴：
>
> ```
> powershell -ExecutionPolicy Bypass -File "完整路径\support-collect.ps1"
> ```

**请在"游戏正在运行、刚按过 F6"的状态下运行** —— 报告会自己判断日志是不是本次会话的。

### 怎么读日志

加载完全正常时，日志里会有这几行（版本号可能不同）：

```
[Info   :   BepInEx] Loading [Chill Netease 0.3.0]
[Info   :Chill Netease] Chill Netease 0.3.0 loaded
[Info   :Chill Netease] [Netease] Bridge init: True
[Info   :Chill Netease] [ChillNetease] 已挂钩 RoomGameManager.Update（每帧驱动）
[Info   :Chill Netease] [ChillNetease] Harmony 补丁已应用，共挂钩 16 个方法
```

按 F6 之后还应该出现 `[Info :Chill Netease] [Netease] 面板 打开`。按这几行的**有无**，能直接定位到环节：

| 日志现象 | 问题出在 | 怎么修 |
| --- | --- | --- |
| 整份日志里**没有任何** `Chill Netease` 字样 | 插件根本没被 BepInEx 加载 | 见下面「常见原因」 |
| 有 `Loading [Chill Netease ...]`，但出现 `未找到 RoomGameManager.Update` | 游戏版本与插件不匹配，面板永远不会响应 | 只能等作者适配 |
| 有加载、有挂钩，但按 F6 后**没有** `面板 打开` | 按键没传进插件 | 回到最上面：前台焦点 / Fn / 等 1~2 分钟 |
| 有 `面板 打开`，但屏幕上看不到面板 | 按键已生效，是渲染/显示问题 | 报上分辨率与窗口模式（全屏 / 无边框窗口） |
| `Bridge init: False` | 三个 DLL 不在同一目录 | 按安装说明把三个放到一起 |

### 常见原因

| 原因 | 怎么确认 | 怎么修 |
| --- | --- | --- |
| BepInEx 没装 / 装错版本 | 游戏根目录没有 `winhttp.dll`、`doorstop_config.ini`；或 `BepInEx\core` 里是 BepInEx 6 的文件 | 装 **BepInEx 5.4.x x64**（见上面的前置要求） |
| 文件被安全软件隔离 | 安全中心 → 病毒和威胁防护 → 保护历史记录，看有没有 `ChillNetease.Plugin.dll` | 恢复文件，并把**游戏目录整个**加入排除项，然后重启游戏 |
| 目录层级放错 | `BepInEx\plugins` 里找不到 `ChillNetease.Plugin.dll` | 对照上面的结构重新解压 |
| 装了重复的旧版本 | 多个目录下都有 `ChillNetease.Plugin.dll` | 只留一份，其余删掉 |
| 主程序名不是 `Chill With You.exe` | 游戏根目录的 exe 名对不上 | 插件的进程白名单写死了这个名字，对不上 BepInEx 会**静默跳过**整个插件（日志里一个字都不会有） |

## 从源码构建

### 插件（.NET，netstandard2.1）

```powershell
dotnet build src/ChillNetease.Plugin/ChillNetease.Plugin.csproj -c Release `
  -p:GameDir="D:\SBeam\steamapps\common\Chill with You Lo-Fi Story"
```

需要 .NET SDK；`GameDir` 指向游戏目录（构建引用游戏 Managed 程序集与 BepInEx）。

### 原生桥接库 ChillNetease.dll（Go）

见 [`tools/netease_bridge/BUILD.md`](tools/netease_bridge/BUILD.md)。

## 项目结构

- `src/ChillNetease.Plugin/` — BepInEx 插件（Harmony 补丁 + IMGUI 面板 + 播放导入）
- `tools/netease_bridge/` — Go 桥接库源码（go-musicfox 二次封装：登录 / 歌单 / 搜索 / 播放地址）
- `tools/NeteaseProbe/` — 独立验证工具（调试用）
- `tools/support-collect.ps1` — 排查脚本：收集环境、plugins 目录、日志关键行，生成诊断报告
- `docs/NETEASE_RESEARCH.md` — 技术调研笔记

## 许可

- 插件与工具源码：MIT（见 [LICENSE](LICENSE)）
- `ChillNetease.dll`：基于 [go-musicfox](https://github.com/go-musicfox/go-musicfox)（MIT），插件仅通过 P/Invoke 调用其导出接口
- `QRCoder.dll`：[QRCoder](https://github.com/codebude/QRCoder)（MIT）

> 本项目通过个人账号登录使用网易云音乐服务，仅供个人学习使用，请遵守网易云音乐服务条款。
