using System;
using System.Threading.Tasks;
using Bulbul;
using UnityEngine;
using UnityEngine.Networking;

namespace ChillNetease.Plugin
{
    /// <summary>
    /// 网易云歌曲 → 游戏原生播放器的导入器。
    /// 联网播放路径（不落盘）：
    ///   后台线程 GetSongURL（P/Invoke 会阻塞，放线程池）
    ///   → 主线程 UnityWebRequest 流式加载 AudioClip（streamAudio=true 边下边播）
    ///   → GameAudioInfo.CreateNormal(clip)（AudioClip 非空 → 游戏 GetAudioClip 直接返回，可播）
    ///   → MusicService.AddMusicItem（绕过 100 首限制）→ PlayMusicInPlaylist
    /// 采用手动状态机推进（本游戏不驱动插件协程，Tick 由 Harmony 每帧驱动）。
    /// </summary>
    public static class MusicImporter
    {
        public enum ImportState { FetchingUrl, Downloading, Ready, Failed }

        public sealed class PendingImport
        {
            public SongInfo Song;
            public string Url;
            public string Quality;
            public ImportState State = ImportState.FetchingUrl;
            public string Error;
            public UnityWebRequest Request;
            public AudioClip Clip;
            public GameAudioInfo Audio;
            public bool IsTrial;
            public DateTime StartedAtUtc = DateTime.UtcNow;

            /// <summary>按需加载模式：非空时加载完成把 AudioClip 填回该曲目对象（歌单注入用）。</summary>
            public GameAudioInfo TargetAudio;
            /// <summary>按需加载完成回调（通常用于触发继续播放）。</summary>
            public Action OnReady;

            private Task<SongUrl> _urlTask;
            private int _urlRetries; // 获取播放地址的重试次数（网易云接口偶发限流，作者有 3 次重试先例）

            /// <summary>启动后台取 URL（线程池，避免阻塞主线程）。</summary>
            public void StartFetchUrl(NeteaseBridge bridge, string quality)
            {
                Quality = quality;
                State = ImportState.FetchingUrl;
                _urlTask = Task.Run(() => bridge.GetSongUrl(Song.Id, quality));
            }

            public void PumpFetchUrl()
            {
                if (_urlTask == null) return;
                if (!_urlTask.IsCompleted)
                {
                    if ((DateTime.UtcNow - StartedAtUtc).TotalSeconds > 30)
                    {
                        State = ImportState.Failed;
                        Error = "获取播放地址超时";
                        Plugin.LogWarn("[Netease] 导入失败: 获取播放地址超时（" + Song.Name + "）");
                    }
                    return;
                }
                SongUrl url;
                try
                {
                    url = _urlTask.Result;
                }
                catch (Exception ex)
                {
                    _urlTask = null;
                    State = ImportState.Failed;
                    Error = "获取播放地址异常: " + ex.Message;
                    Plugin.LogWarn("[Netease] 导入失败: " + Error);
                    return;
                }
                _urlTask = null;
                if (url == null || string.IsNullOrEmpty(url.Url))
                {
                    // 网易云播放地址接口偶发失败/限流 → 重试（最多 3 次，间隔 1 秒）
                    var lastErr = Plugin.Bridge?.LastError() ?? "";
                    if (_urlRetries < 3)
                    {
                        _urlRetries++;
                        Plugin.LogWarn($"[Netease] 获取播放地址失败，重试 {_urlRetries}/3（{Song.Name}）err={lastErr}");
                        _urlTask = Task.Delay(1000).ContinueWith(_ => Plugin.Bridge.GetSongUrl(Song.Id, Quality));
                        return;
                    }
                    State = ImportState.Failed;
                    Error = "获取播放地址失败（重试3次）: " + lastErr;
                    Plugin.LogWarn("[Netease] 导入失败: " + Error);
                    return;
                }
                IsTrial = url.IsTrial;
                // 网易云返回 http:// 明文 URL，本游戏构建禁止非安全 HTTP → 统一升级为 https（CDN 双协议支持）
                Url = url.Url.Replace("http://", "https://");
                State = ImportState.Downloading;
                Plugin.LogInfo($"[Netease] 已获取播放地址: {Song.Name} [{url.Type ?? "mp3"}] {(url.IsTrial ? "（试听）" : "")}");
            }

