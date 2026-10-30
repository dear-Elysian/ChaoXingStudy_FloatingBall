package com.cxball;

import java.util.regex.Matcher;
import java.util.regex.Pattern;

import org.json.JSONArray;
import org.json.JSONObject;
import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;

import com.cxball.Protocol.ApiException;

/**
 * 课程列表采集（附录 A.1，2026-10-20 实测校准）：
 * POST https://mooc1-1.chaoxing.com/mooc-ans/visit/courselistdata
 * body: courseType=1&courseFolderId=0&query=&superstarClass=0
 * 响应实测为 HTML 片段（非 JSON）：
 *   li.course[courseId][clazzId]
 *     span.course-name(title 为全称) ＝ 课程名
 *     .course-info p.line2 ＝ 教师（多个取最后一个非空，第一个多为空副标题）
 *     a[href*=stucoursemiddle] 的 href 含 cpi（用户级 personId）
 *
 * stuenc/enc 获取链（实测）：
 *   1) stucoursemiddle 302 Location（不跟随重定向）→ 含 stuenc(enc 参数) + cpi
 *   2) Location 中 pageHeader=-1 改为 8 后 GET（-1 会被重定向去登录页）→
 *      页面 hidden input #workEnc = work/list 的 enc，#enc = stuenc，#examEnc = 考试 enc（预留），
 *      input[name=cfid] = 随堂练习 activelist 需要的 fid。
 */
public class CourseCollector {

    private static final String LIST_URL = "https://mooc1-1.chaoxing.com/mooc-ans/visit/courselistdata";
    private static final String LIST_BODY = "courseType=1&courseFolderId=0&query=&superstarClass=0";
    private static final String LIST_REFERER = "https://i.chaoxing.com/";

    public JSONArray fetchCourses(ChaoxingClient client) throws ApiException {
        String body = client.postForm(LIST_URL, LIST_BODY, LIST_REFERER);

        // 实测形态：HTML 片段 → Jsoup
        JSONArray out = parseHtml(body);
        if (!out.isEmpty()) return out;

        // 兼容回退：纯 JSON 形态（旧假设，若超星改回 JSON 仍可用）
        out = parseJson(body);
        if (!out.isEmpty()) return out;

        throw new ApiException(Protocol.PARSE_ERROR, "课程列表响应结构无法识别（疑似改版），需校准解析器", false);
    }

    private static JSONArray parseHtml(String body) {
        Document doc = Jsoup.parse(body);
        JSONArray out = new JSONArray();
        for (Element li : doc.select("li.course[courseId][clazzId]")) {
            String courseId = li.attr("courseId").trim();
            String classId = li.attr("clazzId").trim();
            if (courseId.isEmpty() || classId.isEmpty()) continue;

            String name = "";
            Element nameEl = li.selectFirst("span.course-name");
            if (nameEl != null) {
                name = !nameEl.attr("title").isBlank() ? nameEl.attr("title").trim()
                        : nameEl.text().replaceAll("\\s+", " ").trim();
            }
            String teacher = "";
            for (Element p : li.select(".course-info p.line2")) {
                String t = !p.attr("title").isBlank() ? p.attr("title").trim() : p.text().trim();
                if (!t.isBlank()) teacher = t;   // 取最后一个非空（第一个多为空副标题）
            }
            JSONObject c = Models.course(courseId, classId, name, teacher, null, null);
            Element a = li.selectFirst("a[href*=stucoursemiddle]");
            if (a != null) {
                Matcher cm = Pattern.compile("[?&]cpi=(\\d+)").matcher(a.attr("href"));
                if (cm.find()) c.put("cpi", cm.group(1));
            }
            out.put(c);
        }
        return out;
    }

    private static JSONArray parseJson(String body) {
        JSONArray out = new JSONArray();
        try {
            JSONObject json = new JSONObject(body);
            JSONArray arr = findCourseArray(json);
            if (arr == null) return out;
            for (int i = 0; i < arr.length(); i++) {
                JSONObject c = arr.getJSONObject(i);
                out.put(Models.course(
                        optId(c, "courseId", "courseid"),
                        optId(c, "classId", "clazzid", "classid"),
                        c.optString("courseName", c.optString("name", "")),
                        c.optString("teacherName", c.optString("teacherfactor", "")),
                        null, null));
            }
        } catch (Exception ignored) { /* 非 JSON，返回空 */ }
        return out;
    }

