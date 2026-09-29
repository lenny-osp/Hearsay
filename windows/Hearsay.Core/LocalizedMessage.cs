using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Hearsay.Core;

/// <summary>
/// A user-facing Core text the app can translate: the key it was built from
/// and the values put into it, the pattern of <c>LanguageNotice</c>
/// generalized (PLAN.md 18.3, "Localization"). The Mac builds these texts with
/// <c>String(localized:)</c> in HearsayCore, whose key is the English with
/// <c>%@</c> (or <c>%lld</c>, <c>%d</c>) for every inserted value; Windows
/// keeps the English as the exception's <c>Message</c> and hands the app
/// the same key and values, so <c>Strings.Describe</c> looks the key up in
/// its <c>.resw</c> (generated from shared/localization) and formats the
/// translation with <see cref="MessageArguments"/>.
/// </summary>
public interface ILocalizedMessage
{
    /// <summary>The key in shared/localization/strings-en.json, placeholders as the Mac writes them.</summary>
    string MessageKey { get; }

    /// <summary>
    /// The values of the key's placeholders, in order: strings, integers,
    /// or <see cref="ILocalizedMessage"/> (translated in turn).
    /// </summary>
    IReadOnlyList<object> MessageArguments { get; }

    /// <summary>
    /// Text the Mac appends after the formatted key, each part after its
    /// separator (a CLI's output excerpt, a hint); empty for most messages.
    /// </summary>
    IReadOnlyList<MessagePart> MessageTail { get; }
}

/// <summary>
/// One piece appended after a message: <see cref="Separator"/> (never
/// translated, for example "\n"), then <see cref="Content"/>, a verbatim
/// string or an <see cref="ILocalizedMessage"/>.
/// </summary>
public sealed record MessagePart(string Separator, object Content);

/// <summary>
/// An error or result whose text may have a catalog key. <see cref="LocalizedMessage"/>
/// is null for a text that has no key in shared/localization (Windows-only
/// wording not added there yet); the app then shows <c>Message</c> as it is.
/// </summary>
public interface ILocalizedError
{
    ILocalizedMessage? LocalizedMessage { get; }
}

/// <summary>The one implementation of <see cref="ILocalizedMessage"/>, and the formatting both platforms' English follows.</summary>
public sealed partial class LocalizedMessage : ILocalizedMessage, IEquatable<LocalizedMessage>
{
    public LocalizedMessage(string key, params object[] arguments)
        : this(key, arguments, [])
    {
    }

    public LocalizedMessage(string key, IReadOnlyList<object> arguments, IReadOnlyList<MessagePart> tail)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(tail);
        MessageKey = key;
        MessageArguments = arguments;
        MessageTail = tail;
    }

    public string MessageKey { get; }

    public IReadOnlyList<object> MessageArguments { get; }

    public IReadOnlyList<MessagePart> MessageTail { get; }

    /// <summary>This message with <paramref name="content"/> appended after <paramref name="separator"/>.</summary>
    public LocalizedMessage Appending(string separator, object content) =>
        new(MessageKey, MessageArguments, [.. MessageTail, new MessagePart(separator, content)]);

    /// <summary>The English text: the key with its placeholders filled (invariant culture), then the tail.</summary>
    public string English => Format(this, _ => null, CultureInfo.InvariantCulture);

    public override string ToString() => English;

    /// <summary>
    /// <paramref name="message"/> in the language <paramref name="lookup"/>
    /// serves: it returns the translation of a key as the <c>.resw</c> holds
    /// it (a .NET format string when the key has placeholders), or null to
    /// use the English key. Nested messages are looked up the same way.
    /// </summary>
    public static string Format(ILocalizedMessage message, Func<string, string?> lookup, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(lookup);
        var builder = new StringBuilder();
        var key = message.MessageKey;
        var hasFormat = HasFormat(key);
        var value = lookup(key) ?? (hasFormat ? ToDotNetFormat(key) : key);
        if (hasFormat)
        {
            var arguments = message.MessageArguments.Select(argument => Resolve(argument, lookup, culture)).ToArray();
            builder.Append(string.Format(culture, value, arguments));
        }
        else
        {
            builder.Append(value);
        }
        foreach (var part in message.MessageTail)
        {
            builder.Append(part.Separator).Append(Resolve(part.Content, lookup, culture));
        }
        return builder.ToString();
    }

    /// <summary>Whether a catalog text has placeholders (or <c>%%</c>), as windows/scripts/import-strings.py decides.</summary>
    public static bool HasFormat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FormatToken().IsMatch(text);
    }

    /// <summary>
    /// A Mac catalog text as a .NET composite format string, as
    /// windows/scripts/import-strings.py converts it: braces doubled,
    /// <c>%%</c> to <c>%</c>, plain placeholders numbered <c>{0}</c>,
    /// <c>{1}</c>, …, <c>%n$</c> to <c>{n-1}</c>.
    /// </summary>
    public static string ToDotNetFormat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder();
        var counter = 0;
        var last = 0;
        foreach (Match match in FormatToken().Matches(text))
        {
            builder.Append(Escape(text[last..match.Index]));
            if (match.Value == "%%")
            {
                builder.Append('%');
            }
            else if (match.Groups[1].Success)
            {
                builder.Append('{').Append(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1).Append('}');
            }
            else
            {
                builder.Append('{').Append(counter++).Append('}');
            }
            last = match.Index + match.Length;
        }
        builder.Append(Escape(text[last..]));
        return builder.ToString();
    }

    public bool Equals(LocalizedMessage? other) =>
        other is not null && MessageKey == other.MessageKey
        && MessageArguments.SequenceEqual(other.MessageArguments) && MessageTail.SequenceEqual(other.MessageTail);

    public override bool Equals(object? obj) => Equals(obj as LocalizedMessage);

    public override int GetHashCode() => HashCode.Combine(MessageKey, MessageArguments.Count, MessageTail.Count);

    private static object Resolve(object value, Func<string, string?> lookup, IFormatProvider culture) =>
        value is ILocalizedMessage nested ? Format(nested, lookup, culture) : value;

    private static string Escape(string text) =>
        text.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);

    /// <summary>The placeholder rule of mac/Scripts/export-strings.py and import-strings.py.</summary>
    [GeneratedRegex(@"%%|%(?:(\d+)\$)?[-+ #0']*(?:\d+|\*)?(?:\.(?:\d+|\*))?(?:hh|h|ll|l|q|z|t|j|L)?[@dDuUxXoOfFeEgGcCsSaAp]")]
    private static partial Regex FormatToken();
}
