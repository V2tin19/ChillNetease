# ChillNetease — 网易云音乐游戏内插件

在《Chill with You: Lo-Fi Story》游戏内直接使用网易云音乐：登录、浏览歌单、搜索、播放，全程走**游戏原生播放器**，无外挂窗口、无卡顿。

基于 BepInEx 5 + Harmony + Unity IMGUI，音频通过游戏原生 `MusicService` **联网流式加载**（不缓存到本地）。

## 功能

- **F6 面板**：歌单列表（我创建的 / 我收藏的）、歌曲列表、搜索、登录二维码
- **二维码扫码登录**（登录态持久化，下次启动免登录）
- **联网流式播放**：UnityWebRequest 流式加载 → 游戏原生播放器，绕过游戏 100 首导入上限
- **歌单整体注入**游戏原生播放列表，游戏切歌键（上一首/下一首）正常切换，随机模式随机跳
- **游戏内搜索**（支持 Ctrl+V 粘贴中文歌名）
- **鼠标滚轮**滚动列表

## 安装（给朋友）

前置要求：

1. 游戏本体（Steam: Chill with You: Lo-Fi Story）
2. **BepInEx 5**（5.4.x，x64）：若游戏根目录下没有 `BepInEx` 文件夹，请先安装 BepInEx 5（BepInExPack），把 `BepInEx/`、`doorstop_config.ini`、`winhttp.dll` 放到游戏根目录。

步骤：

1. 下载最新 release：`ChillNetease-vX.Y.Z.zip`
2. 解压得到 `chillWithNetease` 文件夹
3. 把整个 `chillWithNetease` 文件夹复制到 `<游戏目录>\BepInEx\plugins\` 下
4. 启动游戏，按 **F6** 打开面板

> 最终结构应为：
>
> ```
> <游戏目录>/BepInEx/plugins/chillWithNetease/ChillNetease/
> ├── ChillNetease.Plugin.dll   (BepInEx 插件)
> ├── ChillNetease.dll          (go-musicfox 编译的原生桥接库, MIT)
> └── QRCoder.dll               (二维码生成, MIT)
> ```

## 使用

| 按键 | 功能 |
| --- | --- |
| F6 | 开关面板 |
| ↑ / ↓ | 移动选择（也支持鼠标滚轮） |
| Enter | 打开歌单 / 播放歌曲；搜索框内输入后按 Enter = 搜索 |
| ← | 返回上一层 |
| Ctrl+V | 搜索框粘贴（中文歌名） |
| 鼠标 | 点击行 = 选择并激活；点"搜索"按钮立即搜索 |

登录：未登录时 F6 会显示二维码 → 手机网易云 App 扫码 → 自动进入歌单。登录态保存在本机（go-musicfox cookie），下次启动免登录。

## 从源码构建

### 插件（.NET，netstandard2.1）

```powershell
dotnet build src/ChillNetease.Plugin/ChillNetease.Plugin.csproj -c Release `
  -p:GameDir="D:\Steam\steamapps\common\Chill with You Lo-Fi Story"
```

需要 .NET SDK；`GameDir` 指向游戏目录（构建引用游戏 Managed 程序集与 BepInEx）。

### 原生桥接库 ChillNetease.dll（Go）

见 [`tools/netease_bridge/BUILD.md`](tools/netease_bridge/BUILD.md)。

## 项目结构

- `src/ChillNetease.Plugin/` — BepInEx 插件（Harmony 补丁 + IMGUI 面板 + 播放导入）
- `tools/netease_bridge/` — Go 桥接库源码（go-musicfox 二次封装：登录 / 歌单 / 搜索 / 播放地址）
- `tools/NeteaseProbe/` — 独立验证工具（调试用）
- `docs/NETEASE_RESEARCH.md` — 技术调研笔记

## 许可

- 插件与工具源码：MIT（见 [LICENSE](LICENSE)）
- `ChillNetease.dll`：基于 [go-musicfox](https://github.com/go-musicfox/go-musicfox)（MIT），插件仅通过 P/Invoke 调用其导出接口
- `QRCoder.dll`：[QRCoder](https://github.com/codebude/QRCoder)（MIT）

> 本项目通过个人账号登录使用网易云音乐服务，仅供个人学习使用，请遵守网易云音乐服务条款。
