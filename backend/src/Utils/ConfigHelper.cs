namespace FlytIT.Chatbot.Utils;

public static class ConfigHelper
{
    public static int GetIntOrDefault(string envVarName, int defaultValue)
        => int.TryParse(Environment.GetEnvironmentVariable(envVarName), out var value) && value > 0 
            ? value : defaultValue;
    
    public static bool GetBoolOrDefault(string envVarName, bool defaultValue)
        => bool.TryParse(Environment.GetEnvironmentVariable(envVarName), out var value) 
            ? value : defaultValue;
    
    public static string GetStringOrDefault(string envVarName, string defaultValue)
        => Environment.GetEnvironmentVariable(envVarName) ?? defaultValue;
}