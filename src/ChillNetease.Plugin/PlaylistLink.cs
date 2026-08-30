using System;
using System.Collections.Generic;
using Bulbul;
using HarmonyLib;
using R3;

namespace ChillNetease.Plugin
{
    /// <summary>
    /// 网易云歌单 ↔ 游戏原生播放列表的联动核心。
    ///
    /// 原理（IL 反编译确认）：
    /// - 游戏音乐设施打开时 FacilityMusic.Setup 把 MusicService.CurrentPlayList
    ///   （ObservableCollections.ObservableList&lt;GameAudioInfo&gt;）直接绑定给选歌列表 UI
    ///   （IMusicListUI.Setup(CurrentPlayList, facilityMusic)）——往里增删歌，游戏 UI 自动刷新。
    /// - 切歌（GetNextGameAudio）从 CurrentPlayList 循环取歌 —— 列表里有什么，切歌就能切到什么。
    ///
    /// 因此联动方案 = 元数据注入 + 播放时按需加载：
    /// 1. 打开网易云歌单 → 把整单歌曲（AudioClip=null 的元数据对象）填入 CurrentPlayList，
    ///    游戏原生选歌列表立即显示网易云歌单，切歌键指向它。
    /// 2. 游戏内点歌/切歌走到播放入口时，若目标网易云歌 AudioClip 为空，
    ///    拦截 → 流式加载 AudioClip → 填回该曲目对象 → 继续播放（AudioClip 就绪后放行）。
    ///
    /// 大歌单保护（用户反馈数千首歌掉帧）：
    /// - 注入用 ObservableList.AddRange 一次性填充（游戏自己的标签筛选重建就是这么做的），
    ///   避免 Clear+逐首 Add 产生数千次集合变更事件。
    /// - 游戏播放列表只注入前 MaxInjectedSongs 首（注入窗口），其余曲目保留在 _fullSongs；
    ///   播放/切歌接近窗口末尾时自动扩窗（TryExtendWindow），随机模式直接在全量歌单中随机。
    ///   这样游戏侧任何随列表规模增长的开销都有上限，而面板里仍能浏览/点播全部歌曲。
    /// </summary>
    public static class PlaylistLink
    {
        /// <summary>netease_歌曲ID → SongInfo（按需加载时查回歌曲元数据）。</summary>
        public static readonly Dictionary<string, SongInfo> UuidSongCache = new Dictionary<string, SongInfo>();

        /// <summary>当前注入的曲目对象（netease_歌曲ID → GameAudioInfo，填回 AudioClip 用）。</summary>
        public static readonly Dictionary<string, GameAudioInfo> UuidAudioMap = new Dictionary<string, GameAudioInfo>();

        /// <summary>当前注入的游戏播放列表对应的网易云歌单 id（-1 = 未注入，由调用方维护）。</summary>
        public static long InjectedPlaylistId = -1;

        /// <summary>
        /// 最近一次播放的列表索引（插件自己追踪，不依赖游戏 PlayingMusic 匹配——
        /// 游戏内部对象/UUID 可能与我们注入的对象对不上，导致切歌位置计算错误）。
        /// </summary>
        public static int LastPlayedIndex = -1;

        /// <summary>当前注入歌单的完整曲目（游戏播放列表只注入前窗口数量，其余按需扩窗）。</summary>
        private static List<SongInfo> _fullSongs = new List<SongInfo>();

        /// <summary>已注入进游戏播放列表的窗口数量（BuildItems/扩窗的记账值）。</summary>
        private static int _injectedCount;

        /// <summary>每次扩窗追加的曲目数。</summary>
        private const int ExtendChunk = 500;

        /// <summary>已注入窗口内的曲目数（对外只读）。</summary>
        public static int InjectedCount => _injectedCount;

        /// <summary>记录最近播放索引（在 PlayMusicInPlaylist 补丁里调用）。</summary>
        public static void NotePlayed(int index) => LastPlayedIndex = index;

        /// <summary>注入窗口上限（BepInEx 配置；0 = 不限制）。初始注入受它约束，用户显式点播时扩窗可越过。</summary>
        private static int WindowLimit => Math.Max(0, Plugin.MaxInjectedSongs?.Value ?? 1000);

        /// <summary>Fisher-Yates 原地打乱（随机播放列表用）。</summary>
        public static void ShuffleList<T>(List<T> list)
        {
            var rng = new Random();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>统计游戏播放列表中的网易云曲目数（窗口记账用，切歌时调用一次，开销可忽略）。</summary>
        private static int CountNetease(System.Collections.Generic.IList<GameAudioInfo> list)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (IsNetease(list[i])) n++;
            }
            return n;
        }

        /// <summary>把 _fullSongs[from, to) 构造成元数据曲目对象（AudioClip 为空，播放时按需加载）。</summary>
        private static List<GameAudioInfo> BuildItems(int from, int to)
        {
            var items = new List<GameAudioInfo>(Math.Max(0, to - from));
            for (int i = from; i < to && i < _fullSongs.Count; i++)
            {
                var s = _fullSongs[i];
                var audio = GameAudioInfo.CreateNormal(
                    null, AudioTag.Other,
                    s.Name, s.ArtistName,
                    "netease_" + s.Id, true, null, null);
                UuidSongCache["netease_" + s.Id] = s;
                UuidAudioMap["netease_" + s.Id] = audio;
                items.Add(audio);
            }
            return items;
        }

