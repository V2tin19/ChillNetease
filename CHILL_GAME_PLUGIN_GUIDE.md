# 《Chill with You: Lo-Fi Story》BepInEx 插件开发指南

> 基于 ChillNetease（游戏内网易云音乐，BepInEx 5 + Harmony + IMGUI + Go 原生桥接）
> 与 ChillAI（Agent 钩子驱动女主行为，Harmony + Win32 输入 + 本地 Bridge）两个插件的实战经验沉淀。
> 目标是让"下一个插件"少走弯路：哪些特性必须记住、哪些路径已经探明、哪些工具链可以直接复用。

---

## 一、游戏技术栈与关键路径（先记住这些）

| 项 | 值 |
|---|---|
| 引擎 | Unity（Mono，程序集名 `Assembly-CSharp`，命名空间 `Bulbul.*` + 全局命名空间） |
| 插件框架 | BepInEx 5（5.4.x，x64）；插件目标框架 **netstandard2.1** |
| 游戏目录 | `D:\SBeam\steamapps\common\Chill with You Lo-Fi Story\` |
| 游戏程序集 | `<游戏目录>\Chill With You_Data\Managed\Assembly-CSharp.dll` |
| 日志 | `<游戏目录>\BepInEx\LogOutput.log`（排查一切问题的第一站） |
| 插件目录 | `<游戏目录>\BepInEx\plugins\<你的插件名>\` |
| 插件配置 | `BepInEx\config\`（BepInEx 自动生成 cfg，`Config.Bind` 即存） |
| 游戏进程 | `Chill With You.exe` |

**构建命令**（两个插件通用）：

```powershell
dotnet build src/<Plugin>/<Plugin>.csproj -c Release `
  -p:GameDir="D:\SBeam\steamapps\common\Chill with You Lo-Fi Story"
```

`GameDir` 用于 csproj 里引用游戏 Managed 程序集与 BepInEx（`.csproj` 中 `<Reference>` 用 `$(GameDir)` 拼路径）。

---

## 二、五大"必须记住"的游戏特性（血泪教训）

### 1. ⚠️ 插件自建 MonoBehaviour 的生命周期消息（Start/Update/OnGUI）不会被驱动
本游戏对 BepInEx 插件/自建组件的 Unity 生命周期回调**一律不调用**。ChillAI 的 Worker 组件如果靠自己的 `Update()` 跑，什么都不会发生。

**解法（已验证 100% 有效）**：用 Harmony 挂钩游戏自己的每帧方法作为驱动源：

```csharp
// 挂钩 Bulbul.RoomGameManager.Update（postfix），每帧驱动你的逻辑
var roomType = gameAsm.GetType("Bulbul.RoomGameManager");
var updateMethod = roomType?.GetMethod("Update",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
harmony.Patch(updateMethod, postfix: new HarmonyMethod(...));
```

### 2. ⚠️ 游戏 Unity 输入系统不驱动插件 → 用 Win32 原生检测
键盘/鼠标事件也收不到（Unity Input 系统不驱动插件）。ChillAI 用 Win32 `GetAsyncKeyState`（按键）、`GetCursorPos` + `ScreenToClient` + `GetClientRect`（把屏幕坐标换算成游戏 IMGUI 坐标，注意 DPI/客户区缩放）。ChillNetease 同理（F6 开关、点击行、滚轮全部 Win32）。

要点：
- 按键要自己维护"上一帧状态"做边沿检测（`down && !wasDown`）
- 坐标换算：`clientPt.X * (Screen.width / rc.Right)`，否则点击位置错位
- 窗口句柄按进程 PID 找（`EnumWindows` + `GetWindowThreadProcessId`），每 10 秒刷新一次

### 3. ⚠️ 游戏状态机方法多为 async/私有 → patch 方法和调用链都要反编译验证
- 游戏大量用 **Cysharp UniTask**（async 方法）。async 方法本体只是"创建状态机并启动"的壳，**patch 壳方法即可拦截**（prefix return false 阻止状态机执行）。例：`HeroineAI.ChangePomodoroActionAsync()`。
- **私有方法 patch 必须用字符串方法名**，`nameof` 编译不过（CS0117）。
- **猜方法名=白干**：ChillAI 曾 patch `ReadyChangePomodoroState`——IL 证明它在整个程序集内**没有任何调用点**，拦截从未触发。**必须先反编译确认调用链再写 patch**（见第五节工具链）。

### 4. 游戏"动作"是状态机 + 持续动画，进入后没人切就永远保持
`HeroineAI` 是状态机（`ActionStateType`），工作动画（WorkPC）是**持续状态**，一旦进入不会自动结束。插件必须主动负责"切走"（见第五节 `ActionStateType` 枚举）。番茄钟驱动女主的**唯一汇聚入口**是 `HeroineAI.ChangePomodoroActionAsync()`（番茄开始/工作结束/休息结束/完成全走它）。