            /// <summary>主线程创建流式下载（必须在主线程调用一次）。</summary>
            public void StartDownload()
            {
                try
                {
                    Plugin.LogInfo("[Netease] 开始流式加载: " + Song.Name);
                    var handler = new DownloadHandlerAudioClip(Url, ResolveAudioType(Url))
                    {
                        streamAudio = true // 边下边播，不落盘
                    };
                    Request = new UnityWebRequest(Url, UnityWebRequest.kHttpVerbGET)
                    {
                        downloadHandler = handler
                    };
                    Request.SendWebRequest();
                }
                catch (Exception ex)
                {
                    State = ImportState.Failed;
                    Error = "创建下载请求失败: " + ex.Message;
                    Plugin.LogWarn("[Netease] 导入失败: " + Error);
                }
            }

            public void PumpDownload()
            {
                if (Request == null) return;
                if (!Request.isDone)
                {
                    if ((DateTime.UtcNow - StartedAtUtc).TotalSeconds > 90)
                    {
                        Request.Dispose();
                        Request = null;
                        State = ImportState.Failed;
                        Error = "下载超时";
                    }
                    return;
                }
                if (Request.result != UnityWebRequest.Result.Success)
                {
                    Error = "下载失败: " + Request.error;
                    Request.Dispose();
                    Request = null;
                    State = ImportState.Failed;
                    Plugin.LogWarn("[Netease] 导入失败: " + Error);
                    return;
                }
                Clip = DownloadHandlerAudioClip.GetContent(Request);
                Request.Dispose();
                Request = null;
                State = Clip != null ? ImportState.Ready : ImportState.Failed;
                if (Clip == null)
                {
                    Error = "音频解码失败";
                    Plugin.LogWarn("[Netease] 导入失败: " + Error + "（" + Song.Name + "）");
                }
                else
                {
                    Plugin.LogInfo($"[Netease] 音频加载完成: {Song.Name}（{Clip.length:0}s）");
                }
            }

            public void Dispose()
            {
                try { Request?.Dispose(); } catch { }
                try { if (Clip != null) UnityEngine.Object.Destroy(Clip); } catch { }
            }
        }

        /// <summary>当前正在导入的请求（单飞，简单起见同一时间只导一首）。</summary>
        public static PendingImport Current;

        /// <summary>最近一次失败信息（供 UI 提示）。</summary>
        public static string LastFailure;

        public static bool IsBusy => Current != null;

        /// <summary>开始导入一首歌（联网流式，单曲模式：加载完成 AddMusicItem + 播放）。</summary>
        public static void StartImport(SongInfo song, AudioQuality quality = AudioQuality.ExHigh)
        {
            if (Plugin.Bridge == null || !Plugin.Bridge.IsLoggedIn) return;
            if (Current != null) return; // 已有导入进行中

            Current = new PendingImport { Song = song };
            Current.StartFetchUrl(Plugin.Bridge, NeteaseBridge.QualityToString(quality));
            Plugin.LogInfo($"[Netease] 开始导入: {song.Name} - {song.ArtistName}");
        }

        /// <summary>
        /// 按需加载模式：把歌曲流式加载进 <paramref name="target"/> 的 AudioClip（歌单注入用）。
        /// 若已有加载进行中（快速切歌），作废旧请求。
        /// </summary>
        public static void StartLoadInto(GameAudioInfo target, SongInfo song, Action onReady)
        {
            if (Plugin.Bridge == null || !Plugin.Bridge.IsLoggedIn) return;
            if (Current != null)
            {
                try { Current.Dispose(); } catch { }
                Current = null;
            }

            Current = new PendingImport { Song = song, TargetAudio = target, OnReady = onReady };
            Current.StartFetchUrl(Plugin.Bridge, NeteaseBridge.QualityToString(AudioQuality.ExHigh));
            Plugin.LogInfo($"[Netease] 开始按需加载: {song.Name} - {song.ArtistName}");
        }

        /// <summary>每帧推进（由 Harmony Tick 调用，主线程）。</summary>
        public static void Pump()
        {
            var imp = Current;
            if (imp == null) return;

            switch (imp.State)
            {
                case ImportState.FetchingUrl:
                    imp.PumpFetchUrl();
                    break;
                case ImportState.Downloading:
                    if (imp.Request == null)
                    {
                        imp.StartDownload(); // 主线程创建 UWR
                    }
                    else
                    {
                        imp.PumpDownload();
                    }
                    break;
                case ImportState.Ready:
                    FinishImport(imp);
                    break;
                case ImportState.Failed:
                    // 关键修复：失败必须释放槽位，否则后续点歌全部被拒
                    LastFailure = $"{imp.Song?.Name}: {imp.Error}";
                    Plugin.LogWarn($"[Netease] 导入失败（已释放）: {LastFailure}");
                    imp.Dispose();
                    Current = null;
                    break;
            }
        }

