using System;
using Bulbul;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ChillNetease.Plugin
{
    /// <summary>
    /// 让游戏原生播放器的"上一首/下一首"按钮真正切换网易云歌曲。
    ///
    /// 关键调查（IL 反编译）：
    /// - 游戏切歌链路是 UI 点击 OnClickButtonSkip/OnClickButtonBack → MusicService.PlayNextMusic/PlayBackMusic
    ///   → GetNextGameAudio（内部取 AudioClip）→ 直接 MusicManager.Play(clip)。
    ///   **不经过 PlayMusicInPlaylist/PlayArugumentMusic**（那是"按列表索引播放"和"点歌播放"的入口）。
    /// - 网易云歌注入时 AudioClip 为空 → GetNextGameAudio 拿不到 clip → 切歌静默失败。
    /// - 之前 patch 的 MusicUI 类型在游戏程序集里不存在（Harmony 静默跳过）→ 按钮置灰修复也失效；
    ///   真正的 UI 容器是 Bulbul.FacilityMusic（DisableSkipMusicUI/EnableSkipMusicUI 私有方法控制按钮置灰）。
    ///
    /// 修复：
    /// 1. patch FacilityMusic.DisableSkipMusicUI → no-op（按钮不再被游戏置灰/换禁用图标）
    /// 2. patch FacilityMusic.OnClickButtonSkip/OnClickButtonBack（同步 Void，UI 点击必经）：
    ///    计算目标歌（CurrentPlayList 当前播放位置 ± 1 循环）→ 网易云歌且 AudioClip 未加载 → 拦截，
    ///    按需加载完成后 PlayMusicInPlaylist 完成切歌；否则放行游戏原生逻辑。
    /// </summary>
    [HarmonyPatch(typeof(FacilityMusic))]
    public static class MusicUiPatches
    {
        private static FacilityMusic _facility;
        private static float _lastFindTime;
        private static Button _nextButton;
        private static Button _backButton;

        /// <summary>拦截"禁用切歌按钮"：网易云歌单场景切歌始终可用。</summary>
        [HarmonyPatch("DisableSkipMusicUI")]
        [HarmonyPrefix]
        private static bool DisableSkipMusicUI_Prefix()
        {
            return false;
        }

        /// <summary>下一首按钮。</summary>
        [HarmonyPatch("OnClickButtonSkip")]
        [HarmonyPrefix]
        private static bool OnClickButtonSkip_Prefix(FacilityMusic __instance)
        {
            return TryPrepareAdjacent(__instance, +1);
        }

        /// <summary>上一首按钮。</summary>
        [HarmonyPatch("OnClickButtonBack")]
        [HarmonyPrefix]
        private static bool OnClickButtonBack_Prefix(FacilityMusic __instance)
        {
            return TryPrepareAdjacent(__instance, -1);
        }

        /// <summary>
        /// 切歌按钮拦截（上一首/下一首）：实际逻辑在 PlaylistLink.TryAdjacent
        /// （网易云歌一律接管、按列表顺序切歌；PlayNextMusic/PlayBackMusic 兜底拦截也复用）。
        /// 返回 false = 已拦截；返回 true = 放行（非网易云歌）。
        /// </summary>
        private static bool TryPrepareAdjacent(FacilityMusic facility, int direction)
        {
            try
            {
                var service = facility.MusicService;
                return PlaylistLink.TryAdjacent(service, direction);
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[Netease] 切歌按钮拦截异常（放行游戏原生）: " + ex.Message);
            }
            return true;
        }

        /// <summary>
        /// 每帧（低频）确保切歌按钮可交互：
        /// 1. 直接强制 MusicPlayerViewForMobile 的 _musicNextButton/_musicBackButton interactable=true
        ///    （绕开游戏内部"能否切歌"的判断——网易云歌单场景始终可切）
        /// 2. 主动调 FacilityMusic.EnableSkipMusicUI()（私有）把按钮状态拉回可用
        /// </summary>
        public static void EnsureButtonsEnabled()
        {
            try
            {
                // 每 1 秒重找一次实例（场景可能切换）
                if (_facility == null || Time.unscaledTime - _lastFindTime > 1f)
                {
                    _lastFindTime = Time.unscaledTime;
                    _facility = UnityEngine.Object.FindObjectOfType<FacilityMusic>();
                    if (_facility == null) return;

                    // 主动调用 EnableSkipMusicUI（遍历 _musicPlayerUIs 调 OnEnableSkipMusic → 按钮恢复可用）
                    try
                    {
                        Traverse.Create(_facility).Method("EnableSkipMusicUI").GetValue();
                    }
                    catch { }

                    // 直接强制按钮可点（双保险）
                    try
                    {
                        var view = UnityEngine.Object.FindObjectOfType<Bulbul.Mobile.MusicPlayerViewForMobile>();
                        if (view != null)
                        {
                            _nextButton = Traverse.Create(view).Field("_musicNextButton").GetValue<Button>();
                            _backButton = Traverse.Create(view).Field("_musicBackButton").GetValue<Button>();
                        }
                    }
                    catch { }
                }
                if (_facility == null) return;

                if (_nextButton != null && !_nextButton.interactable)
                {
                    _nextButton.interactable = true;
                }
                if (_backButton != null && !_backButton.interactable)
                {
                    _backButton.interactable = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogWarn("[Netease] EnsureButtonsEnabled 异常: " + ex.Message);
            }
        }
    }
}
