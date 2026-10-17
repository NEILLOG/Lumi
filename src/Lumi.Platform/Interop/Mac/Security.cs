using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumi.Platform.Interop.Mac;

[SupportedOSPlatform("macos")]
internal static partial class Security
{
    private const string Lib = "/System/Library/Frameworks/Security.framework/Security";

    // OSStatus
    public const int ErrSecSuccess = 0;
    public const int ErrSecUserCanceled = -128;
    public const int ErrSecItemNotFound = -25300;
    public const int ErrSecAuthFailed = -25293;
    public const int ErrSecInteractionNotAllowed = -25308;

    [LibraryImport(Lib)]
    internal static partial int SecItemAdd(SafeCFHandle attributes, IntPtr result);

    [LibraryImport(Lib)]
    internal static partial int SecItemCopyMatching(SafeCFHandle query, out IntPtr result);

    [LibraryImport(Lib)]
    internal static partial int SecItemUpdate(SafeCFHandle query, SafeCFHandle attributesToUpdate);

    [LibraryImport(Lib)]
    internal static partial int SecItemDelete(SafeCFHandle query);

    private static readonly Lazy<IntPtr> Handle = new(() => NativeLibrary.Load(Lib));

    private static IntPtr Constant(string symbol) => CoreFoundation.ReadConstant(Handle.Value, symbol);

    private static readonly Lazy<IntPtr> SecClass = new(() => Constant("kSecClass"));
    private static readonly Lazy<IntPtr> SecClassGenericPassword = new(() => Constant("kSecClassGenericPassword"));
    private static readonly Lazy<IntPtr> SecAttrService = new(() => Constant("kSecAttrService"));
    private static readonly Lazy<IntPtr> SecAttrAccount = new(() => Constant("kSecAttrAccount"));
    private static readonly Lazy<IntPtr> SecAttrLabel = new(() => Constant("kSecAttrLabel"));
    private static readonly Lazy<IntPtr> SecValueData = new(() => Constant("kSecValueData"));
    private static readonly Lazy<IntPtr> SecReturnData = new(() => Constant("kSecReturnData"));
    private static readonly Lazy<IntPtr> SecReturnAttributes = new(() => Constant("kSecReturnAttributes"));
    private static readonly Lazy<IntPtr> SecMatchLimit = new(() => Constant("kSecMatchLimit"));
    private static readonly Lazy<IntPtr> SecMatchLimitOne = new(() => Constant("kSecMatchLimitOne"));

    internal static IntPtr KSecClass => SecClass.Value;
    internal static IntPtr KSecClassGenericPassword => SecClassGenericPassword.Value;
    internal static IntPtr KSecAttrService => SecAttrService.Value;
    internal static IntPtr KSecAttrAccount => SecAttrAccount.Value;
    internal static IntPtr KSecAttrLabel => SecAttrLabel.Value;
    internal static IntPtr KSecValueData => SecValueData.Value;
    internal static IntPtr KSecReturnData => SecReturnData.Value;
    internal static IntPtr KSecReturnAttributes => SecReturnAttributes.Value;
    internal static IntPtr KSecMatchLimit => SecMatchLimit.Value;
    internal static IntPtr KSecMatchLimitOne => SecMatchLimitOne.Value;
}