        private static void FinishImport(PendingImport imp)
        {
            try
            {
                // 关键：给 AudioClip 设置名称——游戏原生 GetCurrentMusicProgress 依据
                // PlayingMusic.AudioClipName（=clip.name）判断是否有进度，为空则进度条空、切歌失效
                if (imp.Clip != null && string.IsNullOrEmpty(imp.Clip.name))
                {
                    imp.Clip.name = "netease_" + imp.Song.Id;
                }

                // 按需加载模式（歌单注入）：填回目标曲目对象，不重复 AddMusicItem
                if (imp.TargetAudio != null)
                {
                    if (imp.Clip == null)
                    {
                        imp.Error = "音频解码失败";
                        imp.State = ImportState.Failed;
                        Plugin.LogWarn("[Netease] 按需加载失败: " + imp.Error + "（" + imp.Song.Name + "）");
                        return;
                    }
                    imp.TargetAudio.AudioClip = imp.Clip;
                    Plugin.LogInfo($"[Netease] ✅ 已加载并继续播放: {imp.Song.Name}（{imp.Clip.length:0}s）");
                    imp.OnReady?.Invoke();
                    imp.State = ImportState.Ready;
                    return;
                }

                // 构造游戏原生曲目对象（AudioClip 已就绪 → GetAudioClip 直接返回）
                var audio = GameAudioInfo.CreateNormal(
                    imp.Clip, AudioTag.Other,
                    imp.Song.Name, imp.Song.ArtistName,
                    "netease_" + imp.Song.Id,
                    true, null, null);
                imp.Audio = audio;

                var service = MusicServicePatches.CurrentInstance;
                if (service == null)
                {
                    imp.Error = "MusicService 实例未就绪";
                    imp.State = ImportState.Failed;
                    Plugin.LogWarn("[Netease] MusicService 实例为空，无法导入");
                    return;
                }

                if (!service.AddMusicItem(audio))
                {
                    // 可能已存在（UUID 重复，首次导入后再次点播）→ 在现有播放列表里找到并直接播放
                    var existingList = service.CurrentPlayList;
                    if (existingList != null)
                    {
                        for (int i = 0; i < existingList.Count; i++)
                        {
                            if (existingList[i].UUID == audio.UUID)
                            {
                                service.PlayMusicInPlaylist(i);
                                Plugin.LogInfo($"[Netease] ✅ 已在列表中，直接播放: {imp.Song.Name}（#{i}）");
                                imp.State = ImportState.Ready;
                                return;
                            }
                        }
                    }
                    imp.Error = "AddMusicItem 返回 false（可能已存在且不在当前列表）";
                    imp.State = ImportState.Failed;
                    Plugin.LogWarn($"[Netease] 导入失败: {imp.Error}");
                    return;
                }

                // 播放刚加入的歌曲（CurrentPlayList 末尾）
                var list = service.CurrentPlayList;
                if (list != null && list.Count > 0)
                {
                    service.PlayMusicInPlaylist(list.Count - 1);
                }

                Plugin.LogInfo($"[Netease] ✅ 已导入并播放: {imp.Song.Name}（音质 {imp.Quality}" +
                    (imp.IsTrial ? "，试听片段" : "") + "）");
                imp.State = ImportState.Ready;
            }
            catch (Exception ex)
            {
                imp.Error = ex.Message;
                imp.State = ImportState.Failed;
                Plugin.LogWarn("[Netease] 导入异常: " + ex);
            }
            finally
            {
                Current = null; // 释放单飞槽位
                if (imp.State == ImportState.Failed)
                {
                    Plugin.LogWarn($"[Netease] 导入失败: {imp.Error}");
                }
            }
        }

        private static AudioType ResolveAudioType(string url)
        {
            var lower = url.ToLowerInvariant();
            if (lower.Contains(".m4a") || lower.Contains(".aac")) return AudioType.ACC;
            if (lower.Contains(".wav")) return AudioType.WAV;
            if (lower.Contains(".ogg")) return AudioType.OGGVORBIS;
            return AudioType.MPEG; // 默认 mp3
        }
    }
}