### 5. Windows 部署：插件 DLL 被游戏进程锁定
游戏运行中覆盖插件 DLL → `Permission denied`。**部署前必须确认进程退出**：`tasklist | grep -i "Chill With You"`。改配置/推送 GitHub 与游戏进程无关，无需关游戏；只有"往游戏目录拷 DLL"需要。

---

## 三、可移植的架构模式（两个插件都验证过的模板）

```
BepInEx 插件（netstandard2.1）
├── Plugin.cs          # BepInPlugin 入口：Config.Bind 配置项 + Awake 里挂钩子
├── Harmony 补丁类      # 每帧驱动（RoomGameManager.Update）+ 游戏行为拦截
├── Win32 输入层        # GetAsyncKeyState / 坐标换算（IMGUI 面板交互）
├── IMGUI 面板          # OnGUI + GUI.Box/GUI.Label（自绘 UI，游戏内设置）
└── 可选：原生桥接       # P/Invoke 调 Go/C 编译的 DLL（ChillNetease.dll）
```

**配置项模式**（游戏内 F8/F6 可调，自动存档）：
```csharp
EnableCodex = Config.Bind("General", "EnableCodex", true, "说明");
// 设置窗口里 Toggle(ref entry, "显示名") —— 值即时生效且写入 cfg
```

**Harmony 挂钩清单**（ChillAI 最终 7 个）：
- `RoomGameManager.Update`（每帧驱动）
- `HeroineAI.ChangePomodoroActionAsync`（番茄钟动作拦截——真实入口）
- `HeroineAI.ReadyChangePomodoroState` / `StartWork` / `StartBreak`（无调用点，保留兜底）
- `HeroineSelfTalkController.UpdateLottery`（自言自语抽签拦截）
- `PomodoroService.PlayPomodoroTimer`（postfix 捕获实例——PomodoroService 是普通类，不能 FindFirstObjectByType）

**"外部状态 → 游戏行为"桥接模式**（ChillAI）：
本地 HTTP Bridge（监听 127.0.0.1:17860）接收外部事件 → 状态机归一化 → 游戏插件轮询 → `HeroineAI.DebugChangeState(状态)`。外部工具（Codex/ZCode）的 hooks 事件通过转发脚本 POST 进 Bridge，**事件名相同即可复用同一转发脚本**。

---

## 四、探索完成的路径（可直接移植，别再挖一遍）

### 女主行为（HeroineAI）
- 实例获取：`Object.FindFirstObjectByType<HeroineAI>()`（全场景有效，每 2 秒刷新）
- 状态切换：`heroine.DebugChangeState(ActionStateType)`（public，调试接口，实测有效）
- 当前状态：`heroine.GetCurrentState()`
- **`ActionStateType` 枚举（EnumDump 已验证）**：
  ```
  None=-1(默认/自然)  WantTalk=1   WildStretchFullBody=4(伸展庆祝)
  WorkPC=17  WorkBook=18  WorkReport=19（工作类）
  BreakMovie=20  BreakReadBook=21  BreakListenMusic=22  BreakTeaTime=23(喝茶)  BreakSleep=24
  ```
  **经验**：工具/外设空闲时切 `None`（自然停手），别强制喝茶/工作；`idle` 绝不映射成工作状态。

### 番茄钟（PomodoroService / HeroineService）
- 调用链（IL 确认）：`PomodoroService.OnTimerEnd/StartPomodoro → HeroineService.StartPomodoroTimer/OnPomodoroWorkEnd/OnPomodoroBreakTimeEnd/OnPomodoroComplete → HeroineAI.ChangePomodoroActionAsync`（唯一入口）
- `PomodoroService` 是普通类（DI 创建）→ 从 `PlayPomodoroTimer`（私有）postfix 捕获实例 → `IsTimerRunning()` 实时查询
- `_onStartWork` 事件订阅者只有 `LeaveChairJudge`（离椅判定）与 `WorkedTimeSelfTalkSelector`（语音选择）——**不驱动女主主状态**，不用拦

### 自言自语（HeroineSelfTalkController）
- 入口 `UpdateLottery()`（抽签，拦截它=不说话）；`UseSelfTalk()` 执行说话
- 番茄钟运行中禁止：patch `UpdateLottery` prefix，条件 `IsTimerRunning()`

### 音乐（ChillNetease 用）
- 游戏原生播放器：`MusicService` 相关（UnityWebRequest 流式播放，绕过游戏 100 首导入上限）
- 播放列表注入：`PlaylistLink.InjectPlaylist(搜索结果/歌单)`
- 游戏切歌键（上一首/下一首）走原生播放器，注入后全接管（含随机模式）

### AI 工具钩子通道（ChillAI 用）
- Codex：`~/.codex/hooks.json`（6 事件，`type:"command"` 单命令串）+ `config.toml [features] codex_hooks=true`
- ZCode：`~/.zcode/cli/config.json`（**Claude Code hook schema**：`events`+`matcher`+`type:"process"` command/args 分开；5 事件无 SessionEnd；会话启动时快照配置）
- 转发脚本：PowerShell POST 到本地 Bridge，`-EventName` 传事件名，事件名相同即可复用

