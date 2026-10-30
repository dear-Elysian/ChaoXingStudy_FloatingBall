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
 * 响应：status=true，datas[] 为主题列表；只取 topicType=10（主题讨论），
 * 20=章节讨论、25=任务引擎不属于本模块。
 * 详情页：https://groupweb.chaoxing.com/course/topic/v3/bbs/{bbsid}/{uuid}/replysList
 *   ?courseId&classId&cpi&knowledgeEnc=&ut=s
 *
 * bbsid 由 CourseCollector.enrichSecrets 从课程页隐藏域取得并随课程缓存。
 * 只抓列表元信息（只读）；不做发帖、回复等写操作。
 */
public class DiscussionCollector implements Collector {

    private static final String BASE = "https://groupweb.chaoxing.com";
    private static final int TYPE_TOPIC_DISCUSS = 10;

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
            // topicType 缺失时按主题讨论处理（老数据）；明确为章节讨论/任务引擎的跳过
            if (t.optInt("topicType", TYPE_TOPIC_DISCUSS) != TYPE_TOPIC_DISCUSS) continue;

            String title = t.optString("title", "").trim();
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
            boolean ended = false;
            if (deadline == 1L || t.optInt("lock_reply", 0) == 1) {
                ended = true;
            } else if (deadline > 1_000_000_000L) {
                long endAt = deadline > 1_000_000_000_000L ? deadline : deadline * 1000L;
                item.put("endAt", endAt);
                ended = endAt <= System.currentTimeMillis();
            }
            item.put("ended", ended);
            out.put(item);
        }
        return out;
    }

    private static long asLong(Object v) {
        if (v instanceof Number num) return num.longValue();
        if (v instanceof String s && !s.isBlank()) {
            try { return Long.parseLong(s.trim()); } catch (NumberFormatException ignored) { /* 非数字 */ }
        }
        return 0L;
    }
}
