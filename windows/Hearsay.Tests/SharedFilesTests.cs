namespace Hearsay.Tests;

public class SharedFilesTests
{
    [Fact]
    public void SharedFolderIsFound()
    {
        Assert.True(File.Exists(SharedFiles.Path("naming-tests.json")));
        Assert.True(File.Exists(SharedFiles.Path("prompts", "general-meeting.txt")));
    }

    [Fact]
    public void SharedTextFilesHaveNoCarriageReturns()
    {
        // The byte-compared files must be LF (AGENTS.md, Line endings).
        foreach (var rel in new[] { "prompts/general-meeting.txt", "prompts/response-rules.txt",
                                    "prompts/system-message.txt", "fixtures/en-30s.expected.srt",
                                    "naming-tests.json", "language-decision-tests.json" })
        {
            var text = SharedFiles.ReadText(rel.Split('/'));
            Assert.DoesNotContain('\r', text);
        }
    }

    [Fact]
    public void PromptFilesHaveNoTrailingNewline()
    {
        foreach (var name in new[] { "general-meeting.txt", "response-rules.txt", "system-message.txt" })
        {
            Assert.False(SharedFiles.ReadText("prompts", name).EndsWith('\n'), name);
        }
    }
}