---

## 五、工具链：反编译与调用链分析（开发前必读）

| 工具 | 位置 | 用途 |
|---|---|---|
| IlDump | `tools/IlDump/` | 单类型 IL 转储：`IlDump <Assembly-CSharp.dll> <类型名> [方法名]`。看方法体确认调用链、方法可见性、是否为 async 壳 |
| GameApiDump | `tools/GameApiDump/` | 反射列出类型全部方法/属性/字段（含可见性标记 pub/pri），适合快速浏览成员 |
| FindCallers | `tools/FindCallers/` | **全局搜调用点**：`FindCallers <dll> <关键词>`，找出"谁订阅了 X / 谁调用了 Y"（关键：验证方法是否真的被调用） |
| EnumDump | `tools/FindCallers/EnumDump.cs` | 列嵌套枚举成员（`StartupObject=EnumDump` 切换后 `dotnet run`），如 ActionStateType 的值 |

**标准排查流程**：
1. `GameApiDump` 看类有哪些成员（public/private）
2. `IlDump` 转储目标方法体 → 找它调用谁 / 谁调它
3. `FindCallers` 全局验证"这个方法到底有没有被调用"（**防止白 patch**）
4. 需要枚举值时 `EnumDump`
5. 游戏里实测后看 `LogOutput.log` 验证 patch 是否命中（ChillAI 日志有"共挂钩 N 个方法"和拦截记录）

> IL 转储时嵌套类型名（如 `HeroineAI/ActionStateType`）IlDump 的 `GetType` 可能找不到——用 EnumDump（遍历 `NestedTypes`）代替。

---

## 六、已知陷阱清单（速查）

1. 插件 MonoBehaviour 生命周期不驱动 → 必须 Harmony 挂钩 `RoomGameManager.Update`
2. Unity 输入不驱动插件 → Win32 原生检测 + 边沿检测 + 客户区坐标换算
3. async 方法 patch 壳即可；私有方法用字符串名（`nameof` 会 CS0117）
4. **patch 前先 FindCallers 验证调用点**（ReadyChangePomodoroState 白挂的教训）
5. 普通类（非 MonoBehaviour）不能 `FindFirstObjectByType` → 用 patch postfix 捕获实例
6. 持续动画状态（WorkPC）进入后不会自动结束 → 必须有"切走"机制（温和校正/事件驱动）
7. 部署 DLL 前确认游戏进程退出（Windows 锁文件）
8. `git add -A` 前 `.gitignore` 要排除 `bin/ obj/ .release/`（二进制误提交教训）
9. bash 里命令含 `powershell` 字样会被安全策略拦截 → 分步执行/用文件传参；`gh release create --notes` 含反引号会被 shell 吃掉 → 用 `--notes-file`
10. `~/.codex/hooks.json` 被 Codex 桌面 App 重写 → 用 App 设置界面开关，不要只改文件

---

## 七、部署与发布流程（已跑通，直接抄）

```bash
# 1. 编译（GameDir 指向游戏）
dotnet build src/<Plugin>/<Plugin>.csproj -c Release -p:GameDir="D:\SBeam\..."

# 2. 部署（先确认游戏退出）
tasklist | grep -i "Chill With You" || echo "game not running"
cp src/<Plugin>/bin/Release/netstandard2.1/<Plugin>.dll "<游戏目录>/BepInEx/plugins/<插件名>/"

# 3. 发布 GitHub（独立仓库，公开/私有按需）
git init -b main && git add -A && git commit -m "..."
gh repo create <owner>/<repo> --public --source . --push   # 已存在空仓库则 remote add + push
# release 打包：staging 目录（.release/，已 gitignore）+ PowerShell Compress-Archive
gh release create vX.Y.Z ".release/<插件>-vX.Y.Z.zip" --title "..." --notes-file ".release/notes.md"
```

---

## 八、给"下一个插件"的建议

- **插件骨架直接抄**：Plugin.cs（Config.Bind + Harmony）+ Win32 输入层 + IMGUI 面板 + RoomGameManager.Update 驱动——ChillNetease/ChillAI 都是这个壳，改内部逻辑即可。
- **先反编译后写码**：凡是动游戏内部逻辑（状态机、UI、动画），先把调用链用工具链走一遍，别猜。
- **外部状态接入走 Bridge 模式**：本地 HTTP + 转发脚本 + 轮询，事件名抽象化，一个 Bridge 服务可服务多个插件。
- **版本号统一管理**：`Plugin.cs` 的 `PluginVersion`、GitHub tag、release zip 名称三者保持一致。
- **发布前敏感扫描**：`grep uid/昵称/本机路径/apiKey`，本机绝对路径和密钥绝不能进公开仓库。
