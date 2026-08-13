using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NeteaseProbe
{
    /// <summary>
    /// 网易云 weapi 接口的 C# 实现（.NET 自带加密，零依赖）。
    /// 参考开源实现（NeteaseCloudMusicApi 等）的公开算法：
    /// - 参数 AES-CBC 双重加密（0CoJUm6Qyw8W8jud + 随机 secKey）
    /// - secKey RSA-1024 加密（PKCS1，先反转）
    /// </summary>
    public static class NeteaseWeapi
    {
        private const string AesKey = "0CoJUm6Qyw8W8jud";
        private const string AesIv = "0102030405060708";
        private const string RsaExponentHex = "010001";
        // 1024 位 RSA modulus（128 字节，不含符号位前缀 00——.NET RSAParameters 要求恰好等于密钥长度）
        private const string RsaModulusHex =
            "e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7";

        public static string CookiePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "netease-cookie.txt");

        // ---------------- 公开方法 ----------------

        /// <summary>weapi POST，返回响应体文本。</summary>
        public static string Post(string url, Dictionary<string, object> payload, string cookie = null)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            var (par, key) = Encrypt(json);
            var body = "params=" + Uri.EscapeDataString(par) + "&encSecKey=" + Uri.EscapeDataString(key);

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null; // 禁用系统代理（clash 代理出口可能被网易云风控 → 返回空）
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.Referer = "https://music.163.com/";
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            req.Headers["Origin"] = "https://music.163.com";
            req.ProtocolVersion = System.Net.HttpVersion.Version11;
            req.ServicePoint.Expect100Continue = false;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (!string.IsNullOrEmpty(cookie)) req.Headers[HttpRequestHeader.Cookie] = cookie;
            using (var sw = new StreamWriter(req.GetRequestStream()))
            {
                sw.Write(body);
            }
            try
            {
                using var resp = (HttpWebResponse)req.GetResponse();
                using var sr = new StreamReader(resp.GetResponseStream() ?? Stream.Null);
                return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                var code = ex.Response is HttpWebResponse r ? (int)r.StatusCode : -1;
                string body2 = "";
                try
                {
                    using var sr = new StreamReader(ex.Response?.GetResponseStream() ?? Stream.Null);
                    body2 = sr.ReadToEnd();
                }
                catch { }
                Console.WriteLine($"[weapi HTTP {code}] {ex.Message} body={body2}");
                return "";
            }
        }

        /// <summary>weapi POST，额外返回 Set-Cookie（登录类接口用）。</summary>
        public static (string Body, string SetCookie) PostWithCookie(string url, Dictionary<string, object> payload, string cookie = null)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            var (par, key) = Encrypt(json);
            var body = "params=" + Uri.EscapeDataString(par) + "&encSecKey=" + Uri.EscapeDataString(key);

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.Referer = "https://music.163.com/";
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
            if (!string.IsNullOrEmpty(cookie)) req.Headers[HttpRequestHeader.Cookie] = cookie;
            using (var sw = new StreamWriter(req.GetRequestStream()))
            {
                sw.Write(body);
            }
            using var resp = (HttpWebResponse)req.GetResponse();
            var setCookie = resp.Headers["Set-Cookie"];
            using var sr = new StreamReader(resp.GetResponseStream() ?? Stream.Null);
            return (sr.ReadToEnd(), setCookie);
        }

        /// <summary>二维码登录第一步：拿 unikey。</summary>
        public static string GetQrKey()
        {
            // 调试：打印加密参数格式
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new Dictionary<string, object> { ["type"] = 1 });
            var (par, key) = Encrypt(json);
            Console.WriteLine($"[debug] payload={json}");
            Console.WriteLine($"[debug] params len={par.Length} head={par.Substring(0, Math.Min(30, par.Length))}");
            Console.WriteLine($"[debug] encSecKey len={key.Length} head={key.Substring(0, Math.Min(40, key.Length))}");

            var resp = Post("https://music.163.com/weapi/login/qr/key",
                new Dictionary<string, object> { ["type"] = 1 });
            return resp;
        }

        /// <summary>二维码登录：轮询 qr/check，803 成功时保存完整登录 cookie。</summary>
        public static string CheckQr(string unikey)
        {
            var (body, setCookie) = PostWithCookie("https://music.163.com/weapi/login/qr/check",
                new Dictionary<string, object> { ["key"] = unikey, ["type"] = 1 });
            if (body.Contains("\"code\":803") && !string.IsNullOrEmpty(setCookie))
            {
                // 提取 Set-Cookie 中的键值（MUSIC_U、__csrf 等），拼接成 cookie 串
                var sb = new StringBuilder();
                foreach (var part in setCookie.Split(','))
                {
                    var trimmed = part.Trim();
                    var eq = trimmed.IndexOf('=');
                    if (eq <= 0) continue;
                    var name = trimmed.Substring(0, eq).Trim();
                    if (name.Equals("Path", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Domain", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Expires", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Max-Age", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Secure", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var value = trimmed.Substring(eq + 1).Split(';')[0].Trim();
                    sb.Append(name).Append('=').Append(value).Append("; ");
                }
                SaveCookie(sb.ToString().TrimEnd(' ', ';'));
            }
            return body;
        }

        /// <summary>拿播放 URL。level: standard/higher/exhigh/lossless/hires。</summary>
        public static string GetSongUrl(string idsJson, string level, string cookie)
        {
            var resp = Post("https://music.163.com/weapi/song/enhance/player/url/v1",
                new Dictionary<string, object>
                {
                    ["ids"] = idsJson,
                    ["level"] = level,
                    ["encodeType"] = "mp3",
                    ["csrf_token"] = ""
                }, cookie);
            return resp;
        }

        // ---------------- cookie 存取 ----------------

        public static void SaveCookie(string cookie) => File.WriteAllText(CookiePath, cookie, Encoding.UTF8);

        /// <summary>读取登录 cookie：优先 go-musicfox 的 Netscape cookie 文件（登录态权威来源），其次自己的文件。</summary>
        public static string LoadCookie()
        {
            var musicfoxCookie = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "go-musicfox", "cookie");
            if (File.Exists(musicfoxCookie))
            {
                var sb = new StringBuilder();
                foreach (var ln in File.ReadAllLines(musicfoxCookie))
                {
                    if (ln.StartsWith("#") || string.IsNullOrWhiteSpace(ln)) continue;
                    var p = ln.Split('\t');
                    if (p.Length >= 7) sb.Append(p[5]).Append('=').Append(p[6]).Append("; ");
                }
                var s = sb.ToString().TrimEnd(' ', ';');
                if (s.Length > 0) return s;
            }
            return File.Exists(CookiePath) ? File.ReadAllText(CookiePath).Trim() : null;
        }

        // ---------------- 加密 ----------------

        private static (string Params, string EncSecKey) Encrypt(string payloadJson)
        {
            // 1. 随机 16 字节 secKey
            var secKey = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secKey);

            // 2. AES-CBC 双重加密
            var onceBase64 = AesEncrypt(Encoding.UTF8.GetBytes(payloadJson), Encoding.UTF8.GetBytes(AesKey));
            var twice = AesEncrypt(Convert.FromBase64String(onceBase64), secKey);

            // 3. RSA 加密 secKey（先反转）
            var reversed = (byte[])secKey.Clone();
            Array.Reverse(reversed);
            var encSecKey = RsaEncrypt(reversed);

            return (twice, encSecKey);
        }

        private static string AesEncrypt(byte[] plaintext, byte[] key)
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = Encoding.UTF8.GetBytes(AesIv);
            using var enc = aes.CreateEncryptor();
            return Convert.ToBase64String(enc.TransformFinalBlock(plaintext, 0, plaintext.Length));
        }

        private static string RsaEncrypt(byte[] data)
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Exponent = HexToBytes(RsaExponentHex),
                Modulus = HexToBytes(RsaModulusHex)
            });
            var encrypted = rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1);
            return Convert.ToHexString(encrypted).ToLowerInvariant();
        }

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
