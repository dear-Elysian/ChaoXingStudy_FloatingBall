package com.cxball;

import java.time.LocalDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.HashSet;
import java.util.Set;
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
 *
 * 分页（2026-11-13 实测）：页面 JS 为 $("#page").paging({ nowPage:1, pageNum:2, ... })，
 * 翻页地址追加 &pageNum=N。第 1 页最多 12 条，必须逐页抓取；总页数从 pageNum 配置解析。
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
    /** 页面 paging 配置里的总页数：pageNum : 2 */
    private static final Pattern TOTAL_PAGES = Pattern.compile("pageNum\\s*:\\s*(\\d+)");
    /** 防御性上限，避免异常页面导致无限翻页。 */
    private static final int MAX_PAGES = 50;

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
        validate(html, name);

        JSONArray out = parse(html, course);
        Set<String> seen = new HashSet<>();
        for (int i = 0; i < out.length(); i++) seen.add(keyOf(out.getJSONObject(i)));

        // 第 1 页默认最多 12 条；后续页必须显式追加 pageNum，否则只会拿到第 1 页。
        int total = Math.min(parseTotalPages(html), MAX_PAGES);
        for (int page = 2; page <= total; page++) {
            String pageHtml;
            try {
                pageHtml = client.get(url + "&pageNum=" + page, referer);
            } catch (ApiException e) {
                // 单页失败保留已抓到的数据；会话失效/风控仍整体上抛
                if (Protocol.SESSION_EXPIRED.equals(e.code) || Protocol.RATE_LIMITED.equals(e.code)) throw e;
                break;
            }
            if (!pageHtml.contains("作业列表")) break;

            JSONArray more = parse(pageHtml, course);
            for (int i = 0; i < more.length(); i++) {
                JSONObject item = more.getJSONObject(i);
                if (seen.add(keyOf(item))) out.put(item);
            }
        }
        return out;
    }

    private static void validate(String html, String name) throws ApiException {
        if (html.contains("无权限的操作")) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]作业页提示无权限（enc 可能失效）", true);
        }
        if (!html.contains("作业列表")) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]作业页结构无法识别", false);
        }
    }

    /** 页面 JS 的 paging 配置：pageNum 为总页数。解析失败按 1 页处理。 */
    private static int parseTotalPages(String html) {
        Matcher m = TOTAL_PAGES.matcher(html);
        if (!m.find()) return 1;
        try {
            return Math.max(1, Integer.parseInt(m.group(1)));
        } catch (NumberFormatException e) {
            return 1;
        }
    }

    /** 去重键：优先用详情链接（含唯一 workId），缺失时退回 courseId|title。 */
    private static String keyOf(JSONObject item) {
        String link = item.optString("link", "");
        return link.isBlank()
                ? item.optString("courseId") + "|" + item.optString("title")
                : link;
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
