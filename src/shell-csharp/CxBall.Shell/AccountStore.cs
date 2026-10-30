using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CxBall.Shell;

/// <summary>
/// 账号数据存取（data/accounts/&lt;uid-hash&gt;/）。
/// session.bin = DPAPI 加密的 Cookie JSON；meta.json = 昵称/头像等；state.json = 冷却时间戳/忽略列表/课程勾选；snapshot.json = 数据快照。
/// 密码永不落盘；Cookie 仅此处加密落盘，传 Java 只走内存。
/// </summary>
static class AccountStore
{
    public static string HashUid(string uid)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("cxball:" + uid));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    static string DirOf(string uidHash) => Path.Combine(Paths.AccountsDir, uidHash);

    // ---------- session.bin（DPAPI） ----------

    public static void SaveSession(string uidHash, string cookiesJson)
    {
        Directory.CreateDirectory(DirOf(uidHash));
        var plain = Encoding.UTF8.GetBytes(cookiesJson);
        var enc = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(DirOf(uidHash), "session.bin"), enc);
    }

    public static string? TryLoadSession(string uidHash)
    {
        var f = Path.Combine(DirOf(uidHash), "session.bin");
        if (!File.Exists(f)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(f), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    public static void DeleteSession(string uidHash)
    {
        var f = Path.Combine(DirOf(uidHash), "session.bin");
        if (File.Exists(f)) File.Delete(f);
    }

    // ---------- meta.json ----------

    public static void SaveMeta(string uidHash, JsonObject meta)
    {
        Directory.CreateDirectory(DirOf(uidHash));
        File.WriteAllText(Path.Combine(DirOf(uidHash), "meta.json"), meta.ToJsonString());
    }

    public static JsonObject? LoadMeta(string uidHash) => LoadJson(Path.Combine(DirOf(uidHash), "meta.json"));

    // ---------- state.json（lastLoginRefreshAt / lastManualRefreshAt / ignored[] / deselectedCourses[]） ----------

    public static JsonObject LoadState(string uidHash)
        => LoadJson(Path.Combine(DirOf(uidHash), "state.json")) ?? new JsonObject();

    public static void SaveState(string uidHash, JsonObject state)
    {
        Directory.CreateDirectory(DirOf(uidHash));
        File.WriteAllText(Path.Combine(DirOf(uidHash), "state.json"), state.ToJsonString());
    }

    // ---------- snapshot.json ----------

    public static void SaveSnapshot(string uidHash, JsonObject snapshot)
    {
        Directory.CreateDirectory(DirOf(uidHash));
        File.WriteAllText(Path.Combine(DirOf(uidHash), "snapshot.json"), snapshot.ToJsonString());
    }

    public static JsonObject? LoadSnapshot(string uidHash) => LoadJson(Path.Combine(DirOf(uidHash), "snapshot.json"));

    static JsonObject? LoadJson(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch { }
        return null;
    }
}
