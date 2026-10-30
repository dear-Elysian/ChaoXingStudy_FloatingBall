package com.cxball;

import org.json.JSONArray;
import org.json.JSONObject;

import com.cxball.Protocol.ApiException;

/**
 * 随堂练习采集（2026-10-27 实测校准）：
 * GET https://mobilelearn.chaoxing.com/v2/apis/active/student/activelist
 *   ?fid&courseId&classId&showNotStartedActive=0&_=<ts>
 * 响应 JSON：data.activeList[]，其中 type/activeType=42 为随堂练习。
 * 字段：id(活动 id)/nameOne(标题)/startTime/endTime(毫秒)/status/userStatus/nameFour(展示时间)。
 * 详情页：https://mobilelearn.chaoxing.com/page/quiz/stu/answerQuestion2?courseId&classId&activeId&fid
 *
 * 只抓列表元信息（只读）；不做答题、不提交。fid 由 CourseCollector.enrichSecrets 从课程页 cfid 取得。
 */
public class PracticeCollector implements Collector {

    private static final String LIST_URL = "https://mobilelearn.chaoxing.com/v2/apis/active/student/activelist";
    private static final String DETAIL_URL = "https://mobilelearn.chaoxing.com/page/quiz/stu/answerQuestion2";
    private static final String ANSWER_URL = "https://mobilelearn.chaoxing.com/v2/apis/studentQuestion/getAnswerResult";
    private static final int TYPE_PRACTICE = 42;
    /** 进程内缓存 activeId → 是否已提交（已结束的答案不再变化，跨刷新复用，避免重复请求） */
    private final java.util.Map<Long, Boolean> answeredCache = new java.util.concurrent.ConcurrentHashMap<>();

    @Override public String type() { return "practice"; }

    @Override public String label() { return "随堂练习"; }

    @Override public boolean perCourse() { return true; }

    @Override
    public JSONArray fetch(ChaoxingClient client, JSONObject course) throws ApiException {
        String courseId = course.getString("courseId");
        String classId = course.getString("classId");
        String name = course.optString("courseName");
        String fid = course.optString("fid", "");
        if (fid.isEmpty()) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]缺少 fid，需先 enrichSecrets", false);
        }

        String url = LIST_URL + "?fid=" + fid + "&courseId=" + courseId
                + "&classId=" + classId
                + "&showNotStartedActive=0&_=" + System.currentTimeMillis();
        String referer = "https://mobilelearn.chaoxing.com/page/active/stuActiveList?courseid=" + courseId
                + "&clazzid=" + classId + "&fid=" + fid;
        String body = client.get(url, referer);

        JSONObject root;
        try {
            root = new JSONObject(body);
        } catch (Exception e) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]随堂练习响应不是 JSON（疑似改版），需校准解析器", false);
        }
        JSONObject data = root.optJSONObject("data");
        JSONArray list = data == null ? null : data.optJSONArray("activeList");
        if (list == null) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]随堂练习 JSON 缺少 data.activeList（疑似改版）", false);
        }

        JSONArray out = new JSONArray();
        long now = System.currentTimeMillis();
        for (int i = 0; i < list.length(); i++) {
            JSONObject a = list.optJSONObject(i);
            if (a == null) continue;
            // 同一活动在响应里同时有 type/activeType，取任意可用值
            int type = a.optInt("activeType", a.optInt("type", -1));
            if (type != TYPE_PRACTICE) continue;

            String title = a.optString("nameOne", "").trim();
            if (title.isEmpty()) title = a.optString("nameTwo", "").trim();
            if (title.isEmpty()) continue;

            long start = a.optLong("startTime", 0L);
            long end = a.optLong("endTime", 0L);
            // 活动状态看 status（2026-10-28 实测校准）：1 = 进行中（nameFour 形如「剩余:1小时」），
            // 2 = 已结束（nameFour 为截止时间），0 = 未开始。
            // 注意 userStatus 不是完成标记（进行中的条目同样为 1），不能用来判「已完成」。
            int state = a.optInt("status", -1);
            String status;
            if (state == 1) {
                status = "进行中";
            } else if (state == 2) {
                status = "已结束";
            } else if (state == 0) {
                status = "未开始";
            } else if (start > now) {
                status = "未开始";
            } else if (end <= 0 || end > now) {
                status = "进行中";
            } else {
                status = "已结束";
            }

            long activeId = a.optLong("id", 0L);
            String link = DETAIL_URL + "?courseId=" + courseId + "&classId=" + classId
                    + (activeId > 0 ? "&activeId=" + activeId : "") + "&fid=" + fid;

            JSONObject item = Models.item("practice", courseId, name, title, status,
                    end > 0 ? end : null,
                    start > 0 ? start : null,
                    link, null);
            // 完成状态：列表接口没有完成标记（userStatus 恒为 1），只有 getAnswerResult 的 data.isAnswered 可靠。
            // 对所有条目都查（进行中的也可能已提交，见 §8.2.1.1），已提交 → 已完成。
            if (activeId > 0 && isAnswered(client, courseId, classId, activeId, referer)) {
                item.put("status", "已完成");
            }
            out.put(item);
        }
        return out;
    }

    /** 单条问答结果（只读）：data.isAnswered 为该学生是否已提交，用于区分已完成/已过期 */
    private boolean isAnswered(ChaoxingClient client, String courseId, String classId, long activeId, String referer) {
        Boolean cached = answeredCache.get(activeId);
        if (cached != null) return cached;
        boolean answered = false;
        try {
            String url = ANSWER_URL + "?activeId=" + activeId + "&courseId=" + courseId
                    + "&classId=" + classId + "&DB_STRATEGY=PRIMARY_KEY&STRATEGY_PARA=activeId";
            JSONObject root = new JSONObject(client.get(url, referer));
            JSONObject data = root.optJSONObject("data");
            answered = data != null && data.optBoolean("isAnswered", false);
            // 只缓存 true（已提交是单向的、不会回退）；false 下次刷新重查，避免漏掉刚提交的
            if (answered) answeredCache.put(activeId, true);
        } catch (Exception ignored) {
            // 单条查询失败不阻断整门课：保守按「未完成」处理（显示为已过期）
        }
        return answered;
    }
}
