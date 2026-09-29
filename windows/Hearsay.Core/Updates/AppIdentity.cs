using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Hearsay.Core.Updates;

/// <summary>What WinVerifyTrust says about a file's Authenticode signature.</summary>
public enum SignatureStatus
{
    /// <summary>No embedded signature (or not a signable file).</summary>
    NotSigned,

    /// <summary>The signature is intact and chains to a trusted root.</summary>
    Trusted,

    /// <summary>
    /// The signature is intact but its certificate does not chain to a
    /// trusted root: the self-signed "Hearsay Code Signing" identity (the
    /// Mac's self-signed certificate has the same standing with Gatekeeper).
    /// </summary>
    UntrustedRoot,

    /// <summary>The file was changed after signing, or the certificate is revoked, distrusted or expired.</summary>
    Invalid,
}

/// <summary>An Authenticode signature: its state and, when signed, the signer's certificate.</summary>
/// <param name="Subject">The signing certificate's subject, for example <c>CN=Hearsay Code Signing (self-signed)</c>.</param>
/// <param name="Thumbprint">SHA-256 of the signing certificate, uppercase hex.</param>
/// <param name="Detail">WinVerifyTrust's result for the log and error texts.</param>
public sealed record AuthenticodeInfo(SignatureStatus Status, string? Subject, string? Thumbprint, string? Detail)
{
    public static AuthenticodeInfo NotSigned { get; } = new(SignatureStatus.NotSigned, null, null, "not signed");

    /// <summary>Intact, whether or not the root is trusted.</summary>
    public bool IsSigned => Status is SignatureStatus.Trusted or SignatureStatus.UntrustedRoot;
}

/// <summary>
/// What the update install checks about a <c>Hearsay.exe</c>: the Windows
/// counterpart of the Mac's Info.plist keys and designated requirement read
/// in <c>UpdatePackage.verifySignature</c> (mac/Hearsay/Features/Updates/UpdatePackage.swift).
/// </summary>
/// <param name="ProductName">The version resource's ProductName (the Mac's <c>CFBundleIdentifier</c> check).</param>
/// <param name="ProductVersion">The version resource's ProductVersion, which the .NET SDK fills from the informational version (<c>0.3.0+&lt;commit&gt;</c>).</param>
public sealed record AppIdentity(string? ProductName, string? ProductVersion, AuthenticodeInfo Signature);

/// <summary>Reads an <see cref="AppIdentity"/>; tests substitute a fake for fake executables.</summary>
public interface IAppIdentityReader
{
    AppIdentity Read(string exePath);
}

/// <summary>
/// Reads the version resource with <see cref="FileVersionInfo"/> and the
/// Authenticode signature with WinVerifyTrust (no revocation check, no
/// network, no UI) plus the signer certificate from the file.
/// </summary>
public sealed class FileAppIdentityReader : IAppIdentityReader
{
    public static FileAppIdentityReader Shared { get; } = new();

    public AppIdentity Read(string exePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        var info = FileVersionInfo.GetVersionInfo(exePath);
        var signature = OperatingSystem.IsWindows() ? Authenticode.Read(exePath) : AuthenticodeInfo.NotSigned;
        return new AppIdentity(Blank(info.ProductName), Blank(info.ProductVersion), signature);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>WinVerifyTrust through P/Invoke (wintrust.dll).</summary>
[SupportedOSPlatform("windows")]
internal static class Authenticode
{
    private const uint TrustENoSignature = 0x800B0100;
    private const uint TrustESubjectFormUnknown = 0x800B0003;
    private const uint TrustEProviderUnknown = 0x800B0001;
    private const uint CertEUntrustedRoot = 0x800B0109;
    private const uint CertEChaining = 0x800B010A;
    private const uint CertEUntrustedTestRoot = 0x800B010D;

    public static AuthenticodeInfo Read(string path)
    {
        var (code, signer) = NativeMethods.Verify(Path.GetFullPath(path));
        var result = (uint)code;
        var status = result switch
        {
            0 => SignatureStatus.Trusted,
            TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => SignatureStatus.NotSigned,
            CertEUntrustedRoot or CertEChaining or CertEUntrustedTestRoot => SignatureStatus.UntrustedRoot,
            _ => SignatureStatus.Invalid,
        };
        if (status == SignatureStatus.NotSigned)
        {
            signer?.Dispose();
            return AuthenticodeInfo.NotSigned;
        }
        var detail = result == 0 ? "valid" : $"0x{result:X8} {new Win32Exception(code).Message}";
        using (signer)
        {
            return new AuthenticodeInfo(
                status, signer?.Subject, signer?.GetCertHashString(HashAlgorithmName.SHA256), detail);
        }
    }

    private static class NativeMethods
    {
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private const uint WtdRevocationCheckNone = 0x00000010;
        private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

        /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2.</summary>
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            public IntPtr FilePath;
            public IntPtr File;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProvFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(
            IntPtr providerData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificateIndex);

        /// <summary>
        /// The leaf certificate of the first signer, read from the verified
        /// state before it is closed (CRYPT_PROVIDER_CERT.pCert follows its
        /// DWORD size, pointer-aligned). The certificate context is duplicated,
        /// so it outlives the state.
        /// </summary>
        private static X509Certificate2? Signer(IntPtr stateData)
        {
            if (stateData == IntPtr.Zero)
            {
                return null;
            }
            var provider = WTHelperProvDataFromStateData(stateData);
            var signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            var certificate = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero)
            {
                return null;
            }
            var context = Marshal.ReadIntPtr(certificate, IntPtr.Size);
            return context == IntPtr.Zero ? null : new X509Certificate2(context);
        }

        public static (int Result, X509Certificate2? Signer) Verify(string path)
        {
            var filePath = Marshal.StringToCoTaskMemUni(path);
            var fileInfo = IntPtr.Zero;
            try
            {
                var file = new WinTrustFileInfo
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    FilePath = filePath,
                };
                fileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(file, fileInfo, fDeleteOld: false);
                var data = new WinTrustData
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = WtdUiNone,
                    RevocationChecks = WtdRevokeNone,
                    UnionChoice = WtdChoiceFile,
                    FileInfo = fileInfo,
                    StateAction = WtdStateActionVerify,
                    ProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
                };
                var action = GenericVerifyV2;
                var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                X509Certificate2? signerCertificate = null;
                try
                {
                    signerCertificate = Signer(data.StateData);
                }
                catch (CryptographicException)
                {
                    // Signed per WinVerifyTrust but no readable signer: keep the result.
                }
                data.StateAction = WtdStateActionClose;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return (result, signerCertificate);
            }
            finally
            {
                if (fileInfo != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(fileInfo);
                }
                Marshal.FreeCoTaskMem(filePath);
            }
        }
    }
}
