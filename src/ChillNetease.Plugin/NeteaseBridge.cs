using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace ChillNetease.Plugin
{
    /// <summary>
    /// ChillNetease.dll（go-musicfox 编译的原生库，MIT）P/Invoke 桥接。
    /// 自写实现，签名与 ChillNetease.h 一致；所有返回 char* 的调用用 NeteaseFreeString 释放。
    /// </summary>
    public class NeteaseBridge
    {
        private const string DllName = "ChillNetease";
        private bool _initialized;

        // ---- P/Invoke（与 ChillNetease.h 导出一致）----
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int NeteaseInit(string dataDir);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NeteaseIsLoggedIn();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetUserInfo();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NeteaseRefreshLogin();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NeteaseLogout();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetLikeSongs(int getAll);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr NeteaseGetSongURL(long songId, string quality);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetSongLyric(long songId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetLastError();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void NeteaseFreeString(IntPtr ptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int NeteaseSetCookie(string cookieStr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NeteaseLikeSong(long songId, int like);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetPersonalFM();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NeteaseFMTrash(long songId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseQRGetKey();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseQRCheckStatus();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void NeteaseQRCancelLogin();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetUserPlaylists(int limit, int offset);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetPlaylistSongs(long playlistId, int getAll);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetPlaylistDetail(long playlistId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr NeteaseSearchPlaylistsByKeyword(string keyword);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr NeteaseSearchSongs(string keyword, string limit);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static string TakeString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            var s = Marshal.PtrToStringUTF8(ptr);
            NeteaseFreeString(ptr);
            return s;
        }

        /// <summary>把插件目录加入原生 DLL 搜索路径，并初始化桥接。dataDir 留空用 go-musicfox 默认路径（复用既有登录态）。</summary>
        public bool Initialize(string pluginDir, string dataDir = "")
        {
            try
            {
                SetDllDirectory(pluginDir);
            }
            catch
            {
                // 非 Windows 忽略
            }
            if (_initialized) return true;

            var rc = NeteaseInit(dataDir ?? "");
            _initialized = rc == 1;
            if (!_initialized)
            {
                Plugin.LogWarn($"[Netease] Init 失败 (code={rc}, err={LastError()})");
            }
            return _initialized;
        }

        public bool IsInitialized => _initialized;
        public bool IsLoggedIn => _initialized && NeteaseIsLoggedIn() == 1;

        public string LastError() => TakeString(NeteaseGetLastError());

        public Dictionary<string, object> GetUserInfo()
        {
            var json = TakeString(NeteaseGetUserInfo());
            return json == null ? null : JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
        }

        /// <summary>二维码登录：取 key 与二维码 URL。</summary>
        public QrLoginState StartQrLogin()
        {
            var json = TakeString(NeteaseQRGetKey());
            if (json == null) return null;
            try { return JsonConvert.DeserializeObject<QrLoginState>(json); }
            catch { return null; }
        }

        /// <summary>二维码登录：轮询扫码状态。</summary>
        public QrLoginState CheckQrLogin()
        {
            var json = TakeString(NeteaseQRCheckStatus());
            if (json == null) return null;
            try { return JsonConvert.DeserializeObject<QrLoginState>(json); }
            catch { return null; }
        }

        public void CancelQrLogin()
        {
            try { NeteaseQRCancelLogin(); } catch { }
        }

        /// <summary>退出登录（清登录态与 cookie）。</summary>
        public bool Logout()
        {
            try { return NeteaseLogout() == 1; }
            catch { return false; }
        }

        /// <summary>刷新登录态（扫码成功后调用，加载用户资料）。</summary>
        public bool RefreshLogin()
        {
            try { return NeteaseRefreshLogin() == 1; }
            catch { return false; }
        }

        /// <summary>用户歌单（创建+收藏），分页。</summary>
        public PlaylistsResponse GetUserPlaylists(int limit = 50, int offset = 0)
        {
            var json = TakeString(NeteaseGetUserPlaylists(limit, offset));
            if (json == null) return null;
            try { return JsonConvert.DeserializeObject<PlaylistsResponse>(json); }
            catch { return null; }
        }

        /// <summary>全部用户歌单（自动翻页），并标记是否为当前账号创建。</summary>
        public List<PlaylistInfo> GetAllUserPlaylists(long ownUserId)
        {
            var result = new List<PlaylistInfo>();
            int offset = 0;
            while (true)
            {
                var page = GetUserPlaylists(50, offset);
                if (page == null || page.Playlists == null || page.Playlists.Count == 0) break;
                foreach (var p in page.Playlists)
                {
                    p.IsMine = p.CreatorId == ownUserId;
                    result.Add(p);
                }
                offset += page.Playlists.Count;
                if (!page.HasMore) break;
                if (result.Count >= 300) break; // 保护
            }
            return result;
        }

        /// <summary>歌单内歌曲。</summary>
        public List<SongInfo> GetPlaylistSongs(long playlistId, bool getAll = true)
        {
            var json = TakeString(NeteaseGetPlaylistSongs(playlistId, getAll ? 1 : 0));
            if (json == null) return null;
            try { return JsonConvert.DeserializeObject<List<SongInfo>>(json); }
            catch { return null; }
        }

        /// <summary>全站搜索单曲（go-musicfox SearchService，Type=1）。</summary>
        public List<SongInfo> SearchSongs(string keyword, int limit = 30)
        {
            var json = TakeString(NeteaseSearchSongs(keyword, limit.ToString()));
            if (json == null) return null;
            try
            {
                var resp = JsonConvert.DeserializeObject<SearchResponse>(json);
                if (resp?.Result?.Songs == null || resp.Result.Songs.Count == 0) return new List<SongInfo>();
                return resp.Result.Songs.Select(s => new SongInfo
                {
                    Id = s.Id,
                    Name = s.Name,
                    Duration = s.Dt / 1000.0,
                    Artists = s.Ar?.Select(a => a.Name).ToList(),
                    Album = s.Al?.Name,
                    AlbumId = s.Al?.Id ?? 0,
                    CoverUrl = s.Al?.PicUrl
                }).ToList();
            }
            catch { return null; }
        }

        /// <summary>播放 URL（quality: standard/higher/exhigh/lossless/...）。</summary>
        public SongUrl GetSongUrl(long songId, string quality = "exhigh")
        {
            var json = TakeString(NeteaseGetSongURL(songId, quality));
            if (json == null) return null;
            try { return JsonConvert.DeserializeObject<SongUrl>(json); }
            catch { return null; }
        }

        /// <summary>收藏歌曲列表。</summary>
        public List<SongInfo> GetLikeSongs(bool getAll = true)
        {
            var json = TakeString(NeteaseGetLikeSongs(getAll ? 1 : 0));
            if (json == null) return null;
            try { return JsonConvert.DeserializeObject<List<SongInfo>>(json); }
            catch { return null; }
        }

        /// <summary>收藏/取消收藏。like=true 收藏。</summary>
        public bool SetLike(long songId, bool like)
        {
            return NeteaseLikeSong(songId, like ? 1 : 0) == 1;
        }

        public string GetSongLyric(long songId) => TakeString(NeteaseGetSongLyric(songId));

        /// <summary>音质枚举 → quality 字符串。</summary>
        public static string QualityToString(AudioQuality q) => q switch
        {
            AudioQuality.Standard => "standard",
            AudioQuality.Higher => "higher",
            AudioQuality.ExHigh => "exhigh",
            AudioQuality.Lossless => "lossless",
            AudioQuality.HiRes => "hires",
            AudioQuality.JYEffect => "jyeffect",
            AudioQuality.Sky => "sky",
            AudioQuality.JYMaster => "jymaster",
            _ => "exhigh"
        };
    }
}
