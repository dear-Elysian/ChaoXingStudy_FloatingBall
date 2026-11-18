package com.cxball;

import org.json.JSONArray;
import org.json.JSONObject;

import com.cxball.Protocol.ApiException;

/**
 * 个人任务采集（2026-11-18 实测校准）：
 * 数据与分组任务同源（课程「任务」页 activeList），activeType=35 且
 * isSpecialPersonalTask=1 或 isnorm=0 为个人任务。
 *
 * 完成判定：详情接口 getMyGroupInfo 的 taskGroupInfo.submitTime 有具体时间即「已完成」；
 * 确认没有提交时间为「未完成」；查询失败为「提交状态未知」，避免误判未完成。
 * 输出 type=assignment，直接进入主面板作业栏；不发起任何写操作。
 */
public class PersonalTaskCollector implements Collector {

    @Override public String type() { return "assignment"; }

    @Override public String label() { return "个人任务"; }

    @Override public boolean perCourse() { return true; }

    @Override
    public JSONArray fetch(ChaoxingClient client, JSONObject course) throws ApiException {
        String courseId = course.getString("courseId");
        String classId = course.getString("classId");
        String name = course.optString("courseName");
        String fid = course.optString("fid", "");
        JSONArray list = ActiveTaskSupport.fetchActiveList(client, course);
        String referer = ActiveTaskSupport.listReferer(courseId, classId, fid);
        String uid = client.cookieValue("_uid");

        JSONArray out = new JSONArray();
        for (int i = 0; i < list.length(); i++) {
            JSONObject a = list.optJSONObject(i);
            if (a == null || !ActiveTaskSupport.isPersonalTask(a)) continue;

            String title = a.optString("nameOne", "").trim();
            if (title.isEmpty()) title = a.optString("nameTwo", "").trim();
            if (title.isEmpty()) continue;

            long start = a.optLong("startTime", 0L);
            long end = a.optLong("endTime", 0L);
            long activeId = a.optLong("id", 0L);
            String link = activeId > 0
                    ? ActiveTaskSupport.detailLink(client, courseId, classId, fid, activeId,
                            a.optInt("isnorm", 1) == 1, referer)
                    : "";
            String submitTime = ActiveTaskSupport.submitTime(client, activeId, uid, referer);
            String status = submitTime == null ? "提交状态未知" : submitTime.isBlank() ? "未完成" : "已完成";

            JSONObject item = Models.item("assignment", courseId, name, title, status,
                    end > 0 ? end : null,
                    start > 0 ? start : null,
                    link, null);
            item.put("personalTask", true);
            if (submitTime == null) item.put("submissionQueryFailed", true);
            if (activeId > 0) item.put("activeId", activeId);
            if (submitTime != null && !submitTime.isBlank()) item.put("submitTime", submitTime);
            out.put(item);
        }
        return out;
    }
}
