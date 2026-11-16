package com.cxball;

import org.json.JSONArray;
import org.json.JSONObject;
import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;

import com.cxball.Protocol.ApiException;

import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * 刷课模块（P0：只读 + 可视化）。
 *
 * 仅访问课程「章节」页并记录章节/任务点概况，不做任何播放、提交或写操作。
 * 章节页（2026-11-15 实测）：
 *   https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/studentcourse
 *     ?courseid=...&clazzid=...&cpi=...&enc=...&t=...&pageHeader=1&v=2
 * 其中 enc 取课程页隐藏域 #enc（CourseCollector 写入 courseEnc），不是 workEnc。
 */
public class BrushCollector implements Collector {

    private static final Pattern PROGRESS =
            Pattern.compile("已完成任务点[:：]\\s*(\\d+)\\s*/\\s*(\\d+)");

    @Override
    public String type() {
        return "brush";
    }

    @Override
    public String label() {
        return "刷课";
    }

    @Override
    public boolean perCourse() {
        return true;
    }

    @Override
    public JSONArray fetch(ChaoxingClient client, JSONObject course) throws ApiException {
        String courseId = course.getString("courseId");
        String classId = course.getString("classId");
        String name = course.optString("courseName");
        String cpi = course.optString("cpi", "");
        String courseEnc = course.optString("courseEnc", course.optString("enc", ""));

        if (courseEnc.isEmpty()) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]缺少章节页 enc", false);
        }

        String url = "https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/studentcourse"
                + "?courseid=" + courseId
                + "&clazzid=" + classId
                + "&cpi=" + cpi
                + "&enc=" + courseEnc
                + "&t=" + System.currentTimeMillis()
                + "&pageHeader=1&v=2";
        String referer = "https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/stu"
                + "?courseid=" + courseId + "&clazzid=" + classId;

        String html = client.get(url, referer);
        Document doc = Jsoup.parse(html);
        String text = doc.text();

        int finishedPoints = 0, totalPoints = 0;
        Matcher pm = PROGRESS.matcher(text);
        if (pm.find()) {
            try {
                finishedPoints = Integer.parseInt(pm.group(1));
                totalPoints = Integer.parseInt(pm.group(2));
            } catch (NumberFormatException ignored) {
                // 保持 0/0
            }
        }

        // 章节树：div.chapter_item[id^=cur] 每个节点一个（含嵌套），标题/待完成数在内部
        int chapterCount = doc.select("div.chapter_item[id^=cur]").size();
        if (text.contains("暂无章节内容")) chapterCount = 0;

        JSONObject item = new JSONObject();
        item.put("type", "brush");
        item.put("courseId", courseId);
        item.put("courseName", name);
        item.put("title", "章节");
        item.put("status", chapterCount > 0 ? "已读取" : "暂无章节");
        item.put("chapterCount", chapterCount);
        item.put("finishedPoints", finishedPoints);
        item.put("totalPoints", totalPoints);
        item.put("link", url);

        JSONArray out = new JSONArray();
        out.put(item);
        return out;
    }
}
