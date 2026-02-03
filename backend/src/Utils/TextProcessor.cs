using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FlytIT.Chatbot.Utils;

public static class TextProcessor
{
    public static string NormalizeWhitespace(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        
        var sb = new StringBuilder(input.Length);
        bool wasWhitespace = false;
        
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!wasWhitespace) sb.Append(' ');
                wasWhitespace = true;
            }
            else
            {
                sb.Append(ch);
                wasWhitespace = false;
            }
        }
        
        return sb.ToString().Trim();
    }
    
    public static string TrimSnippet(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + " …";
}