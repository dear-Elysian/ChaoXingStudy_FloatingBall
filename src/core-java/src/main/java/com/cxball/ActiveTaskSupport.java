package com.cxball;

import org.json.JSONArray;
import org.json.JSONObject;

import com.cxball.Protocol.ApiException;

/**
 * 课程「任务」页共用能力（2026-11-18 实测）：
 * 分组任务与个人任务都来自 activeList 的 activeType=35；
 * 个人任务判定：isSpecialPersonalTask=1 或 isnorm=0（课程任务页模板同款条件）。
 *
 * 个人任务的提交时间不在列表里，需逐条查 getMyGroupInfo 的 taskGroupInfo.submitTime。
 */
final class ActiveTaskSupport {

    static final String LIST_URL = "https://mobilelearn.chaoxing.com/v2/apis/active/student/activelist";
    static final int TYPE_TASK = 35;

    private ActiveTaskSupport() { }

    /** 读取课程任务列表（activeList）；缺 fid 时直接报错，避免静默空列表。 */
    static JSONArray fetchActiveList(ChaoxingClient client, JSONObject course) throws ApiException {
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
        String body = client.get(url, listReferer(courseId, classId, fid));

        JSONObject root;
        try {
            root = new JSONObject(body);
        } catch (Exception e) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]任务列表响应不是 JSON（疑似改版），需校准解析器", false);
        }
        JSONObject data = root.optJSONObject("data");
        JSONArray list = data == null ? null : data.optJSONArray("activeList");
        if (list == null) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]任务 JSON 缺少 data.activeList（疑似改版）", false);
        }
        return list;
    }

    static String listReferer(String courseId, String classId, String fid) {
        return "https://mobilelearn.chaoxing.com/page/active/stuActiveList?courseid=" + courseId
                + "&clazzid=" + classId + "&fid=" + fid;
    }

    /** 个人任务：activeType=35 且 isSpecialPersonalTask=1 或 isnorm=0。 */
    static boolean isPersonalTask(JSONObject a) {
        if (a.optInt("activeType", a.optInt("type", -1)) != TYPE_TASK) return false;
        return a.optInt("isSpecialPersonalTask", 0) == 1 || a.optInt("isnorm", 1) == 0;
    }

    /**
     * 任务详情页（分组任务 / 个人任务同源）：
     * 先查 getActiveInfoForMe，按 isNorm / toAnswerPage / useNoteEditor 组合选择页面；
     * 查询失败时回落到「查看详情」新版页，保证右键「详情」始终有链接。
     */
    static String detailLink(ChaoxingClient client, String courseId, String classId, String fid,
                             long activeId, boolean isNormDefault, String referer) throws ApiException {
        boolean isNorm = isNormDefault;
        boolean toAnswer = false;
        boolean noteEditor = true;
        try {
            String url = "https://mobilelearn.chaoxing.com/v2/apis/taskStu/getActiveInfoForMe"
                    + "?fid=" + fid + "&courseId=" + courseId + "&activeId=" + activeId;
            JSONObject root = new JSONObject(client.get(url, referer));
            JSONObject data = root.optJSONObject("data");
            if (root.optInt("result", 0) == 1 && data != null) {
                isNorm = data.optInt("isNorm", isNorm ? 1 : 0) == 1;
                toAnswer = data.optInt("toAnswerPage", 0) == 1;
                noteEditor = data.optInt("useNoteEditor", 0) == 1;
            }
        } catch (ApiException e) {
            rethrowRefreshFailure(e);
        } catch (Exception ignored) {
            // 单条详情参数失败不影响列表，使用回落页
        }
        String path;
        if (isNorm) {
            path = noteEditor
                    ? (toAnswer ? "/page/renwuNew/stu/taskDetailStuForAnswer_new"
                                : "/page/renwuNew/stu/taskDetailStuAndAnsDetail_new")
                    : (toAnswer ? "/page/renwu/stu/taskDetailStuForAnswer"
                                : "/page/renwu/stu/taskDetailStuAndAnsDetail");
        } else {
            path = noteEditor
                    ? (toAnswer ? "/page/renwuNew/notGroup/taskDetailStuForAnswerNotGroup_new"
                                : "/page/renwuNew/notGroup/taskDetailStuAndAnsDetailNotGroup_new")
                    : (toAnswer ? "/page/renwu/notGroup/taskDetailStuForAnswerNotGroup"
                                : "/page/renwu/notGroup/taskDetailStuAndAnsDetailNotGroup");
        }
        return "https://mobilelearn.chaoxing.com" + path
                + "?courseId=" + courseId + "&classId=" + classId
                + "&activeId=" + activeId + "&fid=" + fid;
    }

    /**
     * 个人任务提交时间；空串 = 确认未提交，null = 查询失败/无法判断。
     * 完成判定只看该字段是否有具体时间，与截止时间无关。
     */
    static String submitTime(ChaoxingClient client, long activeId, String uid, String referer) throws ApiException {
        if (activeId <= 0 || uid == null || uid.isBlank()) return null;
        try {
            String url = "https://mobilelearn.chaoxing.com/v2/apis/taskStu/getMyGroupInfo"
                    + "?activeId=" + activeId + "&puid=" + uid + "&fromDetail=1";
            JSONObject root = new JSONObject(client.get(url, referer));
            if (root.optInt("result", 0) != 1) return null;
            JSONObject data = root.optJSONObject("data");
            JSONObject info = data == null ? null : data.optJSONObject("taskGroupInfo");
            if (info == null || !info.has("submitTime")) return null;
            Object value = info.opt("submitTime");
            if (value == JSONObject.NULL) return "";
            // 空字符串/显式 null 表示未提交；异常字段类型不能被视为提交时间。
            return value instanceof String ? ((String) value).trim() : null;
        } catch (ApiException e) {
            rethrowRefreshFailure(e);
            return null;
        } catch (Exception ignored) {
            return null;
        }
    }

    /** 会话失效与风控必须交给刷新入口统一中止，不能降级为单条查询失败。 */
    private static void rethrowRefreshFailure(ApiException e) throws ApiException {
        if (Protocol.SESSION_EXPIRED.equals(e.code) || Protocol.RATE_LIMITED.equals(e.code)) throw e;
    }
}
