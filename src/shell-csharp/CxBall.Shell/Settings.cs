using System.IO;
using System.Text.Json.Nodes;

namespace CxBall.Shell;

/// <summary>全局设置（data/config/settings.json）。P0 用 JsonObject 宽松读写，字段见方案 §7。</summary>
static class Settings
{
    static JsonObject? _cache;
    static readonly object _lock = new();

    static JsonObject Defaults() => new()
    {
        ["currentAccount"] = null,
        ["loginAutoRefresh"] = true,
        ["loginRefreshIntervalSec"] = 15,
        ["manualThrottleSec"] = 15,
        ["modules"] = new JsonObject { ["assignment"] = true, ["notice"] = true, ["exam"] = false, ["practice"] = false, ["discussion"] = false },   // 首次登录只开作业/消息，其余三项按需勾选（§7.3）
        ["timeDisplay"] = "remainingHours",          // remainingHours | deadline | both | none（§7.5，两个独立勾选）
        ["background"] = new JsonObject { ["image"] = null, ["opacity"] = 75 },
        ["moduleOpacity"] = 100,                     // 模块卡片背景透明度 %（§7.5，统一调节，默认全不透明）
        ["autostart"] = false,                       // 开机自启动（§7.5，默认不勾选）
        ["browserPath"] = "",                        // 详情用外部浏览器路径（§8.2.1，空=未配置，仅在设置页点「自动」时探测）
        ["aiEnabled"] = false,                       // 启用AI模型（§7.9，默认不勾选）
        ["aiEndpoint"] = "",                         // OpenAI 兼容接口地址（已归一化，不含 API Key）
        ["aiModel"] = "",                            // 最近一次检测到的模型名；空 = 未知模型
        ["aiThinking"] = false,                      // 启用思考链（§7.9，默认不勾选；仅 DeepSeek 系接口显式开启）
        ["noticeDays"] = 30,                         // 消息展示时长：7|14|30|90|180|360 天（§7.4.1，默认 30）
        ["ballImage"] = null,                        // 悬浮球自定义图案文件名（data/assets 内），null = 默认
        ["cacheMode"] = "time",                      // time | size（§7.6，互斥）
        ["cacheDays"] = 7,                           // 按时间清理周期：1|3|7|15|30
        ["cacheLimitMB"] = 200,                      // 按大小清理阈值，最小 100
        ["lastCacheCleanAt"] = 0L,                   // 上次缓存清理时间戳（ms）
    };

    public static JsonObject Get()
    {
        lock (_lock)
        {
            if (_cache != null) return (JsonObject)_cache.DeepClone();
            try
            {
                if (File.Exists(Paths.ConfigFile))
                {
                    var node = JsonNode.Parse(File.ReadAllText(Paths.ConfigFile)) as JsonObject;
                    if (node != null) { _cache = Merge(Defaults(), node); return (JsonObject)_cache.DeepClone(); }
                }
            }
            catch { /* 损坏则回退默认 */ }
            _cache = Defaults();
            return (JsonObject)_cache.DeepClone();
        }
    }

    /// <summary>浅层补丁合并（顶层键覆盖/嵌套对象递归）。</summary>
    public static void Patch(JsonObject patch)
    {
        lock (_lock)
        {
            var cur = Get();
            _cache = Merge(cur, patch);
            Save();
        }
    }

    public static void Set(string key, JsonNode? value)
    {
        lock (_lock)
        {
            var cur = Get();
            cur[key] = value?.DeepClone();
            _cache = cur;
            Save();
        }
    }

    static void Save()
    {
        if (_cache == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.ConfigFile)!);
        File.WriteAllText(Paths.ConfigFile, _cache.ToJsonString());
    }

    static JsonObject Merge(JsonObject baseObj, JsonObject patch)
    {
        var result = (JsonObject)baseObj.DeepClone();
        foreach (var kv in patch)
        {
            if (kv.Value is JsonObject po && result[kv.Key] is JsonObject ro)
                result[kv.Key] = Merge(ro, po);
            else
                result[kv.Key] = kv.Value?.DeepClone();
        }
        return result;
    }
}
