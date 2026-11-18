package com.cxball;

import org.json.JSONArray;
import org.json.JSONObject;

import com.cxball.Protocol.ApiException;

/**
 * 分组任务采集（2026-11-18 实测校准）：
 * GET https://mobilelearn.chaoxing.com/v2/apis/active/student/activelist
 *   ?fid&courseId&classId&showNotStartedActive=0&_=<ts>
 * 响应 data.activeList[] 中 activeType=35 为任务；其中
 * isSpecialPersonalTask=1 或 isnorm=0 是「个人任务」，归入作业栏（PersonalTaskCollector），
 * 其余才是分组任务。
 *
 * 只抓列表元信息（只读）；不做提交。fid 由 CourseCollector.enrichSecrets 从课程页 cfid 取得。
 */
public class GroupTaskCollector implements Collector {

    @Override public String type() { return "groupTask"; }

    @Override public String label() { return "分组任务"; }

    @Override public boolean perCourse() { return true; }

    @Override
    public JSONArray fetch(ChaoxingClient client, JSONObject course) throws ApiException {
        String courseId = course.getString("courseId");
        String classId = course.getString("classId");
        String name = course.optString("courseName");
        String fid = course.optString("fid", "");
        JSONArray list = ActiveTaskSupport.fetchActiveList(client, course);
        String referer = ActiveTaskSupport.listReferer(courseId, classId, fid);

        JSONArray out = new JSONArray();
        long now = System.currentTimeMillis();
        for (int i = 0; i < list.length(); i++) {
            JSONObject a = list.optJSONObject(i);
            if (a == null) continue;
            if (a.optInt("activeType", a.optInt("type", -1)) != ActiveTaskSupport.TYPE_TASK) continue;
            if (ActiveTaskSupport.isPersonalTask(a)) continue;   // 个人任务在作业栏展示

            String title = a.optString("nameOne", "").trim();
            if (title.isEmpty()) title = a.optString("nameTwo", "").trim();
            if (title.isEmpty()) continue;

            long start = a.optLong("startTime", 0L);
            long end = a.optLong("endTime", 0L);
            // status：0 未开始 / 1 进行中 / 2 已结束；缺失时按时间兜底。
            // 平台偶尔在截止后仍给 1，这里以 endTime 为准再校正一次。
            int state = a.optInt("status", -1);
            String status;
            if (state == 2) {
                status = "已结束";
            } else if (state == 0) {
                status = "未开始";
            } else if (state == 1) {
                status = "进行中";
            } else if (start > now) {
                status = "未开始";
            } else if (end > 0 && end <= now) {
                status = "已结束";
            } else {
                status = "进行中";
            }
            if (!"已结束".equals(status) && end > 0 && end <= now) status = "已结束";

            long activeId = a.optLong("id", 0L);
            // 详情页地址：按官方任务页逻辑，用 getActiveInfoForMe 的
            // isNorm / toAnswerPage / useNoteEditor 选择新版/旧版、作答/查看页
            String link = activeId > 0
                    ? ActiveTaskSupport.detailLink(client, courseId, classId, fid, activeId,
                            a.optInt("isnorm", 1) == 1, referer)
                    : "";
            JSONObject item = Models.item("groupTask", courseId, name, title, status,
                    end > 0 ? end : null,
                    start > 0 ? start : null,
                    link, null);
            item.put("ended", "已结束".equals(status));
            if (activeId > 0) item.put("activeId", activeId);
            out.put(item);
        }
        return out;
    }
}
