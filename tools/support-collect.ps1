<#
    ChillNetease 支持诊断脚本 v2
    ------------------------------------------------------------------
    用途：当有人反馈"装了插件但按 F6 没反应"时，让对方运行本脚本，
          会在桌面生成一份《ChillNetease-诊断报告.txt》，发给你即可定位问题。

    运行方式（任选其一）：
      A. 放到游戏文件夹里的任意位置（含 BepInEx 或 Chill With You.exe 的那层，
         或其下面任意一层都可以）→ 右键 → "使用 PowerShell 运行"
      B. 在任意位置执行：
             powershell -ExecutionPolicy Bypass -File .\support-collect.ps1
      C. 指定游戏目录：
             powershell -ExecutionPolicy Bypass -File .\support-collect.ps1 -GameDir "D:\Steam\steamapps\common\Chill with You Lo-Fi Story"

    产出：桌面 ChillNetease-诊断报告.txt（纯文本，可直接粘贴/发送）

    注意：本文件保存为 UTF-8 带 BOM，请勿用记事本另存为其它编码，否则中文会乱码。
#>
param([string]$GameDir = '')

$ErrorActionPreference = 'SilentlyContinue'
$report = New-Object 'System.Collections.Generic.List[string]'
function W($s) { [void]$report.Add([string]$s) }
function Sec($t) { W ''; W ('-' * 64); W ('== ' + $t); W ('-' * 64) }
function KB($n) { return [string]([math]::Round($n / 1KB, 1)) + ' KB' }

function Test-GameRoot {
    param([string]$d)
    if (-not $d) { return $false }
    if (-not (Test-Path -LiteralPath $d)) { return $false }
    if (Test-Path -LiteralPath (Join-Path $d 'BepInEx')) { return $true }
    if (Test-Path -LiteralPath (Join-Path $d 'Chill With You.exe')) { return $true }
    if (Test-Path -LiteralPath (Join-Path $d 'Chill With You_Data')) { return $true }
    return $false
}

