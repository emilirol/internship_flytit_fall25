namespace FlytIT.Chatbot.Utils;

public static class AppConstants
{
    // Image processing
    public const int DEFAULT_RENDER_WIDTH = 1080;
    public const int DEFAULT_RENDER_HEIGHT = 1920;
    public const int DEFAULT_TARGET_WIDTH = 1280;
    public const int DEFAULT_CAPTION_MAX_WIDTH = 1024;
    public const int DEFAULT_CAPTION_TIMEOUT_SECONDS = 30;
    public const int DEFAULT_CAPTION_MAX_CONCURRENCY = 1;
    public const int DEFAULT_CAPTION_MAX_RETRIES = 2;
    public const int DEFAULT_CRAWL_CAPTION_MAX_IMAGES = 3;
    
    // Text processing
    public const int DEFAULT_TEXT_MIN_CHARS = 50;
    public const int DEFAULT_SNIPPET_MAX_LENGTH = 700;
    public const int DEFAULT_CONTEXT_MAX_LENGTH = 800;
    public const int DEFAULT_CONTENT_MAX_LENGTH = 1200;
    
    // Search and retrieval
    public const int DEFAULT_MAX_PAGES = 200;
    public const int DEFAULT_SEARCH_SIZE = 5;
    public const int DEFAULT_KNN_K = 20;
    public const int DEFAULT_KNN_CANDIDATES = 100;
    public const int DEFAULT_RRF_K = 60;
    public const int DEFAULT_TAKE_RESULTS = 6;
    
    // File processing
    public const int DEFAULT_MAX_CONCURRENCY = 2;
    public const string DEFAULT_FILE_PATTERN = "*.pdf,*.docx,*.txt";
    
    // Models
    public const string DEFAULT_EMBEDDING_MODEL = "text-embedding-3-small";
    public const string DEFAULT_CHAT_MODEL = "gpt-4o-mini";
    public const string DEFAULT_VISION_MODEL = "gpt-4o-mini";
    
    // Index
    public const int DEFAULT_EMBEDDING_DIMENSIONS = 1536;
}