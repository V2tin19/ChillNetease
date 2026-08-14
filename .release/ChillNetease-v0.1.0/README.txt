ChillNetease v0.1.0 — 网易云音乐游戏内插件
适用游戏：Chill with You: Lo-Fi Story（Steam）

【功能】
- F6 面板：歌单（我创建的/我收藏的）、歌曲列表、搜索、二维码登录
- 联网流式播放（走游戏原生播放器，不缓存本地）
- 游戏切歌键正常切换，随机模式随机跳

【安装】
1. 前置：游戏根目录需有 BepInEx 5（5.4.x）。
   若没有，请先安装 BepInEx 5（BepInExPack），
   把 BepInEx/、doorstop_config.ini、winhttp.dll 放到游戏根目录。
2. 把本压缩包里的 chillWithNetease 文件夹整个复制到：
     <游戏目录>\BepInEx\plugins\
3. 启动游戏，按 F6 打开面板。

最终目录结构：
  <游戏目录>\BepInEx\plugins\chillWithNetease\ChillNetease\
  ├── ChillNetease.Plugin.dll   (BepInEx 插件)
  ├── ChillNetease.dll          (网易云桥接库)
  └── QRCoder.dll               (二维码生成)

【使用】
- F6：开关面板
- ↑/↓ 或鼠标滚轮：选择；Enter：打开歌单 / 播放歌曲
- ←：返回
- 登录：未登录时 F6 显示二维码，用手机网易云 App 扫码即可（登录态自动保存，下次免登录）
- 搜索：面板右上角"搜索"按钮 → 输入歌名（中文用 Ctrl+V 粘贴）→ Enter 或点"搜索"

【备注】
- 仅支持 Windows，需要能联网访问网易云服务
- 登录后播放音质/可用性取决于账号权限（VIP 歌曲需 VIP）
