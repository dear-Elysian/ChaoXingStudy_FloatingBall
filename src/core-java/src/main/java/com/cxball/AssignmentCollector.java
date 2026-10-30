package com.cxball;

import java.time.LocalDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import org.json.JSONArray;
import org.json.JSONObject;
import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;

import com.cxball.Protocol.ApiException;

/**
 * 作业列表采集（附录 A.2，2026-10-20 实测校准）：
 * GET https://mooc1.chaoxing.com/mooc2/work/list?courseId&classId&cpi&ut=s&t&stuenc&enc
 * 响应为 HTML 页面（title"作业列表"）。条目结构实测：
 *   div.bottomList li[data]          data = 作业详情完整 URL
 *     p.overHidden2                  标题（aria-label "标题 ; 状态" 为兜底）
 *     p.status                       状态原文（已完成/待批阅/未完成…）
 *     div.time.notOver               "剩余98小时58分钟"（无绝对日期，换算近似 deadline）
 *     div.time.over（推测）           已过期；能解析日期则用，否则 deadline=0 保证归入已过期
 * 本期只抓列表元信息，不抓作业详情（详情接口预留）。
 */
public class AssignmentCollector implements Collector {

    private static final String WORK_URL = "https://mooc1.chaoxing.com/mooc2/work/list";
    private static final DateTimeFormatter[] TIME_FMT = {
            DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm"),
            DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss")
    };
    private static final Pattern REMAIN =
            Pattern.compile("剩余(?:(\\d+)天)?(?:(\\d+)小时)?(?:(\\d+)分钟)?");
    private static final Pattern FULL_TIME = Pattern.compile("(\\d{4}-\\d{2}-\\d{2}\\s+\\d{2}:\\d{2}(:\\d{2})?)");

    @Override public String type() { return "assignment"; }

    @Override public String label() { return "作业"; }

    @Override public boolean perCourse() { return true; }

    @Override
    public JSONArray fetch(ChaoxingClient client, JSONObject course) throws ApiException {
        String courseId = course.getString("courseId");
        String classId = course.getString("classId");
        String cpi = course.optString("cpi", "");
        String stuenc = course.optString("stuenc", "");
        String enc = course.optString("enc", "");
        String name = course.optString("courseName");
        if (stuenc.isEmpty() || enc.isEmpty()) {
            throw new ApiException(Protocol.INTERNAL,
                    "课程[" + name + "]缺少 stuenc/enc，需先 enrichSecrets", false);
        }
        String url = WORK_URL + "?courseId=" + courseId + "&classId=" + classId
                + "&cpi=" + cpi + "&ut=s&t=" + System.currentTimeMillis()
                + "&stuenc=" + stuenc + "&enc=" + enc;
        String referer = "https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/stu?courseid=" + courseId + "&clazzid=" + classId;
        String html = client.get(url, referer);
        if (html.contains("无权限的操作")) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]作业页提示无权限（enc 可能失效）", true);
        }
        if (!html.contains("作业列表")) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]作业页结构无法识别", false);
        }
        return parse(html, course);
    }

    private JSONArray parse(String html, JSONObject course) {
        Document doc = Jsoup.parse(html);
        JSONArray out = new JSONArray();
        String courseId = course.getString("courseId");
        String courseName = course.optString("courseName");

        for (Element li : doc.select("div.bottomList li[data]")) {
            String title = "";
            Element t = li.selectFirst("p.overHidden2");
            if (t != null) title = t.text().replaceAll("\\s+", " ").trim();
            if (title.isBlank()) {
                String al = li.attr("aria-label");   // "标题 ; 状态"
                int cut = al.indexOf(" ;");
                title = (cut > 0 ? al.substring(0, cut) : al).trim();
            }
            if (title.isBlank()) continue;

            String status = "";
            Element st = li.selectFirst("p.status");
            if (st != null) status = st.text().trim();

            Long deadline = parseDeadline(li.selectFirst("div.time"));

            out.put(Models.item("assignment", courseId, courseName, title, status,
                    deadline, null, li.attr("data"), null));
        }
        return out;
    }

    /** "剩余X天X小时X分钟" → now+δ（近似 deadline，分钟级）；已过期条目 → 解析日期或 0 */
    private static Long parseDeadline(Element timeEl) {
        if (timeEl == null) return null;
        String text = timeEl.text().replaceAll("\\s+", "");

        if (timeEl.hasClass("over") || text.contains("已过")) {
            Matcher dt = FULL_TIME.matcher(timeEl.text());
            if (dt.find()) {
                Long t = parseTime(dt.group(1));
                if (t != null) return t;
            }
            return 0L;   // 精确过去时刻未知：0 保证前端归入"已过期"
        }

        Matcher m = REMAIN.matcher(text);
        if (m.find() && (m.group(1) != null || m.group(2) != null || m.group(3) != null)) {
            long d = m.group(1) == null ? 0 : Long.parseLong(m.group(1));
            long h = m.group(2) == null ? 0 : Long.parseLong(m.group(2));
            long mi = m.group(3) == null ? 0 : Long.parseLong(m.group(3));
            return System.currentTimeMillis() + ((d * 24 + h) * 60 + mi) * 60_000L;
        }
        return null;
    }

    private static Long parseTime(String s) {
        for (DateTimeFormatter f : TIME_FMT) {
            try {
                return LocalDateTime.parse(s, f).atZone(ZoneId.systemDefault()).toInstant().toEpochMilli();
            } catch (Exception ignored) { }
        }
        return null;
    }
}
