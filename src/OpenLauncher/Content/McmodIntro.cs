using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Content
{
    /// <summary>
    /// MC百科（mcmod.cn）中文简介：best-effort 抓取，全程异常保护。
    /// 搜索第一个匹配的条目页，取页面 meta description 作为中文简介；结果缓存 7 天。
    /// </summary>
    public static class McmodIntro
    {
        const string SearchUrl = "https://search.mcmod.cn/s?key=";
        const string GenericSuffix = "我的世界MOD百科，提供Minecraft(我的世界)MOD(模组)物品资料介绍教程攻略和MOD下载。";
        static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
        static readonly Regex ClassLink = new Regex(@"https://www\.mcmod\.cn/class/\d+\.html", RegexOptions.IgnoreCase);
        static readonly Regex MetaDesc = new Regex(@"<meta\s+name=""description""\s+content=""(?<t>[^""]*)""", RegexOptions.IgnoreCase);

        public class Intro { public string Text; public string Url; }

        static string Dir => Paths.Ensure(Path.Combine(Paths.Cache, "mcmod"));

        /// <summary>取中文简介；取不到返回 null，绝不抛异常</summary>
        public static async Task<Intro> FetchAsync(string title, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var key = Util.Sha256(Encoding.UTF8.GetBytes(title.Trim().ToLowerInvariant())).Substring(0, 16);
            var cacheFile = Path.Combine(Dir, key + ".json");
            try
            {
                if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheTtl)
                {
                    var cached = JNode.Parse(File.ReadAllText(cacheFile));
                    if (cached["none"].Bool) return null;
                    return new Intro { Text = cached["text"].Str, Url = cached["url"].Str };
                }
            }
            catch { }

            Intro result = null;
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    var search = await GetStringAsync(SearchUrl + Uri.EscapeDataString(title.Trim()), cts.Token).ConfigureAwait(false);
                    var m = ClassLink.Match(search);
                    if (m.Success)
                    {
                        var page = await GetStringAsync(m.Value, cts.Token).ConfigureAwait(false);
                        var dm = MetaDesc.Match(page);
                        if (dm.Success)
                        {
                            var text = DecodeEntities(dm.Groups["t"].Value).Trim();
                            // 去掉 mcmod 的统一后缀，只留简介本身；纯默认描述视为没有
                            int cut = text.IndexOf("，我的世界MOD百科", StringComparison.Ordinal);
                            if (cut >= 0) text = text.Substring(0, cut).Trim();
                            if (text.Length >= 10 && !text.Contains("MOD百科"))
                                result = new Intro { Text = text, Url = m.Value };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"获取 MC 百科中文简介失败（{title}）：{ex.Message}");
            }

            try
            {
                var j = JNode.Obj().Set("none", result == null);
                if (result != null) j.Set("text", result.Text).Set("url", result.Url);
                File.WriteAllText(cacheFile, j.ToJson(false));
            }
            catch { }
            return result;
        }

        static async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                using (var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) throw new HttpStatusException(url, (int)resp.StatusCode, null);
                    var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return Encoding.UTF8.GetString(bytes);
                }
            }
        }

        static string DecodeEntities(string s)
        {
            return s.Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        }
    }
}
