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
    // All keyrings in this process serialize first use, including those sharing a credential store.
    private static readonly object Gate = new();
    public byte[] OwnerKey()
    {
        lock (Gate)
        {
            try
            {
                if (!credentials.Available) throw new MeetingsLockedException();
                if (credentials.Read(Variable, "owner") is { } encoded)
                {
                    var existing = Convert.FromBase64String(encoded);
                    if (existing.Length != 32) throw new MeetingsLockedException();
                    return existing;
                }
                var key = RandomNumberGenerator.GetBytes(32);
                try { credentials.Write(Variable, "owner", Convert.ToBase64String(key)); }
                catch { CryptographicOperations.ZeroMemory(key); throw; }
                return key;
            }
            catch (Exception e) when (e is not MeetingsLockedException)
            {
                // Credential errors must not echo credential values into a caller's logs.
                throw new MeetingsLockedException();
            }
        }
    }
}
