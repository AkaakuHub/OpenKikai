namespace LLMeta.App.Models;

public class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public string SampleText { get; set; } = "Hello, World!";
}
