using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Hearsay.Core.Settings;

/// <summary>
/// Stores API tokens. Hearsay keeps one token per provider preset id.
/// Port of the <c>SecretStore</c> protocol in
/// mac/HearsayCore/Sources/HearsayCore/Settings/SecretStore.swift.
/// </summary>
public interface ISecretStore
{
    /// <summary>The stored secret, or null when there is none.</summary>
    string? Read(string account);

    /// <summary>Stores <paramref name="secret"/>, replacing any earlier one.</summary>
    void Write(string secret, string account);

    /// <summary>Removes the secret; nothing happens when there is none.</summary>
    void Delete(string account);
}

/// <summary>A Credential Manager call failed with the Win32 error <see cref="ErrorCode"/>.</summary>
public sealed class SecretStoreException : Exception
{
    public SecretStoreException()
        : this(0)
    {
    }

    public SecretStoreException(string message)
        : base(message)
    {
    }

    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SecretStoreException(int errorCode)
        : base(Describe(errorCode))
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }

    /// <summary>
    /// "Credential Manager error: {detail}", the Mac's "Keychain error: %@"
    /// with the message Windows gives for the error. English until W7.
    /// </summary>
    private static string Describe(int errorCode)
    {
        var detail = new Win32Exception(errorCode).Message.TrimEnd().TrimEnd('.');
        return $"Credential Manager error: {detail}";
    }
}

/// <summary>
/// Generic credentials in Windows Credential Manager, target name
/// <c>Hearsay/&lt;account&gt;</c>, persisted for this user on this machine
/// only (CRED_PERSIST_LOCAL_MACHINE; the Mac uses
/// <c>kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly</c>). The secret is
/// stored as UTF-8, as on the Mac, which leaves room for 2,560 bytes.
/// Port of <c>KeychainSecretStore</c>.
/// </summary>
public sealed class CredentialManagerSecretStore : ISecretStore
{
    public const string DefaultTargetPrefix = "Hearsay/";

    public CredentialManagerSecretStore(string targetPrefix = DefaultTargetPrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPrefix);
        TargetPrefix = targetPrefix;
    }

    /// <summary>Prefixed to every account to make the target name (the Mac's keychain service).</summary>
    public string TargetPrefix { get; }

    /// <summary>The Credential Manager target name for <paramref name="account"/>.</summary>
    public string TargetName(string account)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        return TargetPrefix + account;
    }

    public string? Read(string account)
    {
        var target = TargetName(account);
        if (!NativeMethods.CredRead(target, NativeMethods.CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NativeMethods.ErrorNotFound) return null;
            throw new SecretStoreException(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return string.Empty;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public void Write(string secret, string account)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var target = TargetName(account);
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeMethods.Credential
            {
                Type = NativeMethods.CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = NativeMethods.CredPersistLocalMachine,
                UserName = account,
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw new SecretStoreException(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            // The token should not linger in freed memory.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public void Delete(string account)
    {
        var target = TargetName(account);
        if (NativeMethods.CredDelete(target, NativeMethods.CredTypeGeneric, 0)) return;
        var error = Marshal.GetLastPInvokeError();
        if (error != NativeMethods.ErrorNotFound) throw new SecretStoreException(error);
    }

    private static class NativeMethods
    {
        public const uint CredTypeGeneric = 1;
        public const uint CredPersistLocalMachine = 2;
        public const int ErrorNotFound = 1168;

        /// <summary>CREDENTIALW (wincred.h).</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public uint LastWrittenLow;
            public uint LastWrittenHigh;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string targetName, uint type, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern void CredFree(IntPtr buffer);
    }
}

/// <summary>Process-local secret store for tests and previews. Port of <c>InMemorySecretStore</c>. Thread-safe.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, string> secrets;

    public InMemorySecretStore(IReadOnlyDictionary<string, string>? secrets = null)
    {
        this.secrets = secrets is null ? [] : new Dictionary<string, string>(secrets);
    }

    public string? Read(string account)
    {
        lock (gate)
        {
            return secrets.TryGetValue(account, out var secret) ? secret : null;
        }
    }

    public void Write(string secret, string account)
    {
        lock (gate)
        {
            secrets[account] = secret;
        }
    }

    public void Delete(string account)
    {
        lock (gate)
        {
            secrets.Remove(account);
        }
    }
}
