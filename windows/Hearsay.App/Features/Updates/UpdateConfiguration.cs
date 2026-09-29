using System.Reflection;
using Hearsay.Core.Updates;

namespace Hearsay.App.Features.Updates;

/// <summary>
/// Where this build looks for updates: the Mac's Info.plist key
/// <c>HearsayUpdateRepository</c> (<c>UpdateService.repositoryKey</c> in
/// mac/Hearsay/Features/Updates/UpdateService.swift), kept in one place. It is
/// Core's <see cref="UpdateChecker.HearsayRepository"/>, which a Core test
/// compares with <c>HEARSAY_UPDATE_REPOSITORY</c> in mac/project.yml, so both
/// platforms read the same releases (PLAN.md 18.4, "Updates and packaging").
/// </summary>
internal static class UpdateConfiguration
{
    /// <summary>The "owner/name" slug of the GitHub repository.</summary>
    public const string Repository = UpdateChecker.HearsayRepository;
}

/// <summary>
/// This build's version as Settings > General shows it, the Mac's
/// <c>CFBundleShortVersionString</c> and <c>CFBundleVersion</c> in
/// <c>UpdateService.versionLine</c> (mac/Hearsay/Features/Updates/UpdateService.swift).
/// Windows has one version, the <c>Version</c> MSBuild property (0.0.0 for
/// local builds; the release script passes <c>-p:Version=&lt;v&gt;</c>), which
/// the SDK writes as the informational version with <c>+&lt;commit&gt;</c>
/// appended; the build shown in parentheses is that commit, shortened to 7
/// digits, or "0" without one (the Mac shows its build number there).
/// </summary>
/// <param name="Version">The release version, for example "0.3.0", compared with GitHub's tag.</param>
/// <param name="Build">The commit, or "0".</param>
internal sealed record AppVersion(string Version, string Build)
{
    /// <summary>The running Hearsay.exe's version.</summary>
    public static AppVersion Current { get; } = FromAssembly(typeof(AppVersion).Assembly);

    /// <summary>"Version 0.3.0 (a0942f3)" in the interface language.</summary>
    public string Line => Strings.UpdateVersionLine(Version, Build);

    public static AppVersion FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3));
    }

    /// <summary>"0.3.0+a0942f3e…" gives 0.3.0 and a0942f3; an empty text gives 0.0.0 and 0.</summary>
    public static AppVersion Parse(string? informationalVersion)
    {
        var text = (informationalVersion ?? "").Trim();
        var version = UpdateChecker.WithoutBuildMetadata(text).Trim();
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        var build = plus < 0 ? "" : text[(plus + 1)..].Trim();
        if (build.Length > 7 && build.All(char.IsAsciiHexDigit)) build = build[..7];
        return new AppVersion(version.Length == 0 ? "0.0.0" : version, build.Length == 0 ? "0" : build);
    }
}
