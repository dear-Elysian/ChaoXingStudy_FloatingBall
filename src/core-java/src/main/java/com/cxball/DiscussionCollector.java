package com.cxball;

import org.json.JSONArray;
import org.json.JSONObject;

import com.cxball.Protocol.ApiException;

/**
 * 主题讨论采集（2026-10-27 实测校准）：
 * POST https://groupweb.chaoxing.com/course/topic/{bbsid}/getTopicList
 *   ?folder_uuid=&page=1&pageSize=20&kw=&courseId={courseId}
 *   &isSetTop=&selectedStartTime=&selectedEndTime=&selectedType=0
 *   &learnSilverStartTime=&learnSilverEndTime=&lastAuxValue=
 * body: tags=classId{classId}（缺少 body 或 X-Requested-With 头会返回「你没有权限操作」）
 *
 * 响应：status=true，datas[] 为主题列表。讨论页同时包含
 * topicType=0（自由讨论主题，系统建模与UML语言实测）与 topicType=10（任务主题讨论），
 * 两者都展示；20=章节讨论、25=任务引擎不属于本模块。
 * 详情页：https://groupweb.chaoxing.com/course/topic/v3/bbs/{bbsid}/{uuid}/replysList
 *   ?courseId&classId&cpi&knowledgeEnc=&ut=s
 *
 * 回复列表（2026-11-20 按 topicInvitation.js 实测）：
 *   GET /pc/invitation/getTopReplyList?bbsid&uuid&order=1&tag=&kw=   置顶回复
 *   GET /pc/invitation/getReplyList?bbsid&uuid&order=2&lastValue=... 普通回复，按 lastValue 游标翻页
 * 返回 datas[].creater_name；二级回复在 datas[].second_data[].creater_name。
 *
 * bbsid 由 CourseCollector.enrichSecrets 从课程页隐藏域取得并随课程缓存。
 * 只抓列表元信息与回复人昵称（只读）；不做发帖、回复等写操作。
 */
public class DiscussionCollector implements Collector {

    private static final String BASE = "https://groupweb.chaoxing.com";
    private static final int TYPE_FREE_TOPIC = 0;
    private static final int TYPE_TOPIC_DISCUSS = 10;

    private static final String REPLY_LIST_API = BASE + "/pc/invitation/getReplyList";
    private static final String TOP_REPLY_API = BASE + "/pc/invitation/getTopReplyList";
    /** 服务端每页固定 20 条（pageSize/size 等参数实测无效），用 poff.lastValue 翻页。 */
    private static final int MAX_REPLY_PAGES = 100;   // 安全上限，防异常游标死循环

    /** 当前账号昵称，由 Main 在 fetch.start 时注入；只用于精确比对回复人。 */
    private volatile String userName = "";

    public void setUserName(String name) {
        this.userName = name == null ? "" : name.trim();
    }

    @Override public String type() { return "discussion"; }

    @Override public String label() { return "主题讨论"; }

    @Override public boolean perCourse() { return true; }

