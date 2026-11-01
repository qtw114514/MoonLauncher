using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MoonLauncher.Core
{
    /// <summary>
    /// Minecraft 官网社区 RSS（唯一真实来源）。60 分钟本地缓存；
    /// 网络或解析失败时绝不编造内容，只回退缓存或返回明确错误。
    /// 首条新闻附带横幅图：优先抓取原文页 meta og:image；抓不到时回退官网固定新闻头图
    /// （该固定图只是 Minecraft 官网的通用横幅，并非文章原图）。
    /// </summary>
    public static class MinecraftNews
    {
        public const string SourceUrl = "https://www.minecraft.net/en-us/feeds/community-content/rss";
        public const string SourceName = "minecraft.net";

        /// <summary>
        /// 兜底横幅：Minecraft 官网固定新闻头图。仅在拿不到文章 og:image 时使用，
        /// 它是官网通用配图，不是任何一篇新闻的原始配图。
        /// </summary>
        public const string FallbackImageUrl = "https://www.minecraft.net/content/dam/minecraftnet/games/minecraft/key-art/Global-Header_Image-Tile_Whats-Happening_570x321.jpg";

        const int MaxItems = 5;
        static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);
        static string CacheFile => Path.Combine(Paths.Cache, "minecraft-news.xml");

        public class Item
        {
            public string Title;
            public string Description;
            public string Link;
            public string Date;
            /// <summary>横幅图地址：文章 og:image 优先，失败时为官网固定头图（非文章原图）。</summary>
            public string ImageUrl;
        }

        public class Result
        {
            public List<Item> Items = new List<Item>();
            /// <summary>无可用缓存时的网络/解析错误；null 表示成功。</summary>
            public string Error;
            public bool FromCache;
        }

        public static async Task<Result> LoadAsync()
        {
            string xml = null;
            try
            {
                xml = await Http.GetStringAsync(SourceUrl, mirror: false, timeoutSec: 12).ConfigureAwait(false);
                WriteCache(xml);
            }
            catch (Exception ex)
            {
                Log.Warn("获取 Minecraft 官方新闻失败：" + ex.Message);
            }

            List<Item> items = null;
            bool fromCache = false;
            if (xml != null)
            {
                try { items = Parse(xml); }
                catch (Exception ex) { Log.Warn("解析 Minecraft 官方新闻失败：" + ex.Message); }
            }
            if (items == null)
            {
                // 网络或解析失败：优先回退 60 分钟内的本地缓存，否则给出明确错误。
                var cached = ReadCache();
                if (cached != null)
                {
                    try { items = Parse(cached.Value.Xml); fromCache = true; }
                    catch (Exception ex) { Log.Warn("解析缓存新闻失败：" + ex.Message); }
                }
            }
            if (items == null)
            {
                return new Result
                {
                    Error = xml == null
                        ? "无法连接到 Minecraft 官网，且本地没有可用缓存。请检查网络后重试。"
                        : "官方新闻内容无法解析，且本地没有可用缓存。请稍后重试。"
                };
            }

            try { items[0].ImageUrl = await ResolveImageUrlAsync(items[0].Link).ConfigureAwait(false) ?? FallbackImageUrl; }
            catch (Exception ex) { Log.Warn("获取新闻头图失败：" + ex.Message); items[0].ImageUrl = FallbackImageUrl; }
            return new Result { Items = items, FromCache = fromCache };
        }

        /// <summary>抓取新闻原文页的 meta og:image；任何失败都返回 null，由调用方回退固定头图。</summary>
        static async Task<string> ResolveImageUrlAsync(string link)
        {
            if (string.IsNullOrEmpty(link)) return null;
            string html;
            try { html = await Http.GetStringAsync(link, mirror: false, timeoutSec: 8).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("抓取新闻原文页失败：" + ex.Message); return null; }
            if (string.IsNullOrEmpty(html)) return null;
            // meta 标签里 property/name 与 content 的属性顺序不固定，先定位标签再取 content。
            var tag = Regex.Match(html, "<meta[^>]+(?:property|name)=[\"']og:image[\"'][^>]*>", RegexOptions.IgnoreCase);
            if (!tag.Success) return null;
            var content = Regex.Match(tag.Value, "content=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
            if (!content.Success) return null;
            return ToAbsolute(content.Groups[1].Value.Trim(), link);
        }

        static string ToAbsolute(string url, string baseUrl)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
            try { if (Uri.TryCreate(new Uri(baseUrl), url, out var u)) return u.ToString(); } catch { }
            return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
        }

        // ---------- 解析（RSS item：title / description / a10:link href / pubDate） ----------
        static List<Item> Parse(string xml)
        {
            var doc = XDocument.Parse(FixEntities(xml));
            var items = new List<Item>();
            foreach (var el in doc.Descendants("item"))
            {
                string Val(string local) => el.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value;
                var links = el.Elements().Where(e => e.Name.LocalName == "link").ToList();
                var atom = links.FirstOrDefault(e => e.Attribute("href") != null); // a10:link 用 href 属性
                var item = new Item
                {
                    Title = Clean(Val("title")),
                    Description = Clean(Val("description")),
                    Date = FormatDate(Val("pubDate")),
                    Link = ((atom?.Attribute("href")?.Value ?? links.FirstOrDefault()?.Value) ?? "").Trim()
                };
                items.Add(item);
            }
            items.RemoveAll(i => string.IsNullOrEmpty(i.Title) && string.IsNullOrEmpty(i.Link));
            if (items.Count == 0) throw new Exception("RSS 中没有可用的新闻条目。");
            return items.Take(MaxItems).ToList();
        }

        /// <summary>HTML 解码并去标签，压缩空白。</summary>
        static string Clean(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var s = WebUtility.HtmlDecode(html);
            s = Regex.Replace(s, "<[^>]+>", " ");
            s = Regex.Replace(s, "\\s+", " ");
            return s.Trim();
        }

        static string FormatDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            if (DateTimeOffset.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return dto.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return raw.Trim();
        }

        // RSS 里偶发出现 XML 未声明的 HTML 命名实体，逐个转成数字实体后再交给 XmlReader。
        static string FixEntities(string xml)
        {
            return Regex.Replace(xml, "&(nbsp|ensp|emsp|thinsp|mdash|ndash|hellip|copy|reg|trade|deg|plusmn|times|divide|rsquo|lsquo|rdquo|ldquo|raquo|laquo|middot|bull);",
                m => "&#" + (int)WebUtility.HtmlDecode("&" + m.Groups[1].Value + ";")[0] + ";");
        }

        // ---------- 缓存 ----------
        static (string Xml, DateTime Time)? ReadCache()
        {
            try
            {
                if (!File.Exists(CacheFile)) return null;
                var time = File.GetLastWriteTime(CacheFile);
                if (DateTime.Now - time > CacheTtl) return null;
                return (File.ReadAllText(CacheFile), time);
            }
            catch (Exception ex) { Log.Warn("读取新闻缓存失败：" + ex.Message); return null; }
        }

        static void WriteCache(string xml)
        {
            try { File.WriteAllText(CacheFile, xml); }
            catch (Exception ex) { Log.Warn("写入新闻缓存失败：" + ex.Message); }
        }
    }
}
