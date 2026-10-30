using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CxBall.Shell;

/// <summary>
/// BYOK 密钥存储（§7.9）：API Key 独立存 data/config/ai-key.bin，DPAPI 当前用户作用域加密。
/// 不写入 settings.json，因此 settings.get / app.init 回给前端的设置永远不含明文或密文；
/// Key 只在 C# 进程内解密后直接用于请求接口，不落日志、不传 Java。
/// </summary>
static class AiKeyStore
{
    static string FilePath => Path.Combine(Path.GetDirectoryName(Paths.ConfigFile)!, "ai-key.bin");

    public static string? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(plain).Trim();
            return key.Length > 0 ? key : null;
        }
        catch { return null; }
    }

    public static bool HasKey() => TryLoad() != null;

    public static void Save(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, enc);
    }

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
