namespace FlytIT.Chatbot.Utils;

public class ActiveFolderState
{
    public string CurrentFolder { get; private set; }
    public ActiveFolderState(string initialFolder) => CurrentFolder = initialFolder;
    public void Set(string folder) => CurrentFolder = folder;
}

public class IndexingOptions
{
    public string DefaultFolder { get; set; } = ConfigHelper.GetStringOrDefault("DEFAULT_FOLDER", "C:/Users/emili/Desktop/pdf_elasticsearch");
    public string Pattern { get; set; } = "*.pdf,*.docx,*.txt";
    public bool Recursive { get; set; } = true;
    public string? Site { get; set; }
}