    @Override
    public JSONArray fetch(ChaoxingClient client, JSONObject course) throws ApiException {
        String courseId = course.getString("courseId");
        String classId = course.getString("classId");
        String name = course.optString("courseName");
        String cpi = course.optString("cpi", "");
        String bbsid = course.optString("bbsid", "");
        if (bbsid.isEmpty()) return new JSONArray();   // 课程未开通讨论：视为无主题，不记为失败

        String url = BASE + "/course/topic/" + bbsid + "/getTopicList"
                + "?folder_uuid=&page=1&pageSize=20&kw=&courseId=" + courseId
                + "&isSetTop=&selectedStartTime=&selectedEndTime=&selectedType=0"
                + "&learnSilverStartTime=&learnSilverEndTime=&lastAuxValue=";
        // 关键（2026-10-28 实测）：只带登录 Cookie 会被判「你没有权限操作」。
        // 必须先打开一次带课程上下文的讨论页，groupweb 会下发自己的会话 Cookie
        // （route、{courseId}enc/{courseId}t/{courseId}cpi/{courseId}ut），此后再调接口才返回数据。
        String bbsEnc = course.optString("bbsEnc", "");
        String bbsT = course.optString("bbsT", "");
        if (!bbsEnc.isEmpty() && !bbsT.isEmpty()) {
            String warm = BASE + "/course/topic/topicList?courseid=" + courseId
                    + "&clazzid=" + classId + (cpi.isEmpty() ? "" : "&cpi=" + cpi)
                    + "&ut=s&t=" + bbsT + "&stuenc=" + bbsEnc + "&bbsid=" + bbsid
                    + "&learnSilverStartTime=&learnSilverEndTime="
                    + "&showAIEnc=" + course.optString("showAIEnc", "0")
                    + "&supportAI=true&enc=" + bbsEnc + "&pageHeader=2";
            client.get(warm, "https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/stu?courseid="
                    + courseId + "&clazzid=" + classId + (cpi.isEmpty() ? "" : "&cpi=" + cpi));
        }
        String referer = BASE + "/course/topic/topicList?courseid=" + courseId
                + "&clazzid=" + classId + (cpi.isEmpty() ? "" : "&cpi=" + cpi);
        String body = client.postAjax(url, "tags=classId" + classId, referer);

        JSONObject root;
        try {
            root = new JSONObject(body);
        } catch (Exception e) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]主题讨论响应不是 JSON（疑似改版），需校准解析器", false);
        }
        if (!root.optBoolean("status", false)) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]主题讨论接口返回失败: " + root.optString("msg", "未知原因"), false);
        }
        JSONArray datas = root.optJSONArray("datas");
        if (datas == null) datas = new JSONArray();

        JSONArray out = new JSONArray();
        for (int i = 0; i < datas.length(); i++) {
            JSONObject t = datas.optJSONObject(i);
            if (t == null) continue;
            // 讨论页里 topicType=0（自由主题）与 10（任务主题讨论）都展示；
            // 明确为章节讨论/任务引擎的跳过。
            int topicType = t.optInt("topicType", TYPE_FREE_TOPIC);
            if (topicType != TYPE_FREE_TOPIC && topicType != TYPE_TOPIC_DISCUSS) continue;

            String title = t.optString("title", "").trim();
            if (title.isEmpty()) {
                // 部分自由讨论没有标题，讨论页用正文首段展示；取正文压缩空白后的前 60 字。
                String content = t.optString("content", "").replaceAll("\\s+", " ").trim();
                if (content.length() > 60) content = content.substring(0, 60) + "…";
                title = content;
            }
            String uuid = t.optString("uuid", "");
            String topicBbsid = t.optString("bbsid", bbsid);
            if (title.isEmpty() || uuid.isEmpty() || topicBbsid.isEmpty()) continue;

            int replyCount = t.optInt("reply_count", 0);
            long createTime = t.optLong("create_time", 0L);
            String link = BASE + "/course/topic/v3/bbs/" + topicBbsid + "/" + uuid + "/replysList"
                    + "?courseId=" + courseId + "&classId=" + classId
                    + (cpi.isEmpty() ? "" : "&cpi=" + cpi) + "&knowledgeEnc=&ut=s";

            JSONObject item = Models.item("discussion", courseId, name, title,
                    replyCount + " 回复", null, createTime > 0 ? createTime : null, link, null);
            item.put("uuid", uuid);
            item.put("topicId", t.optLong("id", 0L));
            item.put("replyCount", replyCount);
            item.put("createrName", t.optString("createrName", ""));
            // 结束判定（2026-10-28 按站点 topicDetail.js#getEndTime 校准，附录 A.3.3）：
            // quoteInfo.deadline 为 0/缺失 = 未设截止（未结束）；= 1 = 教师手动结束（已结束，页面显示“手动结束”）；
            // > 1 为截止时间戳（毫秒；秒级自动换算），到点即已结束。lock_reply=1（教师主动结束）同样视为已结束。
            JSONObject quote = t.optJSONObject("quoteInfo");
            Object deadlineRaw = quote == null ? null : quote.opt("deadline");
            long deadline = asLong(deadlineRaw);
            long endAt = 0L;
            boolean ended = false;
            if (deadline == 1L || t.optInt("lock_reply", 0) == 1) {
                ended = true;
            } else if (deadline > 1_000_000_000L) {
                endAt = deadline > 1_000_000_000_000L ? deadline : deadline * 1000L;
                item.put("endAt", endAt);
                ended = endAt <= System.currentTimeMillis();
            }
            item.put("ended", ended);

            // 剩余时间与回复状态共用一个展示位（前端 §8.2.8）：
            // 仍有剩余时间时不查回复；已结束/无截止时才需要「已回复/未回复」。
            // 0 回复直接判定未回复，不再发请求；有回复才扫描，命中当前昵称立即停止。
            boolean hasRemaining = endAt > 0 && !ended;
            boolean replied = false;
            if (!hasRemaining && replyCount > 0) {
                try {
                    replied = hasReplied(client, topicBbsid, uuid, link, replyCount);
                } catch (ApiException e) {
                    // 会话失效/限流仍按全局失败上抛；其余回复查询失败不影响主题本身展示。
                    if (Protocol.SESSION_EXPIRED.equals(e.code) || Protocol.RATE_LIMITED.equals(e.code)) throw e;
                }
            }
            item.put("replied", replied);
            out.put(item);
        }
        return out;
    }

    /**
     * 精确比对回复人昵称（区分大小写、去首尾空白）：找到与当前账号昵称完全相同的人即返回 true 并停止翻页。
     * 先按「新→旧」扫普通回复；置顶回复单独接口，只有在已扫条数少于 reply_count 时才补扫一次。
     */
    private boolean hasReplied(ChaoxingClient client, String bbsid, String uuid, String referer,
                               int replyCount) throws ApiException {
        String me = userName;
        if (me.isEmpty()) return false;

        String lastValue = "";
        int scanned = 0;
        for (int page = 0; page < MAX_REPLY_PAGES; page++) {
            String url = REPLY_LIST_API + "?bbsid=" + bbsid + "&uuid=" + uuid
                    + "&order=2&lastValue=" + lastValue + "&lastAuxValue=&tag=&kw=";
            JSONObject root = getJson(client, url, referer);
            if (root == null || !root.optBoolean("status", false)) break;
            JSONArray datas = root.optJSONArray("datas");
            if (datas == null || datas.isEmpty()) break;
            if (containsUser(datas, me)) return true;
            scanned += datas.length();
            JSONObject poff = root.optJSONObject("poff");
            String next = poff == null ? "" : String.valueOf(poff.opt("lastValue"));
            if (next.isEmpty() || "null".equals(next) || next.equals(lastValue)) break;
            lastValue = next;
        }

        // 普通回复列表不含置顶回复；reply_count 仍有剩余时才补扫置顶，避免每帖多一次请求。
        if (scanned < replyCount) {
            String url = TOP_REPLY_API + "?bbsid=" + bbsid + "&uuid=" + uuid
                    + "&order=1&tag=&kw=";
            JSONObject root = getJson(client, url, referer);
            JSONArray datas = root == null ? null : root.optJSONArray("datas");
            return datas != null && containsUser(datas, me);
        }
        return false;
    }

    private static boolean containsUser(JSONArray datas, String me) {
        for (int i = 0; i < datas.length(); i++) {
            JSONObject r = datas.optJSONObject(i);
            if (r == null) continue;
            if (me.equals(r.optString("creater_name", "").trim())) return true;
            JSONArray seconds = r.optJSONArray("second_data");
            if (seconds == null) continue;
            for (int j = 0; j < seconds.length(); j++) {
                JSONObject s = seconds.optJSONObject(j);
                if (s != null && me.equals(s.optString("creater_name", "").trim())) return true;
            }
        }
        return false;
    }

    private static JSONObject getJson(ChaoxingClient client, String url, String referer)
            throws ApiException {
        try {
            return new JSONObject(client.get(url, referer));
        } catch (ApiException e) {
            throw e;   // 交给调用方区分会话失效/网络错误
        } catch (Exception e) {
            return null;
        }
    }

    private static long asLong(Object v) {
        if (v instanceof Number num) return num.longValue();
        if (v instanceof String s && !s.isBlank()) {
            try { return Long.parseLong(s.trim()); } catch (NumberFormatException ignored) { /* 非数字 */ }
        }
        return 0L;
    }
}
