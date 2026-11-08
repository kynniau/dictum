using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Dictum.Platform;
using Dictum.Settings;

namespace Dictum.Windows.Shell;

/// <summary>
/// API keys in Windows Credential Manager (decision D1, 30/10/2026): one generic credential per key, target name
/// <c>Dictum/{VARIABLE}/{id}</c>, user name the variable, the key as a UTF-8 blob, persisted for this computer only. The labels
/// are in keys.json (<see cref="SecretVault"/>), never in the credential.
/// <para>
/// Read on Microsoft Learn on 30/10/2026 before this was written:
/// (i) CRED_PERSIST_LOCAL_MACHINE "persists for all subsequent logon sessions on this same computer. It is visible to other logon
/// sessions of this same user on this same computer and not visible to logon sessions for this user on other computers"
/// (https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw, Persist). Roaming with a Microsoft account
/// is documented for the WinRT Credential Locker (PasswordVault), a different API this class does not use
/// (https://learn.microsoft.com/windows/apps/develop/security/credential-locker).
/// (ii) Inferred from two Learn pages, not verified on the packaged build (a check per docs/TESTING-MSIX.md is required before 1.4
/// ships): CredWrite's requirements say "desktop apps only" (https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew),
/// and a full-trust packaged app "run[s] with the same permissions as a standard desktop app"
/// (https://learn.microsoft.com/windows/msix/msix-containerization-overview); Dictum's package declares runFullTrust and
/// Windows.FullTrustApplication (packaging/AppxManifest.template.xml). No Learn page names CredWrite and MSIX together, and the
/// packaged build has not been run against this class yet.
/// </para>
/// </summary>
internal sealed class CredentialManagerStore : ICredentialStore
{
    private const string Prefix = "Dictum/";
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public bool Available => true;

    public string? Name => "Windows Credential Manager";

    internal static string Target(string variable, string id) => $"{Prefix}{variable}/{id}";

    public void Write(string variable, string id, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var target = Marshal.StringToCoTaskMemUni(Target(variable, id));
        var user = Marshal.StringToCoTaskMemUni(variable);
        var blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = user,
            };
            if (!CredWriteW(ref credential, 0)) throw Refused("CredWrite");
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length); // the key does not linger in freed memory
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
        }
    }

    private static readonly TimeSpan CreateLockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// CredWrite has no add-only flag, so the read-then-write runs under a named mutex that every Dictum process in this
    /// Windows session shares; a process that writes with <see cref="Write"/> instead does not take it, so only TryCreate
    /// callers are serialised.
    /// <para>
    /// Namespace: Local\, the session's own. Read on Microsoft Learn on 08/11/2026
    /// (https://learn.microsoft.com/windows/win32/termserv/kernel-object-namespaces): each session has a separate namespace for
    /// mutexes, and client processes "can use the "Local\" prefix to explicitly create an object in their session namespace";
    /// CreateMutexW (https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-createmutexw) says the rest of the name
    /// "can contain any character except the backslash". Local\ rather than Global\ because the credential is per user and the
    /// race that matters is two processes of one user in one session; the same Learn page says "The global namespace is not
    /// available for Windows Store apps", and a machine-wide name could be created first, with access denied, by another account.
    /// <see cref="SingleInstance"/> names its mutex in Local\ as well.
    /// </para>
    /// <para>
    /// Gap left: one user signed in to two Windows sessions at once (Credential Manager is per user, so this is the only
    /// cross-session case) is not serialised, and two first uses in those sessions at the same moment could still each store a
    /// key. SingleInstance is Local\ too, so each such session can run its own app.
    /// </para>
    /// </summary>
    public bool TryCreate(string variable, string id, string value)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, $@"Local\Dictum.Credentials.{variable}.{id}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            throw new CredentialStoreException($"TryCreate: the credential lock could not be opened ({ex.GetType().Name}).");
        }
        using (mutex)
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(CreateLockTimeout);
            }
            catch (AbandonedMutexException)
            {
                // Its holder exited without releasing. Learn (https://learn.microsoft.com/dotnet/standard/threading/mutexes, read
                // 08/11/2026): the exception "is thrown in the next thread that acquires the mutex", so this thread owns it. The
                // Read below re-checks the store, so a key the holder did store is found and kept.
                acquired = true;
            }
            if (!acquired) throw new CredentialStoreException("TryCreate: the credential lock was not released");
            try
            {
                if (Read(variable, id) is not null) return false;
                Write(variable, id, value);
                return true;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }

    public string? Read(string variable, string id)
    {
        if (!CredReadW(Target(variable, id), CRED_TYPE_GENERIC, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == ERROR_NOT_FOUND) return null;
            throw Refused("CredRead");
        }
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var value = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return value;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public bool Delete(string variable, string id)
    {
        if (CredDeleteW(Target(variable, id), CRED_TYPE_GENERIC, 0)) return true;
        if (Marshal.GetLastWin32Error() == ERROR_NOT_FOUND) return false;
        throw Refused("CredDelete");
    }

    public IReadOnlyList<(string Variable, string Id)> List()
    {
        if (!CredEnumerateW(Prefix + "*", 0, out var count, out var array))
        {
            if (Marshal.GetLastWin32Error() == ERROR_NOT_FOUND) return [];
            throw Refused("CredEnumerate");
        }
        try
        {
            var found = new List<(string, string)>();
            for (var i = 0; i < count; i++)
            {
                var credential = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                if (credential.Type != CRED_TYPE_GENERIC || Marshal.PtrToStringUni(credential.TargetName) is not { } target) continue;
                if (Parse(target) is { } entry) found.Add(entry);
            }
            return found;
        }
        finally
        {
            CredFree(array);
        }
    }

    /// <summary>A target name this class writes, as its variable and id; null for anything else under the prefix (another program's, or hand-made).</summary>
    internal static (string Variable, string Id)? Parse(string target)
    {
        if (!target.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = target[Prefix.Length..].Split('/');
        if (parts.Length != 2) return null;
        var variable = SecretStore.KeyNames.FirstOrDefault(k => string.Equals(k, parts[0], StringComparison.OrdinalIgnoreCase));
        var id = parts[1].ToLowerInvariant();
        return variable is not null && StoredKeyId.IsValid(id) ? (variable, id) : null;
    }

    private static CredentialStoreException Refused(string call)
    {
        var code = Marshal.GetLastWin32Error();
        return new CredentialStoreException($"{call} failed: {new Win32Exception(code).Message} ({code}).", code);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredEnumerateW(string filter, uint flags, out int count, out nint credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
