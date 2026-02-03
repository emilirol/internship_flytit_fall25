using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FlytIT.Chatbot.Utils;

public static class HtmlProcessor
{
    public static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        
        // Remove script and style tags with content
        html = Regex.Replace(html, @"(?is)<script.*?</script>", " ");
        html = Regex.Replace(html, @"(?is)<style.*?</style>", " ");
        html = Regex.Replace(html, @"(?is)<noscript.*?</noscript>", " ");
        
        // Remove all HTML tags
        html = Regex.Replace(html, @"(?s)<[^>]+>", " ");
        
        // HTML decode and normalize whitespace
        html = WebUtility.HtmlDecode(html);
        return TextProcessor.NormalizeWhitespace(html);
    }
    
    public static string StripTag(string html, string tagName)
    {
        var sb = new StringBuilder(html.Length);
        int i = 0;
        
        while (i < html.Length)
        {
            var startIndex = html.IndexOf($"<{tagName}", i, StringComparison.OrdinalIgnoreCase);
            if (startIndex < 0)
            {
                sb.Append(html, i, html.Length - i);
                break;
            }
            
            sb.Append(html, i, startIndex - i);
            var endIndex = html.IndexOf($"</{tagName}>", startIndex, StringComparison.OrdinalIgnoreCase);
            if (endIndex < 0) break;
            
            i = endIndex + tagName.Length + 3;
        }
        
        return sb.ToString();
    }
    
    public static string ExtractBetween(string html, string startTag, string endTag)
    {
        var startIndex = html.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0) return string.Empty;
        
        var endIndex = html.IndexOf(endTag, startIndex, StringComparison.OrdinalIgnoreCase);
        if (endIndex < 0) return string.Empty;
        
        var fragment = html.Substring(startIndex, endIndex - startIndex + endTag.Length);
        var gtIndex = fragment.IndexOf('>');
        
        return gtIndex >= 0 ? fragment[(gtIndex + 1)..(fragment.Length - endTag.Length)] : fragment;
    }
    
    public static IEnumerable<string> ExtractAllTexts(string html, string tagName)
    {
        int i = 0;
        while (i < html.Length)
        {
            var startIndex = html.IndexOf($"<{tagName}", i, StringComparison.OrdinalIgnoreCase);
            if (startIndex < 0) yield break;
            
            var gtIndex = html.IndexOf('>', startIndex);
            if (gtIndex < 0) yield break;
            
            var endIndex = html.IndexOf($"</{tagName}>", gtIndex, StringComparison.OrdinalIgnoreCase);
            if (endIndex < 0) yield break;
            
            var innerText = html[(gtIndex + 1)..endIndex];
            yield return StripHtml(innerText);
            
            i = endIndex + tagName.Length + 3;
        }
    }
    
    public static List<string> ExtractAllHrefs(string html)
    {
        var hrefs = new List<string>();
        int i = 0;
        
        while (i < html.Length)
        {
            var aIndex = html.IndexOf("<a", i, StringComparison.OrdinalIgnoreCase);
            if (aIndex < 0) break;
            
            var gtIndex = html.IndexOf('>', aIndex);
            if (gtIndex < 0) break;
            
            var segment = html[aIndex..gtIndex];
            var hrefIndex = segment.IndexOf("href=", StringComparison.OrdinalIgnoreCase);
            
            if (hrefIndex >= 0)
            {
                var start = hrefIndex + 5;
                if (start < segment.Length)
                {
                    var quote = segment[start];
                    if (quote == '"' || quote == '\'')
                    {
                        var end = segment.IndexOf(quote, start + 1);
                        if (end > start) hrefs.Add(segment[(start + 1)..end]);
                    }
                }
            }
            
            i = gtIndex + 1;
        }
        
        return hrefs;
    }
    
    public static List<string> ExtractAllImgSrc(string html)
    {
        var sources = new List<string>();
        int i = 0;
        
        while (i < html.Length)
        {
            var imgIndex = html.IndexOf("<img", i, StringComparison.OrdinalIgnoreCase);
            if (imgIndex < 0) break;
            
            var gtIndex = html.IndexOf('>', imgIndex);
            if (gtIndex < 0) break;
            
            var segment = html[imgIndex..gtIndex];
            var srcIndex = segment.IndexOf("src=", StringComparison.OrdinalIgnoreCase);
            
            if (srcIndex >= 0)
            {
                var start = srcIndex + 4;
                var quote = segment[start];
                if (quote == '"' || quote == '\'')
                {
                    var end = segment.IndexOf(quote, start + 1);
                    if (end > start) sources.Add(segment[(start + 1)..end]);
                }
            }
            
            i = gtIndex + 1;
        }
        
        return sources;
    }
}