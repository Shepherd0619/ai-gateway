using AiGateway.Proxy;
using Xunit;

namespace AiGateway.Tests;

public sealed class StopSequenceMatcherTests
{
    [Theory]
    [InlineData("allow</decision>hidden", "allow", "</decision>")]
    [InlineData("allow</dec", "allow</dec", null)]
    [InlineData("allow", "allow", null)]
    public void ChunkBoundaries_DoNotChangeVisibleOutput(string text, string expected, string? stop)
    {
        for (var boundary = 0; boundary <= text.Length; boundary++)
        {
            var matcher = new StopSequenceMatcher(["</decision>"]);
            var output = matcher.Append(text[..boundary]) + matcher.Append(text[boundary..]) + matcher.Flush();
            Assert.Equal(expected, output);
            Assert.Equal(stop, matcher.Matched);
            if (stop is not null) Assert.Equal("", matcher.Append("later"));
        }
    }

    [Theory]
    [InlineData("abcde", "", "abcde")]
    [InlineData("abc", "a", "bc")]
    [InlineData("abcX", "a", "bc")]
    public void EarlierPartialMatch_ResolvesConsistentlyAcrossChunks(string text, string expected, string stop)
    {
        for (var boundary = 0; boundary <= text.Length; boundary++)
        {
            var matcher = new StopSequenceMatcher(["abcde", "bc"]);
            Assert.Equal(expected, matcher.Append(text[..boundary]) + matcher.Append(text[boundary..]) + matcher.Flush());
            Assert.Equal(stop, matcher.Matched);
        }
    }
}