        /// <summary>
        /// 切歌（上一首/下一首）统一接管入口：网易云歌**一律由插件接管**，完全按列表顺序切换——
        /// 原生 GetNextGameAudio 对网易云歌的索引计算不可靠（PlayingMusic 匹配失败会重播当前/跳过）。
        /// 已加载的歌直接播放；未加载的歌先按需加载再播放。
        /// 返回 false = 已拦截（原生切歌逻辑不执行）；返回 true = 放行（非网易云歌）。
        /// </summary>
        public static bool TryAdjacent(MusicService service, int direction)
        {
            try
            {
                if (service == null) return true;
                var list = service.CurrentPlayList;
                if (list == null || list.Count == 0) return true;

                // 随机模式：从当前列表随机选（排除正在播放的）——手动/自动切歌都随机跳
                bool shuffle = false;
                try { shuffle = Traverse.Create(service).Property("IsShuffle").GetValue<bool>(); } catch { }

                int windowCount = CountNetease(list);

                int target;
                if (shuffle && _fullSongs.Count > 0)
                {
                    // 随机模式直接在全量歌单中随机（不只是已注入的窗口），选中未注入的歌先扩窗
                    var rng = new Random();
                    var playing = service.PlayingMusic;
                    int playingFull = -1;
                    if (playing != null && IsNetease(playing) &&
                        long.TryParse(playing.UUID.Substring(8), out var playingId))
                    {
                        for (int i = 0; i < _fullSongs.Count; i++)
                        {
                            if (_fullSongs[i].Id == playingId) { playingFull = i; break; }
                        }
                    }
                    int pick;
                    int guard = 0;
                    do
                    {
                        pick = rng.Next(_fullSongs.Count);
                        guard++;
                    } while (pick == playingFull && _fullSongs.Count > 1 && guard < 30);

                    if (pick >= windowCount && !TryExtendWindow(pick + 1))
                    {
                        pick = Math.Min(pick, Math.Max(0, windowCount - 1)); // 扩窗失败 → 从现有窗口随机
                    }
                    target = pick;
                }
                else
                {
                    // 顺序模式：当前播放位置 ± 1 循环
                    int cur = LastPlayedIndex;
                    if (cur < 0 || cur >= list.Count)
                    {
                        var playing = service.PlayingMusic;
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (ReferenceEquals(list[i], playing) ||
                                (playing != null && list[i].UUID != null && list[i].UUID == playing.UUID))
                            {
                                cur = i;
                                break;
                            }
                        }
                    }

                    if (cur < 0)
                    {
                        target = direction > 0 ? 0 : windowCount - 1;
                    }
                    else
                    {
                        int desired = cur + direction;
                        if (desired >= windowCount && windowCount < _fullSongs.Count)
                        {
                            // 顺序播放接近窗口末尾 → 自动扩窗（一首首续上完整歌单）
                            TryExtendWindow(desired + 1);
                            list = service.CurrentPlayList;
                        }
                        if (desired >= list.Count) desired = 0;   // 完整歌单尽头 → 回到开头
                        if (desired < 0) desired = list.Count - 1;
                        target = desired;
                    }
                }

                if (target < 0 || target >= list.Count) return true;

                var audio = list[target];
                if (IsNetease(audio))
                {
                    if (audio.AudioClip != null)
                    {
                        // 已加载 → 直接按列表顺序播放（绕开游戏原生切歌）
                        Plugin.LogInfo($"[Netease] 切歌（{(direction > 0 ? "下一首" : "上一首")}）: 列表 #{target} 已加载，直接播放");
                        service.PlayMusicInPlaylist(target);
                    }
                    else
                    {
                        // 未加载 → 按需加载后播放
                        TryLoadAndPlay(audio, target, service);
                        Plugin.LogInfo($"[Netease] 切歌（{(direction > 0 ? "下一首" : "上一首")}）: 列表 #{target} 未加载，加载后播放");
                    }
                    return false; // 已拦截
                }
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[Netease] 切歌接管异常（放行游戏原生）: " + ex.Message);
            }
            return true;
        }

        /// <summary>是否网易云曲目（以 netease_ 前缀识别）。</summary>
        public static bool IsNetease(GameAudioInfo a)
            => a != null && a.UUID != null && a.UUID.StartsWith("netease_", StringComparison.Ordinal);

        /// <summary>
        /// 把歌单注入游戏原生播放列表并播放指定歌（由 F6 面板调用）。
        /// playIndex &lt; 0 = 只注入不播放（启动时恢复上次歌单用）。
        /// </summary>
        public static void InjectPlaylist(List<SongInfo> songs, int playIndex)
        {
            var service = MusicServicePatches.CurrentInstance;
            if (service == null)
            {
                Plugin.LogWarn("[Netease] MusicService 实例未就绪，无法注入歌单");
                return;
            }

            try
            {
                _fullSongs = songs != null ? new List<SongInfo>(songs) : new List<SongInfo>();
                UuidSongCache.Clear();
                UuidAudioMap.Clear();

                // 注入窗口：默认受 MaxInjectedSongs 约束；指定了播放位置时必须覆盖到它
                int window = _fullSongs.Count;
                int limit = WindowLimit;
                if (limit > 0) window = Math.Min(window, limit);
                if (playIndex >= 0 && playIndex >= window)
                {
                    window = Math.Min(_fullSongs.Count, playIndex + 1 + ExtendChunk);
                }

                var items = BuildItems(0, window);
                _injectedCount = window;

                // 单次 AddRange 填充（游戏原生标签筛选重建也是这个路径），
                // 避免 Clear+逐首 Add 对大歌单产生数千次集合变更事件
                service.CurrentPlayList.Clear();
                if (items.Count > 0)
                {
                    service.CurrentPlayList.AddRange(items);
                }

                // 同步随机播放列表（shuffle 模式下切歌从它取）
                try
                {
                    var shuffleList = Traverse.Create(service)
                        .Field("shuffleList").GetValue<List<GameAudioInfo>>();
                    shuffleList?.Clear();
                    shuffleList?.AddRange(items);
                }
                catch (Exception ex)
                {
                    Plugin.LogWarn("[Netease] shuffleList 同步失败（可忽略）: " + ex.Message);
                }

                // 触发游戏 UI 刷新事件（导入完成通知）
                try
                {
                    var subject = Traverse.Create(service)
                        .Field("_onCompleteImportMusic").GetValue<Subject<Unit>>();
                    subject?.OnNext(Unit.Default);
                }
                catch (Exception ex)
                {
                    Plugin.LogWarn("[Netease] 触发 UI 刷新事件失败（可忽略）: " + ex.Message);
                }

                Plugin.LogInfo($"[Netease] 歌单已注入游戏播放列表: 窗口 {items.Count}/{_fullSongs.Count} 首，播放 #{playIndex}");

                if (playIndex >= 0 && playIndex < items.Count)
                {
                    // 走到按需加载补丁：AudioClip 为空 → 先加载再播
                    service.PlayMusicInPlaylist(playIndex);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[Netease] 歌单注入失败: " + ex);
            }
        }

        /// <summary>
        /// 扩窗：把 _fullSongs 里的后续曲目追加进游戏播放列表（单次 AddRange）。
        /// 播放/切歌接近窗口末尾时调用；追加数 = max(需要量, ExtendChunk)。
        /// </summary>
        public static bool TryExtendWindow(int minCount)
        {
            if (_fullSongs.Count == 0) return false;
            var service = MusicServicePatches.CurrentInstance;
            if (service == null || service.CurrentPlayList == null) return false;

            try
            {
                int from = CountNetease(service.CurrentPlayList);
                if (from >= _fullSongs.Count)
                {
                    _injectedCount = from;
                    return false; // 全量已注入
                }
                int to = Math.Max(Math.Max(minCount, from + 1), Math.Min(from + ExtendChunk, _fullSongs.Count));
                to = Math.Min(to, _fullSongs.Count);
                if (to <= from) return false;

                var items = BuildItems(from, to);
                _injectedCount = to;
                service.CurrentPlayList.AddRange(items);
                try
                {
                    var shuffleList = Traverse.Create(service)
                        .Field("shuffleList").GetValue<List<GameAudioInfo>>();
                    shuffleList?.AddRange(items);
                }
                catch { }

                Plugin.LogInfo($"[Netease] 播放列表扩窗: {to}/{_fullSongs.Count} 首");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[Netease] 扩窗失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 播放入口被拦截时调用：网易云歌且 AudioClip 为空 → 启动按需加载，返回 true（已拦截）。
        /// AudioClip 已就绪或非网易云歌 → 返回 false（放行原方法）。
        /// </summary>
        public static bool TryLoadAndPlay(GameAudioInfo audio, int index, MusicService service)
        {
            if (!IsNetease(audio)) return false;
            if (audio.AudioClip != null) return false; // 已加载 → 放行，真正播放

            if (!UuidSongCache.TryGetValue(audio.UUID, out var song))
            {
                Plugin.LogWarn("[Netease] 找不到网易云歌曲元数据（可能歌单未注入）: " + audio.UUID);
                return false;
            }

            Plugin.LogInfo($"[Netease] 按需加载并播放: {song.Name}（列表 #{index}）");
            MusicImporter.StartLoadInto(audio, song, () =>
            {
                try
                {
                    // 加载完成 → 再次触发播放（此时 AudioClip 非空，补丁放行）
                    service.PlayMusicInPlaylist(index);
                }
                catch (Exception ex)
                {
                    Plugin.LogWarn("[Netease] 加载完成后播放失败: " + ex.Message);
                }
            });
            return true; // 已拦截，等加载完成再播
        }
    }
}
