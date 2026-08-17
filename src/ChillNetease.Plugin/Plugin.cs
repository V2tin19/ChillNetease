using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ChillNetease.Plugin
{
    /// <summary>
    /// ChillNetease 游戏内插件入口（网易云音乐同步）。
    /// 技术栈沿用 ChillAI 验证过的方案：
    /// - 本游戏对插件组件不驱动 Unity 生命周期 → Harmony 挂钩 Bulbul.RoomGameManager.Update 每帧驱动
    /// - UI 用 IMGUI + Win32 键鼠（后续面板）
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Chill With You.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.haikisha.chillnetease";
        public const string PluginName = "Chill Netease";
        public const string PluginVersion = "0.2.1";

        public static ManualLogSource StaticLogger;

        /// <summary>网易云桥接（P/Invoke ChillNetease.dll）。</summary>
        public static NeteaseBridge Bridge;

        /// <summary>当前账号用户 ID（用于区分歌单归属）。</summary>
        public static long OwnUserId;

        /// <summary>已同步的用户歌单（创建+收藏，IsMine 标记归属）。</summary>
        public static List<PlaylistInfo> Playlists = new List<PlaylistInfo>();

        /// <summary>无限歌曲：绕过游戏原生 MusicService 的 100 首导入上限。</summary>
        public static BepInEx.Configuration.ConfigEntry<bool> EnableUnlimitedSongs;

        /// <summary>
        /// 登录后初始化：读用户信息 + 预加载歌单（登录成功/插件启动共用）。
        /// </summary>
        public static void ResetAfterLogin()
        {
            try
            {
                var info = Bridge?.GetUserInfo();
                if (info != null && info.TryGetValue("userId", out var uid))
                {
                    OwnUserId = Convert.ToInt64(uid);
                }

                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var playlists = Bridge.GetAllUserPlaylists(OwnUserId);
                        if (playlists != null)
                        {
                            lock (Playlists)
                            {
                                Playlists.Clear();
                                Playlists.AddRange(playlists);
                            }
                            NeteaseUi.OnPlaylistsLoaded(playlists);
                        }
                        StaticLogger?.LogInfo($"[Netease] 歌单同步完成: 共 {playlists?.Count ?? 0} 个" +
                            $"（我创建 {(playlists?.FindAll(p => p.IsMine).Count ?? 0)} / 收藏 {(playlists?.FindAll(p => !p.IsMine).Count ?? 0)}）");
                    }
                    catch (Exception ex)
                    {
                        StaticLogger?.LogWarning($"[Netease] 歌单同步失败: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                StaticLogger?.LogWarning($"[Netease] 登录后初始化失败: {ex.Message}");
            }
        }

        private void Awake()
        {
            StaticLogger = Logger;
            EnableUnlimitedSongs = Config.Bind("General", "EnableUnlimitedSongs", true,
                "绕过游戏原生 100 首音乐导入上限（网易云歌曲导入需要）");
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded");

            // 1. 初始化网易云桥接（ChillNetease.dll 与插件同目录）
            var pluginDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            Bridge = new NeteaseBridge();
            var ok = Bridge.Initialize(pluginDir, "");
            Logger.LogInfo($"[Netease] Bridge init: {ok}");

            if (ok && Bridge.IsLoggedIn)
            {
                ResetAfterLogin();
            }
            else
            {
                Logger.LogInfo("[Netease] 未登录（F6 面板提供二维码登录）");
            }

            // 2. Harmony 挂钩游戏每帧方法 + 应用补丁（MusicService + MusicUI 切歌按钮）
            var harmony = new Harmony(PluginGuid);
            InstallTickPatch(harmony);
            try
            {
                harmony.PatchAll(); // MusicServicePatches / MusicUiPatches
                Logger.LogInfo($"[ChillNetease] Harmony 补丁已应用，共挂钩 {harmony.GetPatchedMethods().Count()} 个方法");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[ChillNetease] Harmony PatchAll 失败: {ex}");
            }
        }

        /// <summary>挂钩 Bulbul.RoomGameManager.Update（每帧驱动，本游戏唯一可靠的生命周期入口）。</summary>
        private static void InstallTickPatch(Harmony harmony)
        {
            try
            {
                var gameAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
                if (gameAsm == null)
                {
                    StaticLogger?.LogWarning("[ChillNetease] 未找到 Assembly-CSharp");
                    return;
                }
                var roomType = gameAsm.GetType("Bulbul.RoomGameManager");
                var updateMethod = roomType?.GetMethod("Update",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (updateMethod == null)
                {
                    StaticLogger?.LogWarning("[ChillNetease] 未找到 RoomGameManager.Update");
                    return;
                }
                harmony.Patch(updateMethod, postfix: new HarmonyMethod(
                    typeof(Plugin).GetMethod(nameof(TickPostfix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                StaticLogger?.LogInfo("[ChillNetease] 已挂钩 RoomGameManager.Update（每帧驱动）");
            }
            catch (Exception ex)
            {
                StaticLogger?.LogError("[ChillNetease] 挂钩失败: " + ex);
            }
        }

        private static void TickPostfix()
        {
            try
            {
                // 确保 UI 渲染组件存在（OnGUI 需要组件承载；由首个 Tick 惰性创建）
                EnsureUiRenderer();
                // 推进网易云歌曲导入状态机 + 面板输入（每帧，主线程）
                MusicImporter.Pump();
                NeteaseUi.Tick();
                // 确保游戏原生切歌按钮可交互（网易云歌曲可切歌）
                MusicUiPatches.EnsureButtonsEnabled();
                // 监控 MusicService 实例捕获状态（每 10 秒一次）
                if (MusicServicePatches.CurrentInstance == null && Time.unscaledTime - _lastServiceCheck > 10f)
                {
                    _lastServiceCheck = Time.unscaledTime;
                    LogWarn("[Netease] MusicService 实例尚未捕获（游戏可能未调用 AddMusicItem）");
                }
            }
            catch (Exception ex)
            {
                // 绝不因插件异常影响游戏帧循环
                LogWarn("[ChillNetease] Tick 异常: " + ex.Message);
            }
        }

        private static float _lastServiceCheck;

        /// <summary>创建 UI 渲染组件（OnGUI 需要组件承载；由首个 Tick 时惰性创建）。</summary>
        public static void EnsureUiRenderer()
        {
            if (NeteaseUiRenderer.Instance != null) return;
            var go = new GameObject("ChillNetease_UiRenderer");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<NeteaseUiRenderer>();
        }

        public static void LogInfo(string msg) => StaticLogger?.LogInfo(msg);
        public static void LogWarn(string msg) => StaticLogger?.LogWarning(msg);
        public static void LogError(string msg) => StaticLogger?.LogError(msg);
    }
}
