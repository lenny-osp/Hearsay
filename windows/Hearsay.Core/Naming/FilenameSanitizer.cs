using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Hearsay.Core.Naming;

/// <summary>
/// Port of mac/HearsayCore/Sources/HearsayCore/Naming/FilenameSanitizer.swift
/// (itself a port of <c>sanitize_ai_filename</c> from whisper-tools <c>run_whisper.py</c>).
///
/// Produces a lowercase ASCII slug of letters, digits, and hyphens, capped at
/// 80 characters, or null when nothing usable remains.
/// </summary>
public static partial class FilenameSanitizer
{
    public const int MaxLength = 80;

    [GeneratedRegex(@"\.(?:md|srt)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingExtension();

    public static string? Sanitize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        // NFKC, then trim surrounding whitespace.
        var value = ReplaceLoneSurrogates(raw).Normalize(NormalizationForm.FormKC).Trim();
        // Drop a trailing .md / .srt extension (case-insensitive).
        value = TrailingExtension().Replace(value, "");
        // NFKD, lowercase, drop combining marks (accents).
        value = value.Normalize(NormalizationForm.FormKD).ToLowerInvariant();

        // Replace each run of characters outside [a-z0-9-] with one hyphen.
        var slug = new StringBuilder();
        var inRun = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (HasCombiningClass(rune))
            {
                continue;
            }
            if (IsAllowed(rune))
            {
                slug.Append((char)rune.Value);
                inRun = false;
            }
            else if (!inRun)
            {
                slug.Append('-');
                inRun = true;
            }
        }

        // strip("-"), collapse "-+" to "-", cap, then rstrip("-").
        var trimmed = slug.ToString().Trim('-');
        var collapsed = new StringBuilder();
        foreach (var character in trimmed)
        {
            if (character == '-' && collapsed.Length > 0 && collapsed[^1] == '-')
            {
                continue;
            }
            collapsed.Append(character);
        }
        var capped = collapsed.ToString();
        if (capped.Length > MaxLength)
        {
            capped = capped[..MaxLength];
        }
        capped = capped.TrimEnd('-');
        return capped.Length == 0 ? null : capped;
    }

    private static bool IsAllowed(Rune rune) =>
        rune.Value is (>= 0x61 and <= 0x7A) or (>= 0x30 and <= 0x39) or 0x2D;

    /// <summary>
    /// True when the scalar's canonical combining class is not 0, the test the
    /// Swift code makes with <c>canonicalCombiningClass == .notReordered</c>.
    /// .NET exposes no combining class, so it is derived from canonical
    /// ordering: NFD reorders a mark with class above 1 in front of U+0334
    /// (class 1). The few class-1 marks (overlays) cannot be told from class 0
    /// that way and are listed (Unicode 16).
    /// </summary>
    private static bool HasCombiningClass(Rune rune)
    {
        if (rune.Value < 0x300)
        {
            return false;
        }
        var category = Rune.GetUnicodeCategory(rune);
        if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark))
        {
            return false;
        }
        if (IsOverlayClass(rune.Value))
        {
            return true;
        }
        var probe = "a" + rune.ToString() + "̴";
        return !string.Equals(probe.Normalize(NormalizationForm.FormD), probe, StringComparison.Ordinal);
    }

    private static bool IsOverlayClass(int value) =>
        value is (>= 0x0334 and <= 0x0338) or 0x1CD4 or (>= 0x1CE2 and <= 0x1CE8)
            or (>= 0x20D2 and <= 0x20D3) or (>= 0x20D8 and <= 0x20DA) or (>= 0x20E5 and <= 0x20E6)
            or (>= 0x20EA and <= 0x20EB) or 0x10A39 or (>= 0x16AF0 and <= 0x16AF4) or 0x1BC9E
            or (>= 0x1D167 and <= 0x1D169);

    /// <summary>
    /// A Swift string cannot hold a lone surrogate (it becomes U+FFFD when
    /// decoded), and .NET normalization throws on one, so replace them first.
    /// </summary>
    private static string ReplaceLoneSurrogates(string value)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var paired = char.IsHighSurrogate(character) && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]);
            if (paired)
            {
                builder?.Append(character).Append(value[index + 1]);
                index++;
                continue;
            }
            if (char.IsSurrogate(character))
            {
                builder ??= new StringBuilder(value, 0, index, value.Length);
                builder.Append('�');
                continue;
            }
            builder?.Append(character);
        }
        return builder?.ToString() ?? value;
    }
}
