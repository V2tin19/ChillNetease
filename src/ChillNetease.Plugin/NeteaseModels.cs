using System.Collections.Generic;
using Newtonsoft.Json;

namespace ChillNetease.Plugin
{
    /// <summary>网易云歌曲信息（ChillNetease.dll JSON 返回）。</summary>
    public class SongInfo
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("duration")] public double Duration { get; set; }
        [JsonProperty("artists")] public System.Collections.Generic.List<string> Artists { get; set; }
        [JsonProperty("album")] public string Album { get; set; }
        [JsonProperty("albumId")] public long AlbumId { get; set; }
        [JsonProperty("coverUrl")] public string CoverUrl { get; set; }

        public string ArtistName => Artists != null ? string.Join(", ", Artists) : "";
    }

    /// <summary>搜索单曲响应（网易云原生格式）。</summary>
    public class SearchResponse
    {
        [JsonProperty("result")] public SearchResult Result { get; set; }
        [JsonProperty("code")] public int Code { get; set; }
    }

    public class SearchResult
    {
        [JsonProperty("songs")] public List<SearchSong> Songs { get; set; }
    }

    public class SearchSong
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("dt")] public long Dt { get; set; } // 毫秒
        [JsonProperty("ar")] public List<SearchArtist> Ar { get; set; }
        [JsonProperty("al")] public SearchAlbum Al { get; set; }
    }

    public class SearchArtist
    {
        [JsonProperty("name")] public string Name { get; set; }
    }

    public class SearchAlbum
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("picUrl")] public string PicUrl { get; set; }
    }

    /// <summary>播放 URL 结果。</summary>
    public class SongUrl
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("size")] public long Size { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("isTrial")] public bool IsTrial { get; set; }
    }

    /// <summary>歌单信息。</summary>
    public class PlaylistInfo
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("songCount")] public int SongCount { get; set; }
        [JsonProperty("coverUrl")] public string CoverUrl { get; set; }
        [JsonProperty("creatorId")] public long CreatorId { get; set; }

        /// <summary>是否当前账号创建（用于"我创建的/我收藏的"分组）。</summary>
        public bool IsMine { get; set; }
    }

    /// <summary>歌单列表响应。</summary>
    public class PlaylistsResponse
    {
        [JsonProperty("playlists")] public System.Collections.Generic.List<PlaylistInfo> Playlists { get; set; }
        [JsonProperty("hasMore")] public bool HasMore { get; set; }
    }

    /// <summary>歌单详情（含歌曲）。</summary>
    public class PlaylistDetail
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("songCount")] public int SongCount { get; set; }
        [JsonProperty("coverUrl")] public string CoverUrl { get; set; }
        [JsonProperty("creatorId")] public long CreatorId { get; set; }
        [JsonProperty("songs")] public System.Collections.Generic.List<SongInfo> Songs { get; set; }
    }

    /// <summary>二维码登录状态（网易云标准状态码）。</summary>
    public class QrLoginState
    {
        [JsonProperty("uniKey")] public string UniKey { get; set; }
        [JsonProperty("qrcodeUrl")] public string QrCodeUrl { get; set; }
        [JsonProperty("statusCode")] public int StatusCode { get; set; }
        [JsonProperty("statusMsg")] public string StatusMsg { get; set; }

        /// <summary>等待扫码。</summary>
        public bool IsWaitingScan => StatusCode == 801;
        /// <summary>已扫码待确认。</summary>
        public bool IsWaitingConfirm => StatusCode == 802;
        /// <summary>登录成功。</summary>
        public bool IsSuccess => StatusCode == 803;
        /// <summary>二维码失效。</summary>
        public bool IsExpired => StatusCode == 800;
    }

    /// <summary>音质等级（映射 ChillNetease.dll 的 quality 字符串）。</summary>
    public enum AudioQuality
    {
        Standard, Higher, ExHigh, Lossless, HiRes, JYEffect, Sky, JYMaster
    }
}
