using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// Security.framework: the keychain item calls (SecItem.h) and the code-signing self check (SecCode.h). Every declaration
/// and constant below was read 08/11/2026 in the macOS 27.2 SDK of Xcode 27.2 (build 27B52), under
/// <c>$(xcrun --show-sdk-path)/System/Library/Frameworks/Security.framework/Headers</c>; the line numbers cited are that
/// copy's. OSStatus is SInt32. The kSec* keys are exported CFStringRef globals, read through their storage as
/// <see cref="CGWindowList"/> reads its keys; they are never released.
/// </summary>
internal static class Sec
{
    private const string Lib = "/System/Library/Frameworks/Security.framework/Security";

    // SecBase.h:326, 352, 353.
    public const int Success = 0;
    public const int DuplicateItem = -25299;
    public const int ItemNotFound = -25300;

    // CSCommon.h:253 kSecCSDefaultFlags = 0; SecCode.h:470 kSecCSSigningInformation = 1 << 1, the flag that adds
    // the "Signing" keys (kSecCodeInfoTeamIdentifier among them, SecCode.h:512) to SecCodeCopySigningInformation's dictionary.
    public const uint CSDefaultFlags = 0;
    public const uint CSSigningInformation = 1 << 1;

    /// <summary>SecItem.h:1227. On macOS the added item would be returned through <paramref name="result"/>; Dictum passes 0.</summary>
    [DllImport(Lib, EntryPoint = "SecItemAdd")] public static extern int ItemAdd(nint attributes, nint result);

    /// <summary>SecItem.h:1178. <paramref name="result"/> is CF_RETURNS_RETAINED: the caller releases it.</summary>
    [DllImport(Lib, EntryPoint = "SecItemCopyMatching")] public static extern int ItemCopyMatching(nint query, out nint result);

    /// <summary>SecItem.h:1246.</summary>
    [DllImport(Lib, EntryPoint = "SecItemUpdate")] public static extern int ItemUpdate(nint query, nint attributesToUpdate);

    /// <summary>SecItem.h:1277. Deletes every item the query matches; Dictum's queries name one service and one account.</summary>
    [DllImport(Lib, EntryPoint = "SecItemDelete")] public static extern int ItemDelete(nint query);

    /// <summary>SecBase.h:251. A new CFString, or 0; the caller releases it.</summary>
    [DllImport(Lib, EntryPoint = "SecCopyErrorMessageString")] public static extern nint CopyErrorMessageString(int status, nint reserved);

    /// <summary>SecCode.h:63. The running process's code object (CF_RETURNS_RETAINED: the caller releases it).</summary>
    [DllImport(Lib, EntryPoint = "SecCodeCopySelf")] public static extern int CodeCopySelf(uint flags, out nint self);

    /// <summary>SecCode.h:528. A CFDictionary (CF_RETURNS_RETAINED); an unsigned code still succeeds, without the identifier key (SecCode.h:341-343).</summary>
    [DllImport(Lib, EntryPoint = "SecCodeCopySigningInformation")] public static extern int CodeCopySigningInformation(nint code, uint flags, out nint information);

    /// <summary>The exported keys and values, read once on first use.</summary>
    internal sealed class Keys
    {
        public readonly nint Class, ClassGenericPassword, AttrSynchronizable, AttrLabel, AttrAccount, AttrService;
        public readonly nint MatchLimit, MatchLimitOne, MatchLimitAll, ReturnData, ReturnAttributes, ValueData;
        public readonly nint CodeInfoTeamIdentifier;

        public Keys()
        {
            var lib = NativeLibrary.Load(Lib);
            nint Key(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, name));
            Class = Key("kSecClass"); // SecItem.h:53
            ClassGenericPassword = Key("kSecClassGenericPassword"); // SecItem.h:70
            AttrSynchronizable = Key("kSecAttrSynchronizable"); // SecItem.h:456; false or absent: no synchronizable item added or returned (:220-224)
            AttrLabel = Key("kSecAttrLabel"); // SecItem.h:472
            AttrAccount = Key("kSecAttrAccount"); // SecItem.h:478
            AttrService = Key("kSecAttrService"); // SecItem.h:480
            MatchLimit = Key("kSecMatchLimit"); // SecItem.h:926
            MatchLimitOne = Key("kSecMatchLimitOne"); // SecItem.h:928
            MatchLimitAll = Key("kSecMatchLimitAll"); // SecItem.h:930
            ReturnData = Key("kSecReturnData"); // SecItem.h:964
            ReturnAttributes = Key("kSecReturnAttributes"); // SecItem.h:966
            ValueData = Key("kSecValueData"); // SecItem.h:991
            CodeInfoTeamIdentifier = Key("kSecCodeInfoTeamIdentifier"); // SecCode.h:512, a "Signing" key
        }
    }

    private static readonly Lazy<Keys> LoadedKeys = new(() => new Keys());

    /// <summary>The kSec* constants (a native read on first use, so only from code that already runs on macOS).</summary>
    public static Keys K => LoadedKeys.Value;

    /// <summary>The names of the statuses a keychain call can plausibly return (SecBase.h:326-392), for log lines. Managed only: no native call.</summary>
    private static readonly Dictionary<int, string> Names = new()
    {
        [0] = "errSecSuccess", // :326
        [-4] = "errSecUnimplemented", // :327
        [-50] = "errSecParam", // :332
        [-61] = "errSecWrPerm", // :333
        [-108] = "errSecAllocate", // :334
        [-128] = "errSecUserCanceled", // :335
        [-34018] = "errSecMissingEntitlement", // :341
        [-25291] = "errSecNotAvailable", // :344
        [-25292] = "errSecReadOnly", // :345
        [-25293] = "errSecAuthFailed", // :346
        [-25294] = "errSecNoSuchKeychain", // :347
        [-25295] = "errSecInvalidKeychain", // :348
        [-25299] = "errSecDuplicateItem", // :352
        [-25300] = "errSecItemNotFound", // :353
        [-25307] = "errSecNoDefaultKeychain", // :360
        [-25308] = "errSecInteractionNotAllowed", // :361
        [-25316] = "errSecDataNotAvailable", // :369
        [-25244] = "errSecInvalidOwnerEdit", // :379
        [-26275] = "errSecDecode", // :392
    };

    /// <summary>The status's errSec name, or "OSStatus {n}" for one not in the table. No native call.</summary>
    public static string StatusName(int status) => Names.TryGetValue(status, out var name) ? name : $"OSStatus {status}";

    /// <summary>The status's name and number, with the system's own message when it has one (never a value: the message is about the status alone).</summary>
    public static string Describe(int status)
    {
        var text = $"{StatusName(status)} ({status})";
        var message = CopyErrorMessageString(status, 0);
        if (message == 0) return text;
        try
        {
            return CF.ToString(message) is { Length: > 0 } words ? $"{text}: {words}" : text;
        }
        finally
        {
            CF.Release(message);
        }
    }
}