    /**
     * 补全课程的 stuenc/enc/cpi（另存 examEnc 预留）。
     * stuenc = stucoursemiddle 302 Location 的 enc 参数；enc = 课程页 hidden #workEnc（每课程固定）。
     */
    public void enrichSecrets(ChaoxingClient client, JSONObject course) throws ApiException {
        String courseId = course.getString("courseId");
        String classId = course.getString("classId");
        String name = course.optString("courseName");
        String cpi = course.optString("cpi", "");

        // 第一步：302 Location 提供 stuenc + cpi
        String middle = "https://mooc1-1.chaoxing.com/mooc-ans/visit/stucoursemiddle?courseid=" + courseId
                + "&clazzid=" + classId + "&vc=1" + (cpi.isEmpty() ? "" : "&cpi=" + cpi) + "&ismooc2=1&v=2";
        String loc = client.redirectLocation(middle, LIST_REFERER);
        if (loc == null || !loc.contains("mycourse/stu")) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]中间页未重定向到课程页（stucoursemiddle 异常）", false);
        }
        Matcher me = Pattern.compile("[?&]enc=([0-9a-f]{32})").matcher(loc);
        if (me.find()) course.put("stuenc", me.group(1));
        Matcher mc = Pattern.compile("[?&]cpi=(\\d+)").matcher(loc);
        if (mc.find()) course.put("cpi", mc.group(1));
        if (course.optString("stuenc").isEmpty()) {
            throw new ApiException(Protocol.PARSE_ERROR, "课程[" + name + "]未从中间页取得 stuenc", false);
        }

        // 第二步：课程页 hidden input 提供 workEnc / examEnc / cfid（pageHeader 必须为 8）
        String page = client.get(loc.replace("pageHeader=-1", "pageHeader=8"), LIST_REFERER);
        Document doc = Jsoup.parse(page);
        course.put("enc", inputValue(doc, "workEnc"));
        String examEnc = inputValue(doc, "examEnc");
        if (!examEnc.isEmpty()) course.put("examEnc", examEnc);
        String openc = inputValue(doc, "openc");
        if (!openc.isEmpty()) course.put("openc", openc);
        // 主题讨论（groupweb）：列表接口路径需要课程级 bbsid，随课程缓存（附录 A.3.3）
        String bbsid = inputValue(doc, "bbsid");
        if (bbsid.isEmpty()) bbsid = inputValueByName(doc, "bbsid");   // 兼容隐藏域只带 name 的形态
        if (!bbsid.isEmpty()) {
            course.put("bbsid", bbsid);
            // groupweb 会按课程上下文把 stuenc/t/enc 写进自己的会话 Cookie（域名 groupweb.chaoxing.com），
            // 讨论接口必须有这些 Cookie 才返回数据。缓存课程页隐藏域，供讨论采集器预热会话用。
            course.put("bbsEnc", inputValue(doc, "enc"));                 // 注意：#enc（stuenc）≠ #workEnc
            course.put("bbsT", inputValue(doc, "t"));
            String showAIEnc = inputValue(doc, "showAIEnc");
            if (!showAIEnc.isEmpty()) course.put("showAIEnc", showAIEnc);
        }
        // 随堂练习 activelist 需要 fid（学校/租户 id）：课程页隐藏域 cfid 实测为 3239，
        // 而 #fid 是另一个值（实测 34557），不能混用。缺 cfid 时退回页面里的 fid= 链接参数。
        String fid = inputValueByName(doc, "cfid");
        if (fid.isEmpty()) {
            Matcher mf = Pattern.compile("[?&]fid=(\\d+)").matcher(page);
            if (mf.find()) fid = mf.group(1);
        }
        if (!fid.isEmpty()) course.put("fid", fid);
        if (course.optString("enc").isEmpty()) {
            throw new ApiException(Protocol.PARSE_ERROR, "课程[" + name + "]课程页未找到 workEnc", false);
        }
    }

    private static String inputValue(Document doc, String id) {
        Element e = doc.getElementById(id);
        return e == null ? "" : e.attr("value").trim();
    }

    private static String inputValueByName(Document doc, String name) {
        Element e = doc.selectFirst("input[name=" + name + "]");
        return e == null ? "" : e.attr("value").trim();
    }

    /** 在 JSON 树里递归找"元素含 courseId/courseid 键"的数组 */
    private static JSONArray findCourseArray(Object node) {
        if (node instanceof JSONArray arr) {
            if (arr.length() > 0 && arr.get(0) instanceof JSONObject first) {
                if (first.has("courseId") || first.has("courseid")) return arr;
            }
            for (int i = 0; i < arr.length(); i++) {
                JSONArray r = findCourseArray(arr.get(i));
                if (r != null) return r;
            }
        } else if (node instanceof JSONObject obj) {
            for (String key : obj.keySet()) {
                JSONArray r = findCourseArray(obj.get(key));
                if (r != null) return r;
            }
        }
        return null;
    }

    private static String optId(JSONObject o, String... keys) {
        for (String k : keys) {
            Object v = o.opt(k);
            if (v != null) return String.valueOf(v);
        }
        return "";
    }
}
