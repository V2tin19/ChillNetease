using System;
using System.Threading.Tasks;
using Bulbul;
using HarmonyLib;

namespace ChillNetease.Plugin
{
    /// <summary>
    /// 游戏收藏星星按钮 → 网易云红心 联动。
    /// 游戏播放列表每首歌右侧的星星按钮，点击后最终调用 MusicService.RegisterFavoriteMusic /
    /// UnregisterFavoriteMusic（IL 反编译确认：MusicPlayListItemView.OnClickFavoriteButton
    /// → MusicPlayListViewMobile.CreateViewsHolder 订阅回调 → 这两个方法）。
    /// 我们 postfix 拦截：若是网易云注入的曲目（UUID 前缀 netease_），提取 songId 调网易云红心/取消红心；
    /// 游戏本地收藏照常执行（星星图标正常亮灭）。
    /// </summary>
    public static class NeteaseFavoriteSync
    {
        private const string NeteasePrefix = "netease_";

        /// <summary>游戏收藏（红心点亮）→ 网易云红心。</summary>
        [HarmonyPatch(typeof(MusicService), nameof(MusicService.RegisterFavoriteMusic))]
        public static class RegisterFavoriteMusicPatch
        {
            [HarmonyPostfix]
            private static void Postfix(GameAudioInfo gameAudioInfo)
            {
                Sync(gameAudioInfo, true);
            }
        }

        /// <summary>游戏取消收藏（红心熄灭）→ 网易云取消红心。</summary>
        [HarmonyPatch(typeof(MusicService), nameof(MusicService.UnregisterFavoriteMusic))]
        public static class UnregisterFavoriteMusicPatch
        {
            [HarmonyPostfix]
            private static void Postfix(GameAudioInfo gameAudioInfo)
            {
                Sync(gameAudioInfo, false);
            }
        }

        /// <summary>仅处理网易云曲目，后台线程调 DLL（网络调用不卡游戏）。失败只记日志，不阻塞游戏收藏。</summary>
        private static void Sync(GameAudioInfo music, bool like)
        {
            if (music?.UUID == null || !music.UUID.StartsWith(NeteasePrefix, StringComparison.Ordinal))
            {
                return; // 非网易云曲目（游戏自带歌）不干预
            }
            if (Plugin.Bridge == null || !Plugin.Bridge.IsInitialized)
            {
                return;
            }

            var idStr = music.UUID.Substring(NeteasePrefix.Length);
            if (!long.TryParse(idStr, out var songId) || songId <= 0)
            {
                return;
            }

            var bridge = Plugin.Bridge;
            Task.Run(() =>
            {
                try
                {
                    var ok = bridge.SetLike(songId, like);
                    Plugin.LogInfo($"[Netease] 网易云{(like ? "红心" : "取消红心")}: songId={songId} {(ok ? "成功" : "失败 " + bridge.LastError())}");
                }
                catch (Exception ex)
                {
                    Plugin.LogWarn("[Netease] 红心同步异常: " + ex.Message);
                }
            });
        }
    }
}
