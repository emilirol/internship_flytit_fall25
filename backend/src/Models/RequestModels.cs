namespace FlytIT.Chatbot.Models;

public record ChatReq(string Message, string? Site);
public record SetFolderReq(string Folder);
public record IndexNowReq(string? Pattern = null, bool? Recursive = null, string? Site = null);
public record IngestFolderReq(
    string Folder,
    bool Recursive = true,
    string? Site = null,
    string Pattern = "*.pdf",
    int MaxConcurrency = 2);