namespace FlytIT.Chatbot.Models;

public class Doc
{
    public string? Title { get; set; }
    public string? Content { get; set; }
    public float[]? Embedding { get; set; }
    public string? Site { get; set; }
    public string? SourcePath { get; set; }
}