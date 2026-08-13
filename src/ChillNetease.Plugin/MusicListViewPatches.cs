using HarmonyLib;

namespace ChillNetease.Plugin
{
    /// <summary>
    /// 拦截游戏播放列表的"自动滚动到正在播放的歌"行为。
    /// 游戏在播放状态变化时会调用 MusicPlayListViewMobile.ScrollToPlayingMusic(uuid)
    /// 把列表滚动到当前歌并居中显示——用户反馈这是"莫须有的操作"（点了歌列表自己跳），
    /// 因此 no-op 掉。
    /// </summary>
    [HarmonyPatch(typeof(Bulbul.Mobile.MusicPlayListViewMobile), "ScrollToPlayingMusic")]
    public static class MusicListViewPatches
    {
        [HarmonyPrefix]
        private static bool ScrollToPlayingMusic_Prefix()
        {
            return false; // 不滚动，保持用户当前浏览位置
        }
    }
}
