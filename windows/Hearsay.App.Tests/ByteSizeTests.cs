using Hearsay.App.Features.Models;
using Hearsay.App.Features.Notes;
using Hearsay.Core.Settings;

namespace Hearsay.App.Tests;

/// <summary>
/// <see cref="ByteSize"/>, the Mac's <c>ByteSize</c> in
/// mac/Hearsay/Features/Models/ModelRowView.swift (<c>ByteCountFormatter</c>,
/// file style: decimal units, no decimals for bytes and KB, up to one for
/// MB, up to two for GB), and the path shortening of the notes flow.
/// </summary>
public sealed class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(1L, "1 byte")]
    [InlineData(999L, "999 bytes")]
    [InlineData(1_000L, "1 KB")]
    [InlineData(1_499L, "1 KB")]
    [InlineData(999_499L, "999 KB")]
    [InlineData(1_000_000L, "1 MB")]
    [InlineData(1_550_000L, "1.6 MB")]
    [InlineData(574_041_195L, "574 MB")]
    [InlineData(1_000_000_000L, "1 GB")]
    [InlineData(1_624_417_792L, "1.62 GB")]
    [InlineData(1_500_000_000L, "1.5 GB")]
    [InlineData(2_000_000_000_000L, "2 TB")]
    public void FormatFollowsByteCountFormatter(long bytes, string expected)
    {
        using var english = new InterfaceLanguageScope(InterfaceLanguage.English, "en-US");
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Theory]
    [InlineData(1_624_417_792L, "1.6 GB")]
    [InlineData(1_000_000_000L, "1.0 GB")]
    [InlineData(999_999_999L, "1000 MB")]
    [InlineData(574_041_195L, "574 MB")]
    [InlineData(147_951_465L, "148 MB")]
    public void ShortIsTheButtonLabel(long bytes, string expected)
    {
        using var english = new InterfaceLanguageScope(InterfaceLanguage.English, "en-US");
        Assert.Equal(expected, ByteSize.Short(bytes));
    }

    [Fact]
    public void NumbersFollowTheInterfaceLanguage()
    {
        using var german = new InterfaceLanguageScope(InterfaceLanguage.German, "de-DE");
        Assert.Equal("1,62 GB", ByteSize.Format(1_624_417_792));
        Assert.Equal("1,6 GB", ByteSize.Short(1_624_417_792));
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "windows", "%@ bytes", 12L), ByteSize.Format(12));
    }

    [Theory]
    [InlineData("short", 10, "short")]
    [InlineData("0123456789", 10, "0123456789")]
    [InlineData("0123456789A", 10, "01234…789A")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", 11, "abcde…vwxyz")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", 3, "a…z")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", 2, "abcdefghijklmnopqrstuvwxyz")]
    public void MiddleTruncation(string text, int limit, string expected)
    {
        var result = NotesFlowView.MiddleTruncated(text, limit);
        Assert.Equal(expected, result);
        if (text.Length > limit && limit >= 3) Assert.Equal(limit, result.Length);
    }
}
