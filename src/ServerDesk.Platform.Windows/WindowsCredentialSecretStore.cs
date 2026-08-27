using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Secrets;

namespace ServerDesk.Platform.Windows;

public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxCredentialBlobSize = 2560;
    private const string TargetPrefix = "ServerDesk:";

    public ValueTask SetAsync(
        SecretReference reference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(secret);

        var secretBytes = Encoding.Unicode.GetBytes(secret);
        if (secretBytes.Length > MaxCredentialBlobSize)
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                $"Credential exceeds the Windows Credential Manager {MaxCredentialBlobSize}-byte limit.");
        }

        var blob = Marshal.AllocCoTaskMem(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);

            var credential = new NativeCredential
            {
                Flags = 0,
                Type = CredentialTypeGeneric,
                TargetName = GetTargetName(reference),
                Comment = null,
                LastWritten = default,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = null,
                UserName = Environment.UserName,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            if (secretBytes.Length > 0)
            {
                Marshal.Copy(new byte[secretBytes.Length], 0, blob, secretBytes.Length);
            }

            Marshal.FreeCoTaskMem(blob);
            CryptographicOperations.ZeroMemory(secretBytes);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredRead(GetTargetName(reference), CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return ValueTask.FromResult<string?>(null);
            }

            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            var characterCount = checked((int)credential.CredentialBlobSize / sizeof(char));
            var value = characterCount == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, characterCount);
            return ValueTask.FromResult(value);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public ValueTask DeleteAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredDelete(GetTargetName(reference), CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static string GetTargetName(SecretReference reference) => TargetPrefix + reference.Value;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
