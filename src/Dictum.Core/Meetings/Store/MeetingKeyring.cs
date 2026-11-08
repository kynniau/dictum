using System.Security.Cryptography;
using Dictum.Platform;

namespace Dictum.Meetings.Store;

public interface IMeetingKeyring
{
    byte[] OwnerKey();
}

public sealed class MeetingsLockedException : Exception
{
    public const string LockedMessagePlaceholder = "PLACEHOLDER: Meetings is locked (owner to approve wording)";
    public MeetingsLockedException() : base(LockedMessagePlaceholder) { }
}

public sealed class MeetingKeyring(ICredentialStore credentials) : IMeetingKeyring
{
    public const string Variable = "DICTUM_MEETINGS_KEY";
    // All keyrings in this process serialize first use, including those sharing a credential store. The Gate is per process
    // only: two Dictum processes of one user (the app in two Windows sessions, or any second process that opens Meetings) can both read no key
    // and both mint one. The key is therefore stored with TryCreate (add-only), never Write (create-or-replace): the process
    // that loses reads back and uses the winner's key, because a replaced owner key leaves every meeting sealed with the
    // first one unreadable for ever (decision D2: export is the only recovery).
    private static readonly object Gate = new();
    public byte[] OwnerKey()
    {
        lock (Gate)
        {
            try
            {
                if (!credentials.Available) throw new MeetingsLockedException();
                if (credentials.Read(Variable, "owner") is { } encoded) return Existing(encoded);
                var key = RandomNumberGenerator.GetBytes(32);
                bool created;
                try { created = credentials.TryCreate(Variable, "owner", Convert.ToBase64String(key)); }
                catch { CryptographicOperations.ZeroMemory(key); throw; }
                if (created) return key;
                CryptographicOperations.ZeroMemory(key); // another process stored its key first: use that one
                return credentials.Read(Variable, "owner") is { } winner ? Existing(winner) : throw new MeetingsLockedException();
            }
            catch (Exception e) when (e is not MeetingsLockedException)
            {
                // Credential errors must not echo credential values into a caller's logs.
                throw new MeetingsLockedException();
            }
        }
    }

    private static byte[] Existing(string encoded)
    {
        var existing = Convert.FromBase64String(encoded);
        if (existing.Length != 32) throw new MeetingsLockedException();
        return existing;
    }
}
