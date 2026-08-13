using System;
using System.Collections.Generic;
using System.Linq;
using Bulbul;
using HarmonyLib;
using R3;

namespace ChillNetease.Plugin
{
    /// <summary>
    /// 对接游戏原生播放器 MusicService：
    /// 1. patch AddMusicItem 捕获实例（游戏无静态单例，靠补丁截获 __instance）
    /// 2. 绕过 100 首导入上限（_allMusicList.Count >= 100 拒绝）
    /// 3. 保持"查重 + 加入当前播放列表"的原生行为
    /// </summary>
    [HarmonyPatch(typeof(MusicService))]
    public static class MusicServicePatches
    {
        /// <summary>游戏原生 MusicService 实例（patch 捕获）。</summary>
        public static MusicService CurrentInstance { get; internal set; }

        /// <summary>
        /// 游戏启动时通过 Load 加载音乐数据（不经过 AddMusicItem），
        /// 因此必须同时 patch Load 才能尽早捕获实例。
        /// </summary>
        [HarmonyPatch("Load")]
        [HarmonyPostfix]
        private static void Load_Postfix(MusicService __instance)
        {
            CurrentInstance = __instance;
        }

        [HarmonyPatch("AddMusicItem")]
        [HarmonyPrefix]
        private static bool AddMusicItem_Prefix(MusicService __instance, GameAudioInfo music, ref bool __result)
        {
            CurrentInstance = __instance;

            // 未开启无限歌曲 → 执行原方法（保持游戏默认 100 首限制）
            if (!Plugin.EnableUnlimitedSongs.Value)
            {
                return true;
            }

            if (music == null)
            {
                __result = false;
                return false;
            }

            var allMusicList = Traverse.Create(__instance)
                .Field("_allMusicList")
                .GetValue<List<GameAudioInfo>>();
            if (allMusicList == null)
            {
                return true; // 拿不到内部列表，交给原方法
            }

            // 查重（UUID）
            if (allMusicList.Any(m => m.UUID == music.UUID))
            {
                __result = false;
                return false;
            }

            // 绕过 100 首上限：直接加入内部列表
            allMusicList.Add(music);

            // 同步加入当前播放列表与随机列表（保证游戏原生播放列表 UI 立即可见）
            var shuffleList = Traverse.Create(__instance)
                .Field("shuffleList")
                .GetValue<List<GameAudioInfo>>();
            var currentPlayList = __instance.CurrentPlayList;
            shuffleList?.Add(music);
            currentPlayList?.Add(music);

            // 触发导入完成事件，通知游戏 UI 刷新
            try
            {
                var subject = Traverse.Create(__instance)
                    .Field("_onCompleteImportMusic")
                    .GetValue<Subject<Unit>>();
                subject?.OnNext(Unit.Default);
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[Netease] 触发 UI 刷新事件失败: " + ex.Message);
            }

            __result = true;
            return false; // 跳过原方法
        }

