using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace NeteaseProbe
{
    /// <summary>
    /// M1 验证工具：确认 ChillNetease.dll（go-musicfox 编译的原生库）能脱离 ChillPatcher 独立工作。
    /// 流程：Init → 登录态检测 → （未登录则二维码登录）→ 拉取用户歌单列表。
    /// </summary>
    public static class Program
    {
        private const string DllName = "ChillNetease";

        // ---- P/Invoke（签名与 ChillNetease.h 一致）----
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int NeteaseInit(string dataDir);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int NeteaseIsLoggedIn();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetUserInfo();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetLastError();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void NeteaseFreeString(IntPtr ptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseQRGetKey();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseQRCheckStatus();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void NeteaseQRCancelLogin();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetUserPlaylists(int limit, int offset);

        private static string PtrToString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            var s = Marshal.PtrToStringUTF8(ptr);
            NeteaseFreeString(ptr);
            return s;
        }

        public static int Main(string[] args)
        {
            // DLL 加载目录：当第一个参数是 "probe" 时用工具同目录，否则视为 DLL 目录
            var dllDir = (args.Length > 0 && args[0] == "probe")
                ? AppDomain.CurrentDomain.BaseDirectory
                : (args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory);
            var dllPath = Path.Combine(dllDir, "ChillNetease.dll");
            if (!File.Exists(dllPath))
            {
                Console.WriteLine($"[FAIL] 找不到 {dllPath}");
                return 1;
            }
            SetDllDirectory(dllDir);

            // 子命令模式：probe songs <playlistId> / probe url <songId>
            if (args.Length >= 2 && args[0] == "probe")
            {
                var rc = NeteaseInit("");
                if (rc != 1)
                {
                    Console.WriteLine($"[FAIL] Init: {GetLastError()}");
                    return 1;
                }
                return args[1] switch
                {
                    "songs" => ProbePlaylistSongs(long.Parse(args[2])),
                    "url" => ProbeSongUrl(long.Parse(args[2]), args.Length > 3 ? args[3] : "exhigh"),
                    "like" => ProbeLikeList(),
                    "qrlogin" => DoQRLogin() ? 0 : 1,
                    "weapi_key" => ProbeWeapiKey(),
                    "weapi_url" => ProbeWeapiUrl(args.Length > 2 ? args[2] : ""),
                    "search" => ProbeWeapiSearch(args.Length > 2 ? args[2] : ""),
                    _ => 1
                };
            }

            Console.WriteLine($"=== ChillNetease.dll 独立验证（{Path.GetFullPath(dllPath)}）===");

            // 1. 初始化（空串 = go-musicfox 默认配置目录，可复用既有登录态）
            Console.Write("NeteaseInit(\"\") ... ");
            var init = NeteaseInit("");
            Console.WriteLine(init == 1 ? "✅ OK" : $"❌ FAIL (code={init}, err={GetLastError()})");
            if (init != 1) return 1;

            // 2. 登录态检测
            var loggedIn = NeteaseIsLoggedIn() == 1;
            Console.WriteLine($"已登录: {(loggedIn ? "✅ 是" : "❌ 否")}");

            if (!loggedIn)
            {
                var ok = DoQRLogin();
                if (!ok) return 1;
            }

            // 3. 用户信息
            var info = PtrToString(NeteaseGetUserInfo());
            Console.WriteLine($"用户信息: {info}");

            // 4. 用户歌单（创建+收藏）
            Console.WriteLine("\n=== 用户歌单（GetUserPlaylists limit=50 offset=0）===");
            var playlists = PtrToString(NeteaseGetUserPlaylists(50, 0));
            Console.WriteLine(playlists ?? "(null)");

            Console.WriteLine("\n验证完成");
            return 0;
        }

        private static bool DoQRLogin()
        {
            Console.WriteLine("\n=== 二维码登录 ===");
            var keyJson = PtrToString(NeteaseQRGetKey());
            if (keyJson == null)
            {
                Console.WriteLine($"[FAIL] QRGetKey 失败: {GetLastError()}");
                return false;
            }
            Console.WriteLine($"QR key: {keyJson}");

            // 提取 qrcodeUrl（JsonConvert 自动处理 \u0026 等 JSON 转义）
            string url = null;
            try
            {
                var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object>>(keyJson);
                url = parsed != null && parsed.TryGetValue("qrcodeUrl", out var u) ? u?.ToString() : null;
            }
            catch { }
            if (url != null)
            {
                Console.WriteLine($"请用网易云 App 扫码: {url}");
                TrySaveQrPng(url);
            }

            Console.WriteLine("等待扫码（轮询 QRCheckStatus，最多 120 秒）...");
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2000);
                var statusJson = PtrToString(NeteaseQRCheckStatus());
                Console.WriteLine($"  status: {statusJson}");
                if (statusJson == null) continue;
                var code = ExtractIntField(statusJson, "statusCode") ?? -1;
                if (code == 803)
                {
                    Console.WriteLine("✅ 扫码成功，已登录！");
                    return true;
                }
                if (code == 800)
                {
                    Console.WriteLine("❌ 二维码已失效，请重试");
                    return false;
                }
            }
            Console.WriteLine("❌ 超时未扫码");
            NeteaseQRCancelLogin();
            return false;
        }

        private static int ProbePlaylistSongs(long playlistId)
        {
            Console.WriteLine($"=== 歌单歌曲 GetPlaylistSongs({playlistId}) ===");
            var ptr = NeteaseGetPlaylistSongs(playlistId, 1);
            var json = PtrToString(ptr);
            Console.WriteLine(json ?? "(null, err=" + GetLastError() + ")");
            return 0;
        }

        private static int ProbeSongUrl(long songId, string quality)
        {
            Console.WriteLine($"=== 播放 URL GetSongURL({songId}, {quality}) ===");
            var ptr = NeteaseGetSongURL(songId, quality);
            var json = PtrToString(ptr);
            Console.WriteLine(json ?? "(null, err=" + GetLastError() + ")");
            return 0;
        }

        /// <summary>weapi：发起二维码登录，返回 unikey 并轮询（用户扫码后保存 cookie）。</summary>
        private static int ProbeWeapiKey()
        {
            Console.WriteLine("=== weapi 二维码登录 ===");
            var resp = NeteaseWeapi.GetQrKey();
            Console.WriteLine("qr/key 响应: " + resp);
            var uniKey = ExtractField(resp, "unikey") ?? ExtractField(resp, "uniKey");
            if (string.IsNullOrEmpty(uniKey))
            {
                Console.WriteLine("[FAIL] 未拿到 unikey");
                return 1;
            }
            Console.WriteLine($"unikey: {uniKey}");
            Console.WriteLine("请用网易云 App 扫码登录...（轮询 120 秒）");

            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(2500);
                var check = NeteaseWeapi.CheckQr(uniKey);
                Console.WriteLine("  qr/check: " + check);
                if (check.Contains("\"code\":803") || check.Contains("\\\"code\\\":803") || ExtractIntField(check, "code") == 803)
                {
                    // 登录成功：完整 cookie 在响应头里？weapi 的 Set-Cookie 会由 WebClient 自动处理，
                    // 但 WebClient 不持久化跨请求。需要从响应头拿 Set-Cookie。
                    Console.WriteLine("✅ 扫码成功！但 cookie 需要从响应头获取（待实现）");
                    return 0;
                }
                if (ExtractIntField(check, "code") == 800)
                {
                    Console.WriteLine("❌ 二维码失效");
                    return 1;
                }
            }
            Console.WriteLine("❌ 超时");
            return 1;
        }

        /// <summary>weapi：用已保存 cookie 拿播放 URL。</summary>
        private static int ProbeWeapiSearch(string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) { Console.WriteLine("用法: probe search <关键词>"); return 1; }
            Console.WriteLine($"=== DLL 搜索单曲: {keyword} ===");
            // 需先初始化（登录态）
            if (NeteaseInit("") != 1) { Console.WriteLine("[FAIL] Init"); return 1; }
            var ptr = NeteaseSearchSongs(keyword, "30");
            var json = PtrToString(ptr);
            Console.WriteLine(json == null ? "(null, err=" + GetLastError() + ")" : json.Substring(0, Math.Min(json.Length, 900)));
            return 0;
        }

        private static string LoadMusicFoxCookie()
        {
            try
            {
                var path = System.IO.Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "go-musicfox", "cookie");
                if (!System.IO.File.Exists(path)) return null;
                var kv = new List<string>();
                foreach (var ln in System.IO.File.ReadAllLines(path))
                {
                    if (ln.StartsWith("#") || string.IsNullOrWhiteSpace(ln)) continue;
                    var p = ln.Split('\t');
                    if (p.Length >= 7) kv.Add(p[5] + "=" + p[6]);
                }
                return string.Join("; ", kv);
            }
            catch { return null; }
        }

        private static int ProbeWeapiUrl(string songId)
        {
            var cookie = NeteaseWeapi.LoadCookie();
            if (string.IsNullOrEmpty(cookie))
            {
                Console.WriteLine("[FAIL] 没有 cookie（先运行 weapi 登录）");
                return 1;
            }
            Console.WriteLine($"=== weapi 播放 URL（song {songId}）===");
            var resp = NeteaseWeapi.GetSongUrl("[" + songId + "]", "exhigh", cookie);
            Console.WriteLine(resp);
            return 0;
        }

        private static int ProbeLikeList()
        {
            Console.WriteLine("=== 收藏歌曲 GetLikeSongs(getAll=1) ===");
            var ptr = NeteaseGetLikeSongs(1);
            var json = PtrToString(ptr);
            Console.WriteLine(json == null ? "(null, err=" + GetLastError() + ")" : json.Substring(0, Math.Min(json.Length, 500)));
            return 0;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetLikeSongs(int getAll);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetPlaylistSongs(long playlistId, int getAll);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr NeteaseSearchSongs(string keyword, string limit);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr NeteaseGetSongURL(long songId, string quality);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NeteaseGetLikeList();

        private static string GetLastError() => PtrToString(NeteaseGetLastError());

        private static string ExtractField(string json, string field)
        {
            var marker = "\"" + field + "\"";
            var idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var colon = json.IndexOf(':', idx + marker.Length);
            if (colon < 0) return null;
            var start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length) return null;
            if (json[start] == '"')
            {
                var end = json.IndexOf('"', start + 1);
                return end > start ? json.Substring(start + 1, end - start - 1) : null;
            }
            var comma = json.IndexOfAny(new[] { ',', '}' }, start);
            return comma > start ? json.Substring(start, comma - start).Trim() : json.Substring(start).Trim();
        }

        private static int? ExtractIntField(string json, string field)
        {
            var s = ExtractField(json, field);
            return int.TryParse(s, out var v) ? v : (int?)null;
        }

        /// <summary>尝试用 Python qrcode 生成二维码 PNG（失败不阻塞）。</summary>
        private static void TrySaveQrPng(string url)
        {
            try
            {
                var py = "python"; // 需在 PATH 中且装有 qrcode 库；失败静默跳过（仍可用 URL 扫码）
                var script = $"import qrcode; qrcode.make('{url.Replace("'", "\\'")}').save(r'{Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qr.png")}')";
                System.Diagnostics.Process.Start(py, $"-c \"{script.Replace("\"", "\\\"")}\"");
            }
            catch
            {
                // 忽略：无 qrcode 库也能用 URL 扫码
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}
