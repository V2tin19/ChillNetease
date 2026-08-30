using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;

namespace ChillNetease.Plugin
{
    /// <summary>
    /// 本地持久化的"链接导入歌单"：保存歌单 id/名称/歌曲元数据到 BepInEx/config，
    /// 重启后面板仍可见、可一键恢复注入，避免每次开游戏重新导入。
    /// </summary>
    public class StoredPlaylist
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("songCount")] public int SongCount { get; set; }
        [JsonProperty("importedAt")] public DateTime ImportedAt { get; set; }
        [JsonProperty("lastUsedAt")] public DateTime LastUsedAt { get; set; }
        [JsonProperty("songs")] public List<SongInfo> Songs { get; set; } = new List<SongInfo>();
    }

    public static class PlaylistStore
    {
        private static readonly object Gate = new object();
        private static readonly List<StoredPlaylist> Playlists = new List<StoredPlaylist>();

        /// <summary>启动时是否已尝试加载（Load 失败也置 true，避免反复重读坏文件）。</summary>
        public static bool Loaded { get; private set; }

        private static string FilePath => Path.Combine(Paths.ConfigPath, "ChillNetease.playlists.json");

        public static void Load()
        {
            lock (Gate)
            {
                if (Loaded) return;
                Loaded = true;
                try
                {
                    var path = FilePath;
                    if (!File.Exists(path)) return;
                    var json = File.ReadAllText(path);
                    var list = JsonConvert.DeserializeObject<List<StoredPlaylist>>(json);
                    if (list != null)
                    {
                        Playlists.Clear();
                        Playlists.AddRange(list);
                    }
                    Plugin.LogInfo($"[Netease] 本地导入歌单加载完成: {Playlists.Count} 个");
                }
                catch (Exception ex)
                {
                    Plugin.LogWarn("[Netease] 本地导入歌单加载失败（忽略）: " + ex.Message);
                }
            }
        }

        public static void Save()
        {
            lock (Gate)
            {
                try
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    var settings = new JsonSerializerSettings { Formatting = Formatting.Indented, NullValueHandling = NullValueHandling.Ignore };
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonConvert.SerializeObject(Playlists, settings));
                    // 先写临时文件再替换，避免写到一半崩溃留下损坏的存档
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    File.Move(tmp, FilePath);
                }
                catch (Exception ex)
                {
                    Plugin.LogWarn("[Netease] 本地导入歌单保存失败: " + ex.Message);
                }
            }
        }

        /// <summary>快照（按最近使用排序；返回新列表，遍历无需持锁）。</summary>
        public static List<StoredPlaylist> Snapshot()
        {
            lock (Gate)
            {
                var copy = new List<StoredPlaylist>(Playlists);
                copy.Sort((a, b) => b.LastUsedAt.CompareTo(a.LastUsedAt));
                return copy;
            }
        }

        public static StoredPlaylist Find(long id)
        {
            lock (Gate) return Playlists.Find(p => p.Id == id);
        }

        /// <summary>最近使用的一个导入歌单（无则 null）。</summary>
        public static StoredPlaylist GetLatest()
        {
            lock (Gate)
            {
                StoredPlaylist latest = null;
                foreach (var p in Playlists)
                {
                    if (latest == null || p.LastUsedAt > latest.LastUsedAt) latest = p;
                }
                return latest;
            }
        }

        public static void Upsert(long id, string name, List<SongInfo> songs)
        {
            if (id <= 0 || songs == null || songs.Count == 0) return;
            lock (Gate)
            {
                var existing = Playlists.Find(p => p.Id == id);
                var now = DateTime.UtcNow;
                if (existing != null)
                {
                    existing.Name = name;
                    existing.SongCount = songs.Count;
                    existing.LastUsedAt = now;
                    existing.Songs = songs;
                }
                else
                {
                    Playlists.Add(new StoredPlaylist
                    {
                        Id = id,
                        Name = name,
                        SongCount = songs.Count,
                        ImportedAt = now,
                        LastUsedAt = now,
                        Songs = songs
                    });
                }
            }
            Save();
        }

        public static bool Remove(long id)
        {
            bool removed;
            lock (Gate) removed = Playlists.RemoveAll(p => p.Id == id) > 0;
            if (removed) Save();
            return removed;
        }

        public static void Touch(long id)
        {
            lock (Gate)
            {
                var p = Playlists.Find(x => x.Id == id);
                if (p == null) return;
                p.LastUsedAt = DateTime.UtcNow;
            }
            Save();
        }
    }
}