        /// <summary>
        /// 播放列表按索引播放入口（游戏原生切歌/播放列表 UI 走这里）。
        /// 目标为网易云歌且 AudioClip 未加载 → 拦截并触发按需加载，加载完成后自动继续播放。
        /// </summary>
        [HarmonyPatch("PlayMusicInPlaylist")]
        [HarmonyPrefix]
        private static bool PlayMusicInPlaylist_Prefix(MusicService __instance, int index, ref bool __result)
        {
            try
            {
                // 记录最近播放索引（切歌位置计算依赖它，不信任游戏内部状态）
                PlaylistLink.NotePlayed(index);

                var list = __instance.CurrentPlayList;
                if (list == null || index < 0 || index >= list.Count) return true;
                var audio = list[index];
                if (PlaylistLink.TryLoadAndPlay(audio, index, __instance))
                {
                    __result = true; // 假装成功，加载完成后真正播放
                    return false;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[Netease] PlayMusicInPlaylist 拦截异常: " + ex.Message);
            }
            return true;
        }

        /// <summary>
        /// "全部排除"状态修复：游戏在 CurrentPlayList 为空时调用 SetIsAllExcludedMusicFromPlaylist
        /// 会把状态错误地置为 true（空列表循环不执行），且注入网易云歌单后不重算——
        /// 导致游戏 UI 的"下一首"按钮被 b__26_14 的 IsAllExcluded 判断永久拦截。
        /// 修复：getter 拦截——列表含网易云歌时强制返回 false（网易云歌单不可能"全部排除"）。
        /// </summary>
        [HarmonyPatch("get_IsAllExcludedMusicFromPlaylist")]
        [HarmonyPrefix]
        private static bool GetIsAllExcluded_Prefix(MusicService __instance, ref bool __result)
        {
            try
            {
                var list = __instance.CurrentPlayList;
                if (list != null && list.Count > 0)
                {
                    foreach (var a in list)
                    {
                        if (PlaylistLink.IsNetease(a))
                        {
                            __result = false;
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[Netease] IsAllExcluded 拦截异常: " + ex.Message);
            }
            return true; // 纯游戏歌场景走原逻辑
        }

        /// <summary>
        /// 点击列表项播放入口（FacilityMusic.OnClickButtonPlayListPlayMusicButton 走 PlayArugumentMusic）。
        /// 同上：网易云歌未加载 → 拦截按需加载。
        /// </summary>
        [HarmonyPatch("PlayArugumentMusic")]
        [HarmonyPrefix]
        private static bool PlayArugumentMusic_Prefix(MusicService __instance, GameAudioInfo audioInfo)
        {
            try
            {
                if (audioInfo == null) return true;
                var list = __instance.CurrentPlayList;
                if (list == null) return true;
                for (int i = 0; i < list.Count; i++)
                {
                    if (ReferenceEquals(list[i], audioInfo) || list[i].UUID == audioInfo.UUID)
                    {
                        if (PlaylistLink.TryLoadAndPlay(audioInfo, i, __instance))
                        {
                            return false; // 已拦截：按需加载完成后会 PlayMusicInPlaylist 继续
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[Netease] PlayArugumentMusic 拦截异常: " + ex.Message);
            }
            return true;
        }

        /// <summary>
        /// "下一首"兜底拦截：游戏 UI 的下一首按钮可能走 IMusicPlayerUI.OnClickNextButton 事件
        /// 直接连到 PlayNextMusic（不经过 FacilityMusic.OnClickButtonSkip），因此在此统一兜底——
        /// 无论入口（UI 按钮/自动播放/其他），网易云歌一律接管按列表顺序切歌。
        /// 注意：prefix 不写 __result（方法返回 UniTask，插件无法引用该类型；UniTask.Void 调用方为
        /// fire-and-forget，拦截后无副作用）。
        /// </summary>
        [HarmonyPatch("PlayNextMusic")]
        [HarmonyPrefix]
        private static bool PlayNextMusic_Prefix(MusicService __instance, int nextCount, MusicChangeKind changeKind)
        {
            int direction = nextCount >= 0 ? 1 : -1;
            return PlaylistLink.TryAdjacent(__instance, direction);
        }

        /// <summary>"上一首"兜底拦截（同 PlayNextMusic）。</summary>
        [HarmonyPatch("PlayBackMusic")]
        [HarmonyPrefix]
        private static bool PlayBackMusic_Prefix(MusicService __instance)
        {
            return PlaylistLink.TryAdjacent(__instance, -1);
        }

        /// <summary>
        /// "跳过当前歌"拦截：游戏 UI 的手动"下一首"按钮很可能走 SkipCurrentMusic
        /// （而不是 PlayNextMusic——那解释了下首按钮之前没反应、自动播放却正常）。
        /// 网易云歌一律接管 → 按列表顺序切下一首。
        /// </summary>
        [HarmonyPatch("SkipCurrentMusic")]
        [HarmonyPrefix]
        private static bool SkipCurrentMusic_Prefix(MusicService __instance, MusicChangeKind kind)
        {
            return PlaylistLink.TryAdjacent(__instance, 1);
        }

        /// <summary>
        /// 随机播放开关接管（关键保护）：
        /// 游戏原生 SetShuffle 会 ① 无条件 Clear CurrentPlayList（把网易云注入的列表弄丢）
        /// ② shuffleList 从 _allMusicList 重建（网易云歌不在其中）。
        /// 我们接管：不 Clear 播放列表，shuffleList 从 CurrentPlayList（含网易云歌）重建。
        /// </summary>
        [HarmonyPatch("SetShuffle")]
        [HarmonyPrefix]
        private static bool SetShuffle_Prefix(MusicService __instance, bool isShuffle)
        {
            try
            {
                // 1. 状态与存档
                Traverse.Create(__instance).Property("IsShuffle").SetValue(isShuffle);
                try
                {
                    var save = Traverse.Create(typeof(SaveDataManager)).Property("Instance").GetValue<SaveDataManager>();
                    if (save != null)
                    {
                        var setting = save.MusicSetting;
                        if (setting != null)
                        {
                            Traverse.Create(setting).Field("IsShufflePlayMusic").SetValue(isShuffle);
                        }
                        Traverse.Create(save).Method("SaveMusicSetting").GetValue();
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogWarn("[Netease] SetShuffle 存档失败（可忽略）: " + ex.Message);
                }

                // 2. 关键：不 Clear CurrentPlayList；shuffleList 从当前播放列表重建（网易云歌在里面）
                var list = __instance.CurrentPlayList != null
                    ? new List<GameAudioInfo>(__instance.CurrentPlayList)
                    : new List<GameAudioInfo>();
                if (isShuffle)
                {
                    PlaylistLink.ShuffleList(list);
                }
                Traverse.Create(__instance).Field("shuffleList").SetValue(list);

                // 3. 当前播放的歌移到 shuffleList 头部（保持"当前歌"语义）
                var playing = __instance.PlayingMusic;
                if (playing != null)
                {
                    int idx = list.FindIndex(a => ReferenceEquals(a, playing) ||
                        (playing.UUID != null && a.UUID != null && a.UUID == playing.UUID));
                    if (idx > 0)
                    {
                        var item = list[idx];
                        list.RemoveAt(idx);
                        list.Insert(0, item);
                        Traverse.Create(__instance).Field("shuffleList").SetValue(list);
                    }
                }

                Plugin.LogInfo($"[Netease] 随机播放已{(isShuffle ? "开启" : "关闭")}（列表已保护，shuffle 含网易云歌）");
                return false; // 已接管
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[Netease] SetShuffle 接管失败（放行原方法）: " + ex.Message);
            }
            return true;
        }
    }
}
