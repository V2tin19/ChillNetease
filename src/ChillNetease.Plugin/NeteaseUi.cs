using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using QRCoder;
using UnityEngine;

namespace ChillNetease.Plugin
{
    /// <summary>
    /// 游戏内网易云控制面板（IMGUI + Win32 键鼠）。
    /// 不依赖作者的 Flutter/OneJS 组件——与游戏同帧渲染，无卡顿、样式自绘。
    /// Tick() 由 Harmony 挂钩 RoomGameManager.Update 每帧驱动；Render() 由组件的 OnGUI 调用。
    /// </summary>
    public static class NeteaseUi
    {
        private enum View { Login, Playlists, Songs, Search }

        // ---- Win32 ----
        private const int VK_F6 = 0x75;   // F6（F7/F9 被 Chill Env Sync、F8 被 ChillAI 占用）
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;
        private const int VK_RETURN = 0x0D;
        private const int VK_LEFT = 0x25;
        private const int VK_RIGHT = 0x27;
        private const int VK_LBUTTON = 0x01;

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rc);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
        private const uint CF_UNICODETEXT = 13;

        /// <summary>读剪贴板文本（UTF-16，搜索框粘贴用）。</summary>
        private static string GetClipboardText()
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return null;
                try
                {
                    var h = GetClipboardData(CF_UNICODETEXT);
                    if (h == IntPtr.Zero) return null;
                    var p = GlobalLock(h);
                    if (p == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(p); }
                    finally { GlobalUnlock(h); }
                }
                finally { CloseClipboard(); }
            }
            catch { return null; }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

        /// <summary>仅当游戏窗口处于前台时才响应键盘（避免焦点在游戏外时误操作）。</summary>
        private static bool IsGameFocused()
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                GetWindowThreadProcessId(fg, out var pid);
                return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch { return false; }
        }

        // ---- 状态 ----
        private static View _view = View.Playlists;
        private static bool _windowOpen;
        private static bool _keyF7Down, _keyUpDown, _keyDownDown, _keyEnterDown, _keyLeftDown, _keyRightDown, _lmbDown;
        private static int _selected;
        private static int _scrollOffset;
        private static bool _showMine = true;
        private static IntPtr _gameHwnd;
        private static int _hwndFindFrame = -1;

        // ---- 登录二维码 ----
        private static string _qrUrl;          // 二维码 URL
        private static Texture2D _qrTexture;   // 生成的二维码图片
        private static string _qrHint = "";    // 状态提示（待扫码/已扫待确认/失败）
        private static float _qrNextPollTime;  // 下次轮询时间
        private static bool _qrPolling;        // 正在轮询登录结果

        // ---- 搜索 ----
        private static string _searchQuery = "";
        private static List<SongInfo> _searchResults = new List<SongInfo>();
        private static bool _searchLoading;
        private static string _searchError;
        private static bool[] _searchKeyDown = new bool[256]; // ASCII 键按下状态（下降沿检测）
        private static bool _keyCtrlVDown, _keyBackDown;
        private static bool _searchDirty;          // 搜索词已改动但未重新搜索（Enter 优先触发搜索而不是播放）
        private static string _pendingSearchQuery; // 本次搜索发起时的关键词（回填结果时判断是否过期）
        private static float _viewSwitchTime = -1f; // 最近一次视图切换时间（防双击第二击误触新视图的行）

        // ---- 数据 ----
        private static List<PlaylistInfo> _mine = new List<PlaylistInfo>();
        private static List<PlaylistInfo> _collected = new List<PlaylistInfo>();
        private static List<SongInfo> _songs = new List<SongInfo>();
        private static bool _songsLoading;
        private static long _currentPlaylistId;
        private static string _songsLoadError;
        private static readonly HashSet<long> _likedIds = new HashSet<long>();
        private static string _toast = "";
        private static float _toastUntil;

        private const float LineH = 26f;
        private const float HeaderH = 84f;
        private const float FooterH = 26f;
        private static Rect WindowRect = new Rect(16, 36, 440, 470); // y 由 Tick 按屏幕居中覆盖

        // GUI 样式：惰性初始化（不能在静态构造函数里访问 GUI.skin——不在 OnGUI 上下文会抛异常）
        private static GUIStyle _rowStyle;
        private static GUIStyle _selectedStyle;
        private static GUIStyle _headerStyle;
        private static GUIStyle _smallStyle;
        private static GUIStyle _toastStyle;
        private static GUIStyle _titleStyle;
        private static bool _stylesReady;

        private static void EnsureStyles()
        {
            if (_stylesReady) return;
            _rowStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 4, 0, 0),
                normal = { background = MakeTex(new Color(0.12f, 0.12f, 0.12f, 0.85f)) }
            };
            _selectedStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 4, 0, 0),
                normal = { background = MakeTex(new Color(0.25f, 0.2f, 0.05f, 0.9f)) }
            };
            _headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _toastStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold };
            _stylesReady = true;
        }

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        public static bool IsOpen => _windowOpen;

        public static void Toggle()
        {
            _windowOpen = !_windowOpen;
            if (_windowOpen)
            {
                // 打开时按登录态决定视图：未登录 → 显示二维码登录
                if (Plugin.Bridge != null && Plugin.Bridge.IsLoggedIn)
                {
                    if (_view == View.Login) _view = View.Playlists;
                }
                else
                {
                    _view = View.Login;
                    StartQrFlow();
                }
                _selected = 0;
                _scrollOffset = 0;
                _viewSwitchTime = Time.unscaledTime;
            }
            LogToggle();
        }

        private static void LogToggle()
        {
            Plugin.LogInfo("[Netease] 面板 " + (_windowOpen ? "打开" : "关闭"));
        }

        /// <summary>每帧驱动（Harmony postfix 调用，主线程）。</summary>
        public static void Tick()
        {
            // 面板位置：屏幕最上方、水平居中
            WindowRect.x = (Screen.width - WindowRect.width) / 2f;
            WindowRect.y = 10f;

            MusicImporter.Pump();
            // 登录二维码轮询（未登录视图）
            PollQrLogin();
            // 导入失败 → 面板提示（消费一次）
            if (!string.IsNullOrEmpty(MusicImporter.LastFailure))
            {
                ShowToast("无法播放: " + Truncate(MusicImporter.LastFailure, 42));
                MusicImporter.LastFailure = null;
            }
            HandleInput();
        }

        /// <summary>OnGUI 渲染（组件调用）。</summary>
        public static void Render()
        {
            if (!_windowOpen) return;
            EnsureStyles();
            HandleScrollWheel(); // IMGUI 滚轮：鼠标悬停在面板列表区时滚动列表
            GUI.Box(WindowRect, "", (GUIStyle)GUI.skin.box);
            DrawHeader();
            switch (_view)
            {
                case View.Login: DrawLogin(); break;
                case View.Playlists: DrawPlaylists(); break;
                case View.Songs: DrawSongs(); break;
                case View.Search: DrawSearch(); break;
            }
            DrawToast();
        }

        // ---------------- 输入 ----------------

        private static void HandleInput()
        {
            // 仅游戏窗口前台时响应键盘/鼠标（焦点在游戏外时按 F6/Enter 等不再误触发）
            if (!IsGameFocused()) return;

            // F6 开关（F7/F9 被 Chill Env Sync 占用）
            bool f6 = Key(VK_F6);
            if (f6 && !_keyF7Down) Toggle();
            _keyF7Down = f6;
            if (!_windowOpen) return;

            bool up = Key(VK_UP), down = Key(VK_DOWN), enter = Key(VK_RETURN), left = Key(VK_LEFT), right = Key(VK_RIGHT);
            if (up && !_keyUpDown) MoveSelection(-1);
            if (down && !_keyDownDown) MoveSelection(1);
            if (enter && !_keyEnterDown) Activate();
            if (left && !_keyLeftDown) GoBack();
            if (right && !_keyRightDown) Activate();
            _keyUpDown = up; _keyDownDown = down; _keyEnterDown = enter; _keyLeftDown = left; _keyRightDown = right;

            // 搜索视图：文本输入（ASCII 键 + Ctrl+V 粘贴 + Backspace）
            if (_view == View.Search)
            {
                HandleSearchTextInput();
            }

            // 鼠标
            bool lmb = Key(VK_LBUTTON);
            if (lmb && !_lmbDown)
            {
                if (TryGetClientPoint(out var p))
                {
                    HandleClick(p);
                }
            }
            _lmbDown = lmb;
        }

        private static bool Key(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>
        /// IMGUI 滚轮：鼠标悬停在面板列表区域时滚动列表（须在 OnGUI 上下文中调用，
        /// 因为 ScrollWheel 事件只能从 Event.current 拿到）。滚轮一格滚一行。
        /// </summary>
        private static void HandleScrollWheel()
        {
            var ev = Event.current;
            if (ev == null || ev.type != EventType.ScrollWheel) return;
            if (!TryGetClientPoint(out var mp)) return;
            if (!WindowRect.Contains(mp)) return;
            if (mp.y < WindowRect.y + HeaderH) return; // 标题/按钮区不滚动

            float dy = ev.delta.y;
            if (dy == 0f) return;
            int count = ListCount();
            if (count <= 0) return;

            // 一格滚一行（Unity 归一化 delta 通常 ±1；部分平台/DPI 下为 ±120 原生像素，归一化处理）
            int steps = Mathf.Abs(dy) >= 100f ? 1 : Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(dy)));
            // IMGUI 中滚轮 delta 与直觉相反：向上滚 delta.y<0 → offset 减小（看更早条目）
            int dir = dy < 0f ? -1 : 1;
            int maxOffset = Mathf.Max(0, count - ListVisibleLines());
            _scrollOffset = Mathf.Clamp(_scrollOffset + dir * steps, 0, maxOffset);
            // 选中项跟随滚动，始终可见
            _selected = Mathf.Clamp(_selected, _scrollOffset, Mathf.Max(_scrollOffset, count - 1));
            ev.Use();
        }

        /// <summary>搜索视图文本输入：ASCII 键、Ctrl+V 粘贴、Backspace 删除。</summary>
        private static void HandleSearchTextInput()
        {
            const int VK_BACK = 0x08, VK_CONTROL = 0x11, VK_V = 0x56;

            // Ctrl+V 粘贴（中文歌名：从剪贴板）
            if (Key(VK_CONTROL) && Key(VK_V) && !_keyCtrlVDown)
            {
                _keyCtrlVDown = true;
                var clip = GetClipboardText();
                if (!string.IsNullOrEmpty(clip))
                {
                    _searchQuery += clip.Trim();
                    if (_searchQuery.Length > 60) _searchQuery = _searchQuery.Substring(0, 60);
                    _searchDirty = true; // 词已变 → 下次 Enter 应搜索而不是播放旧结果
                }
                return;
            }
            if (!Key(VK_CONTROL) && !Key(VK_V)) _keyCtrlVDown = false;

            // Backspace
            bool bs = Key(VK_BACK);
            if (bs && !_keyBackDown && _searchQuery.Length > 0)
            {
                _searchQuery = _searchQuery.Substring(0, _searchQuery.Length - 1);
                _searchDirty = true;
            }
            _keyBackDown = bs;

            // ASCII 字符键（字母/数字/空格/常用符号），下降沿
            for (int vk = 0x30; vk <= 0x5A; vk++) // 0-9, A-Z
            {
                bool down = Key(vk);
                if (down && !_searchKeyDown[vk])
                {
                    char c = vk <= 0x39 ? (char)vk : (char)(vk + 32); // 数字原样，字母转小写
                    _searchQuery += c;
                    if (_searchQuery.Length > 60) _searchQuery = _searchQuery.Substring(0, 60);
                    _searchDirty = true;
                }
                _searchKeyDown[vk] = down;
            }
            bool space = Key(0x20);
            if (space && !_searchKeyDown[0x20])
            {
                _searchQuery += ' ';
                if (_searchQuery.Length > 60) _searchQuery = _searchQuery.Substring(0, 60);
                _searchDirty = true;
            }
            _searchKeyDown[0x20] = space;
        }

        private static void MoveSelection(int delta)
        {
            int count = ListCount();
            if (count <= 0) return;
            _selected = Mathf.Clamp(_selected + delta, 0, count - 1);
            int visible = ListVisibleLines();
            if (_selected < _scrollOffset) _scrollOffset = _selected;
            if (_selected >= _scrollOffset + visible) _scrollOffset = _selected - visible + 1;
        }

        private static int ListCount()
        {
            if (_view == View.Playlists)
            {
                var list = _showMine ? _mine : _collected;
                return list.Count;
            }
            if (_view == View.Songs) return _songs.Count;
            if (_view == View.Search) return _searchResults.Count;
            return 0; // Login 视图无列表
        }

        private static int VisibleLines()
        {
            return (int)((WindowRect.height - HeaderH - FooterH) / LineH);
        }

        /// <summary>搜索视图结果区顶部多了搜索框+提示（HeaderH+58），可视行数更少，单独计算。</summary>
        private static int SearchVisibleLines()
        {
            return Mathf.Max(1, (int)((WindowRect.height - FooterH - (HeaderH + 58f)) / LineH));
        }

        private static int ListVisibleLines()
        {
            return _view == View.Search ? SearchVisibleLines() : VisibleLines();
        }

        private static void Activate()
        {
            if (_view == View.Playlists)
            {
                OpenSelectedPlaylist();
            }
            else if (_view == View.Songs)
            {
                PlaySelectedSong();
            }
            else if (_view == View.Search)
            {
                // 词刚改过或没有结果 → Enter 是搜索；否则 Enter 是播放选中
                if (_searchDirty || _searchResults.Count == 0)
                {
                    DoSearch();
                }
                else
                {
                    PlaySearchResult();
                }
            }
            // Login 视图 Enter 无操作
        }

        private static void GoBack()
        {
            if (_view == View.Songs)
            {
                _view = View.Playlists;
                _selected = 0;
                _scrollOffset = 0;
                _viewSwitchTime = Time.unscaledTime;
            }
            else if (_view == View.Search)
            {
                _view = View.Playlists;
                _selected = 0;
                _scrollOffset = 0;
                _viewSwitchTime = Time.unscaledTime;
            }
        }

        private static void OpenSelectedPlaylist()
        {
            var list = _showMine ? _mine : _collected;
            if (_selected < 0 || _selected >= list.Count) return;
            var pl = list[_selected];
            LoadSongs(pl.Id);
        }

        private static void PlaySelectedSong()
        {
            if (_selected < 0 || _selected >= _songs.Count) return;

            if (PlaylistLink.InjectedPlaylistId != _currentPlaylistId)
            {
                // 首次播放该歌单：把整个歌单注入游戏原生播放列表
                // （游戏选歌列表 UI 自动显示网易云歌，切歌键指向它；播放走按需加载）
                PlaylistLink.InjectPlaylist(new List<SongInfo>(_songs), _selected);
                PlaylistLink.InjectedPlaylistId = _currentPlaylistId;
            }
            else
            {
                // 已注入：直接按索引播放（目标歌 AudioClip 未加载时自动按需加载后播放）
                var service = MusicServicePatches.CurrentInstance;
                if (service != null && service.CurrentPlayList != null && _selected < service.CurrentPlayList.Count)
                {
                    service.PlayMusicInPlaylist(_selected);
                }
            }
        }

        // ---------------- 搜索 ----------------

        /// <summary>进入搜索视图。</summary>
        private static void EnterSearch()
        {
            _view = View.Search;
            _selected = 0;
            _scrollOffset = 0;
            _searchDirty = false; // 新进入：输入为空，无脏状态
            _viewSwitchTime = Time.unscaledTime;
        }

        /// <summary>执行搜索（后台线程调 DLL，结果回填主线程）。</summary>
        private static void DoSearch()
        {
            var q = _searchQuery.Trim();
            if (q.Length == 0 || Plugin.Bridge == null) return;
            if (_searchLoading) return;
            _searchLoading = true;
            _searchError = null;
            _pendingSearchQuery = q;

            Task.Run(() =>
            {
                try { return Plugin.Bridge.SearchSongs(q, 30); }
                catch { return null; }
            }).ContinueWith(t =>
            {
                _searchLoading = false;
                if (t.Result == null)
                {
                    _searchError = "搜索失败（" + (Plugin.Bridge?.LastError() ?? "未知错误") + "）";
                    return;
                }
                _searchResults = t.Result;
                _selected = 0;
                _scrollOffset = 0;
                // 结果与当前输入一致才算"已搜索过"（期间用户又改词则保持脏，Enter 仍触发搜索）
                if (_searchQuery.Trim() == _pendingSearchQuery) _searchDirty = false;
                if (_searchResults.Count == 0) _searchError = "没有找到相关歌曲";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>播放搜索结果中的所选歌：整体注入（可切歌）+ 播放所选。</summary>
        private static void PlaySearchResult()
        {
            if (_selected < 0 || _selected >= _searchResults.Count) return;
            // 搜索结果当作一个"临时歌单"注入（可切歌），播放所选
            PlaylistLink.InjectPlaylist(new List<SongInfo>(_searchResults), _selected);
            PlaylistLink.InjectedPlaylistId = -1; // 搜索结果不是歌单，下次点歌单会重新注入
        }

        // ---------------- 登录二维码 ----------------

        /// <summary>打开登录视图时启动二维码登录流程。</summary>
        private static void StartQrFlow()
        {
            if (Plugin.Bridge == null || !Plugin.Bridge.IsInitialized) return;
            _qrHint = "正在获取登录二维码...";
            _qrUrl = null;
            if (_qrTexture != null) { UnityEngine.Object.Destroy(_qrTexture); _qrTexture = null; }

            Task.Run(() =>
            {
                try
                {
                    var state = Plugin.Bridge.StartQrLogin();
                    return state?.QrCodeUrl;
                }
                catch { return null; }
            }).ContinueWith(t =>
            {
                var url = t.Result;
                if (string.IsNullOrEmpty(url))
                {
                    _qrHint = "获取二维码失败，请重试";
                    return;
                }
                _qrUrl = url;
                _qrTexture = BuildQrTexture(url, 220);
                _qrHint = "请用网易云 App 扫一扫登录";
                _qrNextPollTime = 0f;
                _qrPolling = true;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>把二维码 URL 渲染成 Texture2D（QRCoder 矩阵 → 像素）。</summary>
        private static Texture2D BuildQrTexture(string url, int size)
        {
            try
            {
                var gen = new QRCodeGenerator();
                var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
                var matrix = data.ModuleMatrix; // List<List<bool>>
                int n = matrix.Count;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var scale = Mathf.Max(1, size / n);
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int mx = Mathf.Clamp(x / scale, 0, n - 1);
                        int my = Mathf.Clamp((size - 1 - y) / scale, 0, n - 1); // 上下翻转对齐
                        bool dark = matrix[my][mx];
                        pixels[y * size + x] = dark ? new Color(0f, 0f, 0f, 1f) : new Color(1f, 1f, 1f, 1f);
                    }
                }
                tex.SetPixels(pixels);
                tex.Apply();
                return tex;
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[Netease] 二维码生成失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>每帧轮询登录状态（在 Tick 里调用）。</summary>
        private static void PollQrLogin()
        {
            if (!_qrPolling || _windowOpen == false) return;
            if (_qrUrl == null) return;
            if (Time.unscaledTime < _qrNextPollTime) return;
            _qrNextPollTime = Time.unscaledTime + 2f; // 每 2 秒查一次

            Task.Run(() =>
            {
                try { return Plugin.Bridge.CheckQrLogin(); }
                catch { return null; }
            }).ContinueWith(t =>
            {
                var state = t.Result;
                if (state == null) { _qrHint = "查询失败，稍后重试"; return; }
                switch (state.StatusCode)
                {
                    case 800: _qrHint = "二维码已失效，正在重新获取..."; StartQrFlow(); break;
                    case 801: _qrHint = "请用网易云 App 扫一扫登录"; break;
                    case 802: _qrHint = "已扫码，请在手机上确认"; break;
                    case 803:
                        _qrHint = "登录成功！";
                        _qrPolling = false;
                        OnQrLoginSuccess();
                        break;
                    default: _qrHint = $"状态 {state.StatusCode}"; break;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>二维码登录成功：刷新登录态、加载歌单、进入歌单视图。</summary>
        private static void OnQrLoginSuccess()
        {
            try { Plugin.Bridge.RefreshLogin(); } catch { }
            Plugin.ResetAfterLogin();
            _view = View.Playlists;
            _selected = 0;
            _scrollOffset = 0;
            _viewSwitchTime = Time.unscaledTime;
            _qrUrl = null;
            _qrTexture = null;
            ShowToast("登录成功");
        }

        /// <summary>退出登录：清状态并回到登录视图。</summary>
        private static void LogoutAccount()
        {
            try
            {
                var ok = Plugin.Bridge != null && Plugin.Bridge.Logout();
                ShowToast(ok ? "已退出账号" : "退出失败");
            }
            catch { ShowToast("退出失败"); }

            // 清空本地数据
            _mine.Clear();
            _collected.Clear();
            _songs.Clear();
            _qrUrl = null;
            _qrTexture = null;
            _view = View.Login;
            _selected = 0;
            _scrollOffset = 0;
            _viewSwitchTime = Time.unscaledTime;
            StartQrFlow();
        }

        private static void HandleClick(Vector2 p)
        {
            // 隐藏按钮（右上角）
            if (p.x >= WindowRect.x + WindowRect.width - 72 && p.x <= WindowRect.x + WindowRect.width - 12 &&
                p.y >= WindowRect.y + 8 && p.y <= WindowRect.y + 34)
            {
                Toggle();
                return;
            }
            // 登出按钮（右上角）
            if (p.x >= WindowRect.x + WindowRect.width - 142 && p.x <= WindowRect.x + WindowRect.width - 82 &&
                p.y >= WindowRect.y + 8 && p.y <= WindowRect.y + 34)
            {
                LogoutAccount();
                return;
            }

            // 搜索按钮（歌单视图，右上角）
            if (_view == View.Playlists &&
                p.x >= WindowRect.x + WindowRect.width - 212 && p.x <= WindowRect.x + WindowRect.width - 152 &&
                p.y >= WindowRect.y + 8 && p.y <= WindowRect.y + 34)
            {
                EnterSearch();
                return;
            }

            // 搜索按钮（搜索视图，搜索框右侧）：点一下立即搜索
            if (_view == View.Search &&
                p.x >= WindowRect.x + WindowRect.width - 78 && p.x <= WindowRect.x + WindowRect.width - 12 &&
                p.y >= WindowRect.y + HeaderH + 4 && p.y <= WindowRect.y + HeaderH + 30)
            {
                DoSearch();
                return;
            }

            // 标题区 → 无操作；行区 → 选中+激活；返回按钮区（右上）
            // 注意：搜索视图的结果列表起点比普通列表低 58px（搜索框+提示条），行坐标必须按视图算
            float rowsTop = _view == View.Search ? WindowRect.y + HeaderH + 58f : WindowRect.y + HeaderH;
            int row = (int)((p.y - rowsTop) / LineH);
            if (row >= 0 && row < ListVisibleLines())
            {
                // 视图刚切换（如点击歌单进入歌曲列表）后的短暂窗口内忽略行点击，
                // 防止"双击歌单"的第二击落在新视图同一位置 → 误播放那一行
                if (Time.unscaledTime - _viewSwitchTime < 0.4f) return;
                int idx = _scrollOffset + row;
                if (idx < ListCount())
                {
                    _selected = idx;
                    if (_view == View.Search)
                    {
                        PlaySearchResult(); // 点哪行播哪行（不因脏状态改判为搜索）
                    }
                    else
                    {
                        Activate();
                    }
                }
            }
            // 返回按钮（左上角小区域）
            if (p.x >= WindowRect.x + 8 && p.x <= WindowRect.x + 56 && p.y >= WindowRect.y + 50 && p.y <= WindowRect.y + 76)
            {
                GoBack();
            }
        }

        // ---------------- 数据加载 ----------------

        private static void LoadSongs(long playlistId)
        {
            _currentPlaylistId = playlistId;
            _songsLoading = true;
            _songsLoadError = null;
            _songs.Clear();
            _selected = 0;
            _scrollOffset = 0;
            _view = View.Songs;
            _viewSwitchTime = Time.unscaledTime;

            Task.Run(() =>
            {
                try
                {
                    var songs = Plugin.Bridge?.GetPlaylistSongs(playlistId, true);
                    lock (_songs)
                    {
                        _songs.Clear();
                        if (songs != null) _songs.AddRange(songs);
                    }
                    _songsLoading = false;
                    // 预加载收藏状态
                    try
                    {
                        var likes = Plugin.Bridge?.GetLikeSongs(true);
                        if (likes != null)
                        {
                            lock (_likedIds) { _likedIds.Clear(); foreach (var s in likes) _likedIds.Add(s.Id); }
                        }
                    }
                    catch { }
                    Plugin.LogInfo($"[Netease] 歌单歌曲加载完成: {_songs.Count} 首");
                }
                catch (Exception ex)
                {
                    _songsLoading = false;
                    _songsLoadError = ex.Message;
                    Plugin.LogWarn("[Netease] 歌单歌曲加载失败: " + ex.Message);
                }
            });
        }

        /// <summary>由 Plugin 在歌单同步完成后调用（后台线程）。</summary>
        public static void OnPlaylistsLoaded(List<PlaylistInfo> all)
        {
            lock (_mine)
            {
                _mine.Clear();
                _collected.Clear();
                foreach (var p in all)
                {
                    if (p.IsMine) _mine.Add(p); else _collected.Add(p);
                }
            }
        }

        private static void ShowToast(string msg)
        {
            _toast = msg;
            _toastUntil = Time.unscaledTime + 2.5f;
        }

        // ---------------- 渲染 ----------------

        private static void DrawHeader()
        {
            var login = Plugin.Bridge != null && Plugin.Bridge.IsLoggedIn;
            string status = login ? "已登录" : "未登录";
            string nick = "";
            if (login)
            {
                var info = Plugin.Bridge.GetUserInfo();
                if (info != null && info.TryGetValue("nickname", out var n)) nick = n?.ToString() ?? "";
            }
            _titleStyle.normal.textColor = new Color(0.97f, 0.97f, 0.97f); // 白色标题
            GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + 8, 220, 24), "网易云音乐", _titleStyle);

            _smallStyle.normal.textColor = login ? new Color(0.2f, 0.8f, 0.4f) : new Color(0.9f, 0.3f, 0.3f);
            GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + 36, 260, 20), $"{status} {nick}".Trim(), _smallStyle);

            // 右上角按钮（点击走 Win32 鼠标区域，见 HandleClick）
            var btnStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter
            };
            btnStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
            GUI.Box(new Rect(WindowRect.x + WindowRect.width - 72, WindowRect.y + 8, 60, 26), "✕ 隐藏", btnStyle);
            if (login)
            {
                GUI.Box(new Rect(WindowRect.x + WindowRect.width - 142, WindowRect.y + 8, 60, 26), "登出", btnStyle);
            }
            if (_view == View.Playlists)
            {
                GUI.Box(new Rect(WindowRect.x + WindowRect.width - 212, WindowRect.y + 8, 60, 26), "搜索", btnStyle);
            }

            // 返回按钮（Songs / Search 视图）
            if (_view == View.Songs || _view == View.Search)
            {
                var backStyle = new GUIStyle(GUI.skin.button) { fontSize = 12 };
                if (GUI.Button(new Rect(WindowRect.x + 12, WindowRect.y + 54, 52, 24), "◀ 返回", backStyle))
                {
                    GoBack();
                }
                // 视图名
                if (_view == View.Songs)
                {
                    GUI.Label(new Rect(WindowRect.x + 72, WindowRect.y + 56, 200, 20), "歌曲列表（Enter 播放）", _smallStyle);
                }
            }
            else if (_view == View.Playlists)
            {
                GUI.Label(new Rect(WindowRect.x + 72, WindowRect.y + 56, 260, 20), "歌单（Enter 打开 · F6 关闭）", _smallStyle);
            }
            else
            {
                GUI.Label(new Rect(WindowRect.x + 72, WindowRect.y + 56, 260, 20), "扫码登录（F6 关闭）", _smallStyle);
            }

            if (MusicImporter.IsBusy && MusicImporter.Current != null)
            {
                var imp = MusicImporter.Current;
                string msg = imp.State switch
                {
                    MusicImporter.ImportState.FetchingUrl => "获取播放地址…",
                    MusicImporter.ImportState.Downloading => "加载中…",
                    _ => ""
                };
                if (msg.Length > 0)
                {
                    _smallStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
                    GUI.Label(new Rect(WindowRect.x + 220, WindowRect.y + 56, 210, 20), msg, _smallStyle);
                }
            }
        }

        // ---------------- 登录视图 ----------------

        private static void DrawLogin()
        {
            float cx = WindowRect.x + WindowRect.width / 2f;
            float top = WindowRect.y + HeaderH + 14f;

            if (_qrTexture != null)
            {
                float size = 200f;
                GUI.DrawTexture(new Rect(cx - size / 2f, top, size, size), _qrTexture);
                _smallStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
                GUI.Label(new Rect(WindowRect.x + 30, top + size + 12, WindowRect.width - 60, 22), _qrHint, _smallStyle);
            }
            else
            {
                _smallStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
                GUI.Label(new Rect(WindowRect.x + 30, top + 30, WindowRect.width - 60, 22),
                    string.IsNullOrEmpty(_qrHint) ? "正在获取二维码..." : _qrHint, _smallStyle);
                if (!string.IsNullOrEmpty(_qrUrl))
                {
                    GUI.Label(new Rect(WindowRect.x + 30, top + 56, WindowRect.width - 60, 40),
                        Truncate(_qrUrl, 58), _smallStyle);
                }
            }

            _smallStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
            GUI.Label(new Rect(WindowRect.x + 30, WindowRect.y + WindowRect.height - FooterH - 16,
                WindowRect.width - 60, 20), "扫码后自动进入歌单", _smallStyle);
        }

        private static void DrawPlaylists()
        {
            lock (_mine)
            {
                var list = _showMine ? _mine : _collected;
                float top = WindowRect.y + HeaderH;
                int visible = VisibleLines();
                for (int i = 0; i < visible; i++)
                {
                    int idx = _scrollOffset + i;
                    if (idx >= list.Count) break;
                    var pl = list[idx];
                    var rect = new Rect(WindowRect.x + 8, top + i * LineH, WindowRect.width - 16, LineH - 3);
                    DrawRow(rect, idx, $"{pl.Name}  [{pl.SongCount}首]", pl.CoverUrl != null);
                }
            }
        }

        private static void DrawSearch()
        {
            // 搜索框（右侧留出"搜索"按钮）
            var boxStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 4, 0, 0)
            };
            boxStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
            float boxX = WindowRect.x + 12;
            float boxW = WindowRect.width - 24 - 72; // 右侧 72px 留给按钮
            string qDisplay = _searchQuery + (Time.frameCount % 60 < 30 ? "▏" : "");
            GUI.Box(new Rect(boxX, WindowRect.y + HeaderH + 4, boxW, 26), qDisplay, boxStyle);

            // 搜索按钮（点击立即搜索；Enter 也智能触发，双保险）
            var btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            btnStyle.normal.textColor = new Color(0.95f, 0.9f, 0.8f);
            if (GUI.Button(new Rect(boxX + boxW + 6, WindowRect.y + HeaderH + 4, 66, 26), "搜索", btnStyle))
            {
                DoSearch();
            }

            _smallStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
            GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + HeaderH + 36, WindowRect.width - 24, 18),
                "输入后 Enter 搜索 · ↑↓ 选歌 Enter 播放 · ← 返回", _smallStyle);

            // 结果列表
            float top = WindowRect.y + HeaderH + 58;
            if (_searchLoading)
            {
                _smallStyle.normal.textColor = Color.gray;
                GUI.Label(new Rect(WindowRect.x + 12, top, 300, 20), "搜索中…", _smallStyle);
                return;
            }
            if (_searchError != null)
            {
                _smallStyle.normal.textColor = new Color(0.9f, 0.3f, 0.3f);
                GUI.Label(new Rect(WindowRect.x + 12, top, 400, 20), _searchError, _smallStyle);
                return;
            }
            int visible = ListVisibleLines();
            for (int i = 0; i < visible; i++)
            {
                int idx = _scrollOffset + i;
                if (idx >= _searchResults.Count) break;
                var s = _searchResults[idx];
                var rect = new Rect(WindowRect.x + 8, top + i * LineH, WindowRect.width - 16, LineH - 3);
                DrawRow(rect, idx, $"{s.Name}  -  {s.ArtistName}", true);
            }
        }

        private static void DrawSongs()
        {
            if (_songsLoading)
            {
                _smallStyle.normal.textColor = Color.gray;
                GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + HeaderH + 8, 300, 20), "加载歌曲中…", _smallStyle);
                return;
            }
            if (_songsLoadError != null)
            {
                _smallStyle.normal.textColor = new Color(0.9f, 0.3f, 0.3f);
                GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + HeaderH + 8, 400, 20), "加载失败: " + _songsLoadError, _smallStyle);
                return;
            }
            lock (_songs)
            {
                float top = WindowRect.y + HeaderH;
                int visible = VisibleLines();
                for (int i = 0; i < visible; i++)
                {
                    int idx = _scrollOffset + i;
                    if (idx >= _songs.Count) break;
                    var s = _songs[idx];
                    var rect = new Rect(WindowRect.x + 8, top + i * LineH, WindowRect.width - 16, LineH - 3);
                    bool liked = _likedIds.Contains(s.Id);
                    string label = $"{s.Name}  -  {s.ArtistName}" + (liked ? "  ♥" : "");
                    DrawRow(rect, idx, label, true);
                }
            }
        }

        private static void DrawRow(Rect rect, int idx, string label, bool enabled)
        {
            var style = idx == _selected ? _selectedStyle : _rowStyle;
            style.normal.textColor = idx == _selected
                ? new Color(1f, 0.85f, 0.2f)
                : enabled ? new Color(0.9f, 0.9f, 0.9f) : new Color(0.55f, 0.55f, 0.55f);
            string prefix = idx == _selected ? "▶ " : "  ";
            GUI.Box(rect, prefix + Truncate(label, 48), style);
        }

        private static void DrawToast()
        {
            if (_toast.Length == 0 || Time.unscaledTime > _toastUntil) return;
            _toastStyle.normal.textColor = new Color(0.5f, 0.9f, 0.6f);
            GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + WindowRect.height - 24, 420, 20), _toast, _toastStyle);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        // ---------------- Win32 坐标 ----------------

        private static bool TryGetClientPoint(out Vector2 point)
        {
            point = default;
            var hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetCursorPos(out var pt);
            if (!ScreenToClient(hwnd, ref pt)) return false;
            GetClientRect(hwnd, out var rc);
            if (rc.R <= 0 || rc.B <= 0) return false;
            point = new Vector2(pt.X * (Screen.width / (float)rc.R), pt.Y * (Screen.height / (float)rc.B));
            return true;
        }

        private static IntPtr FindGameWindow()
        {
            if (_gameHwnd != IntPtr.Zero && Time.frameCount - _hwndFindFrame < 600)
            {
                return _gameHwnd;
            }
            _gameHwnd = IntPtr.Zero;
            _hwndFindFrame = Time.frameCount;
            var pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out var wpid);
                if (wpid == pid) { _gameHwnd = h; return false; }
                return true;
            }, IntPtr.Zero);
            return _gameHwnd;
        }
    }

    /// <summary>
    /// IMGUI 渲染承载组件。本游戏不驱动插件对象生命周期，但 AddComponent 到活动场景
    /// GameObject 上的组件，其 OnGUI 消息会被 Unity 正常调用（ChillAI 浮层已验证）。
    /// </summary>
    public sealed class NeteaseUiRenderer : MonoBehaviour
    {
        public static NeteaseUiRenderer Instance { get; private set; }

        private void Awake()
        {
            if (Instance == null) Instance = this;
        }

        private void OnGUI()
        {
            NeteaseUi.Render();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