W 'ChillNetease 诊断报告'
W ('生成时间 : ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
W ('PowerShell: ' + $PSVersionTable.PSVersion.ToString())

# ---------------------------------------------------------------- 1. 定位游戏目录
Sec '1. 定位游戏目录'
$libs = @(
    'C:\Program Files (x86)\Steam\steamapps\common',
    'C:\Program Files\Steam\steamapps\common',
    'C:\Steam\steamapps\common',
    'D:\Steam\steamapps\common',
    'D:\SBeam\steamapps\common',
    'D:\SteamLibrary\steamapps\common',
    'E:\Steam\steamapps\common',
    'E:\SteamLibrary\steamapps\common',
    'F:\SteamLibrary\steamapps\common'
)
$root = ''
$starts = @()
if ($GameDir) { $starts += $GameDir }
if ($PSScriptRoot) { $starts += $PSScriptRoot }
$starts += (Get-Location).Path
foreach ($s in $starts) {
    $cur = $s
    for ($i = 0; $i -lt 6; $i++) {
        if (-not $cur) { break }
        if (Test-GameRoot $cur) { $root = $cur; break }
        $up = Split-Path -Parent $cur
        if (-not $up -or $up -eq $cur) { break }
        $cur = $up
    }
    if ($root) { break }
}
if (-not $root) {
    foreach ($l in $libs) {
        if (Test-Path -LiteralPath $l) {
            foreach ($s in (Get-ChildItem -LiteralPath $l -Directory -Filter 'Chill with You*')) {
                if (Test-GameRoot $s.FullName) { $root = $s.FullName; break }
            }
        }
        if ($root) { break }
    }
}
if ($root) {
    W ('游戏目录 : ' + $root)
} else {
    W '!! 没找到游戏目录'
    W '   请把本脚本放到游戏文件夹里（含 Chill With You.exe 的那一层，或其子目录）再运行，'
    W '   或用 -GameDir 参数指定游戏根目录：'
    W '   powershell -ExecutionPolicy Bypass -File .\support-collect.ps1 -GameDir "D:\Steam\...\Chill with You Lo-Fi Story"'
    W ('   （已尝试：' + (($starts + $libs) -join ' | ') + '）')
}

if ($root) {
    # ------------------------------------------------------------ 2. 根目录 / BepInEx
    Sec '2. 游戏根目录（BepInEx 是否装上）'
    if (Test-Path -LiteralPath (Join-Path $root 'BepInEx')) {
        W '  BepInEx 文件夹 : 存在'
    } else {
        W '  BepInEx 文件夹 : 【缺失】'
        W '  [!!] 游戏根目录没有 BepInEx 文件夹 —— BepInEx 没装，插件根本不会被加载。'
        W '       需要先装 BepInEx 5.4.x x64：把 BepInEx/、doorstop_config.ini、winhttp.dll'
        W '       一起放到游戏根目录（与 Chill With You.exe 同一层）。'
    }
    foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'BepInEx.Patcher.exe', 'UnityPlayer.dll')) {
        $state = '缺失'
        if (Test-Path -LiteralPath (Join-Path $root $f)) { $state = '存在' }
        W ('  ' + $f.PadRight(24) + $state)
    }
    W '  根目录下的 exe：'
    foreach ($e in (Get-ChildItem -LiteralPath $root -File -Filter '*.exe')) { W ('    ' + $e.Name) }
    if (Test-Path -LiteralPath (Join-Path $root 'Chill With You.exe')) {
        W '  [OK] 找到 Chill With You.exe（与插件的进程名匹配）'
    } else {
        W '  [!!] 没有 "Chill With You.exe" —— 插件的进程白名单写死了这个名字。'
        W '       主程序名对不上时 BepInEx 会静默跳过本插件，日志里一个字都不会有。'
    }

    W ''
    W '  游戏进程状态：'
    $proc = @(Get-Process -Name 'Chill With You')
    if ($proc.Count -gt 0) {
        foreach ($p in $proc) {
            W ('    运行中   PID=' + $p.Id + '   启动于 ' + $p.StartTime + '   窗口标题="' + $p.MainWindowTitle + '"')
        }
        W '    [OK] 报告生成时游戏正在运行，日志反映的就是本次会话。'
    } else {
        W '    未运行'
        W '    [注意] 游戏当前没开，日志里是"上一次启动"的记录。'
        W '           排查 F6 问题时，请在"游戏正在运行、刚按过 F6"的状态下重新生成一份报告。'
    }

    W ''
    W '  显示器分辨率（面板看不见时用得上）：'
    $anyRes = $false
    foreach ($v in (Get-CimInstance Win32_VideoController)) {
        if ($v.CurrentHorizontalResolution) {
            W ('    ' + $v.Name + '   ' + $v.CurrentHorizontalResolution + 'x' + $v.CurrentVerticalResolution)
            $anyRes = $true
        }
    }
    if (-not $anyRes) { W '    （读不到活动显示器分辨率）' }

    # ------------------------------------------------------------ 3. BepInEx 版本
    Sec '3. BepInEx 版本（本插件需要 BepInEx 5.4.x x64）'
    $core = Join-Path $root 'BepInEx\core'
    if (Test-Path -LiteralPath $core) {
        foreach ($b in (Get-ChildItem -LiteralPath $core -File | Where-Object { $_.Name -like 'BepInEx*' })) {
            W ('  ' + $b.Name.PadRight(34) + 'v' + $b.VersionInfo.FileVersion)
        }
        foreach ($x in @('BepInEx.Unity.IL2CPP.dll', 'BepInEx.Unity.Mono.dll', 'BepInEx.Unity.IL2CPP.CoreCLR.dll')) {
            if (Test-Path -LiteralPath (Join-Path $core $x)) {
                W ('  [!!] 发现 ' + $x + ' —— 这是 BepInEx 6 的文件。本插件是 BepInEx 5 插件，装了 6 不会加载。')
            }
        }
    } else {
        W '  [!!] 没有 BepInEx\core 目录 —— BepInEx 没装好'
    }

    # ------------------------------------------------------------ 4. plugins 目录树
    Sec '4. BepInEx\plugins 目录树'
    $pl = Join-Path $root 'BepInEx\plugins'
    $allFiles = @()
    if (Test-Path -LiteralPath $pl) {
        $allFiles = @(Get-ChildItem -LiteralPath $pl -Recurse -File)
        W ('  文件总数 : ' + $allFiles.Count)
        foreach ($f in $allFiles) {
            W ('  ' + $f.FullName.Substring($pl.Length).TrimStart('\') + '   [' + (KB $f.Length) + ']')
        }
    } else {
        W '  [!!] 没有 BepInEx\plugins 目录'
    }

    W ''
    W '  BepInEx\config 下的配置（有插件配置 = 插件至少成功启动过一次）：'
    $cfgDir = Join-Path $root 'BepInEx\config'
    if (Test-Path -LiteralPath $cfgDir) {
        $cfgs = @(Get-ChildItem -LiteralPath $cfgDir -File)
        if ($cfgs.Count -eq 0) { W '    （config 目录是空的）' }
        foreach ($c in $cfgs) { W ('    ' + $c.Name) }
    } else {
        W '    [!!] 没有 BepInEx\config 目录'
    }

    # ------------------------------------------------------------ 5. 三件套完整性
    Sec '5. 插件文件完整性（三个 DLL 必须放在同一个目录）'
    $need = @('ChillNetease.Plugin.dll', 'ChillNetease.dll', 'QRCoder.dll')
    $map = @{}
    foreach ($f in $allFiles) {
        if ($need -contains $f.Name) {
            if (-not $map.ContainsKey($f.DirectoryName)) { $map[$f.DirectoryName] = @{} }
            $map[$f.DirectoryName][$f.Name] = $f.Length
        }
    }
    if ($map.Count -eq 0) {
        W '  [!!] plugins 里完全找不到 ChillNetease.Plugin.dll —— 插件文件没放进 plugins 目录'
    } else {
        foreach ($d in $map.Keys) {
            W ('  目录 : ' + $d)
            foreach ($n in $need) {
                if ($map[$d].ContainsKey($n)) {
                    W ('    ' + $n.PadRight(26) + '存在  ' + (KB $map[$d][$n]))
                } else {
                    W ('    ' + $n.PadRight(26) + '缺失   <== 缺这个')
                }
            }
            if ($map[$d].ContainsKey('ChillNetease.Plugin.dll') -and -not $map[$d].ContainsKey('ChillNetease.dll')) {
                W '    [!!] Plugin.dll 和 ChillNetease.dll 不在同一目录 —— 面板能开，但登录/搜索/播放全废'
            }
        }
        if ($map.Keys.Count -gt 1) {
            W '  [注意] 有多个目录存在插件 dll。BepInEx 会递归扫描 plugins 下所有子目录，'
            W '         凡是含 ChillNetease.Plugin.dll 的目录都会被加载；两份 Plugin.dll 会互相冲突。'
            W '         建议只保留一份，其余（旧版本 / 解压残留）删掉。'
        }
    }

    # ------------------------------------------------------------ 6. 日志判读
    Sec '6. BepInEx 运行日志'
    $logPath = Join-Path $root 'BepInEx\LogOutput.log'
    if (-not (Test-Path -LiteralPath $logPath)) {
        W '  [!!] 没有 LogOutput.log —— BepInEx 可能压根没启动（winhttp.dll / doorstop 没生效）'
    } else {
        $L = @(Get-Content -LiteralPath $logPath -Encoding UTF8)
        W ('  日志文件 : ' + $logPath)
        W ('  行数     : ' + $L.Count + '    最后写入 : ' + (Get-Item -LiteralPath $logPath).LastWriteTime)

        if ($proc.Count -gt 0) {
            if ((Get-Item -LiteralPath $logPath).LastWriteTime -ge $proc[0].StartTime) {
                W '  [OK] 日志最后写入晚于游戏启动 → 这份日志就是本次会话的'
            } else {
                W '  [!!] 日志最后写入早于游戏启动 → 疑似上次会话的日志，本次可能还没写、或插件没加载'
            }
        }

        W ''
        W '  --- 首行（BepInEx 版本 / 游戏）---'
        if ($L.Count -gt 0) { W ('  ' + $L[0]) } else { W '  （日志文件是空的）' }

        W ''
        W '  --- 状态行（判读用；已滤掉播放流水，不含歌单/歌曲名）---'
        $keyPat = 'Loading \[Chill Netease|Chill Netease .* loaded|Bridge init|已挂钩 RoomGameManager|Harmony 补丁已应用|面板 打开|面板 关闭|未找到|找不到|本地导入歌单加载完成|歌单同步完成'
        $key = @($L | Where-Object { $_ -match $keyPat })
        if ($key.Count -eq 0) {
            W '  （没有任何本插件的状态记录 —— 插件很可能根本没被加载）'
        } else {
            foreach ($k in $key) { W ('  ' + $k) }
        }

        W ''
        W '  --- 播放流水（原始日志里占绝大部分的噪音，已省略）---'
        $flowPat = '按需加载并播放|开始按需加载|已加载并继续播放|开始流式加载|音频加载完成|已获取播放地址|歌单歌曲加载完成|歌单已注入游戏播放列表|未加载，加载后播放|切歌'
        $nFlow = @($L | Where-Object { $_ -match $flowPat }).Count
        W ('  共 ' + $nFlow + ' 条播放相关日志，已省略（不含歌名）。')

        W ''
        W '  --- 错误 / 异常行（最多 20 条，含其它插件）---'
        $errs = @($L | Where-Object { $_ -match '\[Error|Exception|Failed|失败|未找到' } | Select-Object -First 20)
        if ($errs.Count -eq 0) { W '  （无明显错误）' }
        foreach ($e in $errs) { W ('  ' + $e) }

        W ''
        W '  --- 末尾 12 行（已滤掉重复心跳与播放流水，不含歌名）---'
        $tail = @($L | Where-Object { $_ -notmatch '心跳: connected' -and $_ -notmatch $flowPat })
        $t0 = [Math]::Max(0, $tail.Count - 12)
        for ($i = $t0; $i -lt $tail.Count; $i++) { W ('  ' + $tail[$i]) }

        # ---- 自动判读 ----
        $nLoad    = @($L | Where-Object { $_ -match 'Loading \[Chill Netease' }).Count
        $nHookOk  = @($L | Where-Object { $_ -match '已挂钩 RoomGameManager\.Update' }).Count
        $nHookBad = @($L | Where-Object { $_ -match '未找到 RoomGameManager\.Update|未找到 Assembly-CSharp' }).Count
        $nPanel   = @($L | Where-Object { $_ -match '面板 打开' }).Count
        $nBrOk    = @($L | Where-Object { $_ -match 'Bridge init: True' }).Count
        $nBrBad   = @($L | Where-Object { $_ -match 'Bridge init: False' }).Count

        W ''
        W '  ================= 自动判读 ================='
        if ($nLoad -eq 0) {
            W '  [结论] 插件没有被 BepInEx 加载。依次检查：'
            W '         · BepInEx 是否 5.4.x x64（装了 BepInEx 6 不会加载）'
            W '         · 主程序名是否 Chill With You.exe'
            W '         · plugins 下是否存在 ChillNetease.Plugin.dll'
            W '         · 是否被安全软件隔离或删除'
        } else {
            W '  [1] 插件已被 BepInEx 加载 ......... 正常'
            if ($nHookBad -gt 0) {
                W '  [2] 每帧驱动挂钩失败 ............. 致命'
                W '      游戏版本与插件不匹配（找不到 RoomGameManager.Update），插件无法响应 F6。'
                W '      这一条只能由作者做版本适配，请把日志报给作者。'
            } elseif ($nHookOk -gt 0) {
                W '  [2] 每帧驱动挂钩成功 ............. 正常'
            } else {
                W '  [2] 找不到挂钩记录（日志可能被截断，建议完整重启一次游戏再看）'
            }
            if ($nBrBad -gt 0) {
                W '  [3] 桥接库加载失败 ............... 异常'
                W '      ChillNetease.dll 没和 Plugin.dll 放在同一目录；面板能开，但登录/搜索/播放全废。'
            } elseif ($nBrOk -gt 0) {
                W '  [3] 桥接库加载成功 ............... 正常'
            }
            if ($nPanel -eq 0) {
                W '  [4] 按 F6 没有传到插件 ........... 键盘层问题'
                W '      依次尝试：先点一下游戏画面再按（插件只认前景窗口是游戏）/ 笔记本试 Fn+F6 /'
                W '      进游戏后先等 1~2 分钟（插件未签名，杀软首次扫描会拖慢加载）。'
            } else {
                W '  [4] 按 F6 已被插件收到 ........... 按键没问题'
                W '      屏幕上仍看不到面板的话，属于渲染/显示问题。'
                W '      请补充：显示器分辨率、窗口模式（全屏独占 / 无边框 / 窗口化）。'
            }
        }
        W '  ============================================'
    }

    # ------------------------------------------------------------ 7. Defender
    Sec '7. Windows Defender 排除目录'
    $mp = Get-MpPreference
    $ex = @()
    if ($mp) { $ex = @($mp.ExclusionPath) }
    $ex = @($ex | Where-Object { $_ -and $_ -is [string] -and $_ -notmatch '^N/A' })
    if ($ex.Count -gt 0) {
        foreach ($e in $ex) { W ('  ' + $e) }
        $hit = @($ex | Where-Object { $root -like ('*' + $_ + '*') -or $_ -like ('*' + $root + '*') })
        if ($hit.Count -eq 0) {
            W '  [!!] 以上排除项里没看到游戏目录 —— 建议把游戏目录整个加入排除项'
        } else {
            W '  [OK] 游戏目录已在排除项里'
        }
    } elseif ($mp) {
        W '  读不到排除项列表（需要以管理员身份运行 PowerShell 才能查看）。'
        W '  可手动确认：Windows 安全中心 → 病毒和威胁防护 → 管理设置 → 排除项。'
        W '  另外请顺便看一眼"保护历史记录"，确认 ChillNetease.Plugin.dll 没被隔离。'
    } else {
        W '  读不到（未启用 Defender 或系统无该组件）。'
    }
}

# ---------------------------------------------------------------- 8. 反馈补充项
Sec '8. 发给作者时请补充这几项'
W '  1) 具体现象：例如"进游戏后按 F6 没反应，等了两分钟也没有"'
W '  2) 窗口模式：全屏独占 / 无边框窗口 / 窗口化'
W '  3) 是否笔记本（按 F6 是否需要配合 Fn）'
W '  4) 除本插件外还装了哪些 BepInEx 插件（见上面第 4 节）'
W '  5) 游戏购买平台（Steam 等）'
W ''
W '  提示：报告里可能带出其它插件的日志；地理坐标（经纬度）已自动隐藏。'
W '        如果要贴到公开的地方，建议再自己扫一遍。'

# ---------------------------------------------------------------- 输出
$desk = [Environment]::GetFolderPath('Desktop')
if (-not $desk) { $desk = $PSScriptRoot }
if (-not $desk) { $desk = (Get-Location).Path }
$outFile = Join-Path $desk 'ChillNetease-诊断报告.txt'
$text = ($report -join "`r`n") -replace '\d{1,3}\.\d{3,}\s*,\s*-?\d{1,3}\.\d{3,}', '[坐标已隐藏]'
$text | Set-Content -LiteralPath $outFile -Encoding UTF8

Write-Host ''
Write-Host '诊断完成，报告已保存到：'
Write-Host ('  ' + $outFile)
Write-Host ''
Write-Host '请把这份 txt 发回给作者。'
