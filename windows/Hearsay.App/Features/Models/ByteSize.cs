using System.Globalization;

namespace Hearsay.App.Features.Models;

/// <summary>
/// Byte counts for the Models tab. Mirrors <c>ByteSize</c> in
/// mac/Hearsay/Features/Models/ModelRowView.swift: <see cref="Format"/>
/// follows <c>ByteCountFormatter</c> with the file count style (decimal
/// units; no decimals for bytes and KB, up to one for MB, up to two for GB
/// and above, trailing zeros dropped), <see cref="Short"/> the Mac's
/// one-decimal button label. Numbers follow the interface language's
/// culture, as the Mac's <c>InterfaceLanguageLaunch.applied.locale</c>.
/// </summary>
internal static class ByteSize
{
    private const double Kilo = 1_000;
    private const double Mega = 1_000_000;
    private const double Giga = 1_000_000_000;
    private const double Tera = 1_000_000_000_000;

    public static string Format(long bytes)
    {
        var culture = CultureInfo.CurrentCulture;
        double value = bytes;
        if (Math.Abs(value) < Kilo) return Strings.Bytes(bytes);
        if (Math.Abs(value) < Mega) return (value / Kilo).ToString("0", culture) + " KB";
        if (Math.Abs(value) < Giga) return (value / Mega).ToString("0.#", culture) + " MB";
        if (Math.Abs(value) < Tera) return (value / Giga).ToString("0.##", culture) + " GB";
        return (value / Tera).ToString("0.##", culture) + " TB";
    }

    /// <summary>One decimal place for GB, none for MB, for button labels like "1.6 GB".</summary>
    public static string Short(long bytes)
    {
        var culture = CultureInfo.CurrentCulture;
        double value = bytes;
        return value >= Giga
            ? (value / Giga).ToString("0.0", culture) + " GB"
            : (value / Mega).ToString("0", culture) + " MB";
    }
}
