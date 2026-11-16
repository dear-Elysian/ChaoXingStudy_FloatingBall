package com.cxball;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import org.json.JSONArray;
import org.json.JSONObject;
import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;
import org.jsoup.select.Elements;

import com.cxball.Protocol.ApiException;

/**
 * 刷课规划器（v3.28）。
 *
 * 读取课程章节树，筛出「叶子节点 + 未完成」的章节，再逐章读取 knowledge/cards 的
 * mArg.attachments，得到任务点类型与 jobid，供 C# 侧用本地浏览器执行刷课。
 *
 * 只读：本类只 GET 章节页与 cards 页，不播放、不提交、不调用任何完成接口。
 */
public class BrushPlanner {

    private static final Pattern PROGRESS =
            Pattern.compile("已完成任务点[:：]\\s*(\\d+)\\s*/\\s*(\\d+)");
    private static final Pattern STUDY_ENC =
            Pattern.compile("var\\s+enc\\s*=\\s*[\"']([0-9a-zA-Z]+)[\"']");

    private static final Set<String> BRUSHABLE = Set.of("doc", "video", "audio");

    /** 规划结果：mode=empty（无内容）/ finished（已刷完）/ start（有可刷章节） */
    public JSONObject plan(ChaoxingClient client, CourseCollector courses, String courseId) throws ApiException {
        JSONObject course = findCourse(courses.fetchCourses(client), courseId);
        if (course == null) {
            throw new ApiException(Protocol.PARSE_ERROR, "课程列表中未找到该课程，请先刷新课程列表", false);
        }
        courses.enrichSecrets(client, course);

        String courseName = course.optString("courseName", courseId);
        String classId = course.getString("classId");
        String cpi = course.optString("cpi", "");
        String courseEnc = course.optString("courseEnc", course.optString("stuenc", ""));
        if (courseEnc.isEmpty()) {
            throw new ApiException(Protocol.PARSE_ERROR, "课程[" + courseName + "]缺少章节页 enc", false);
        }

        String chapterUrl = "https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/studentcourse"
                + "?courseid=" + courseId
                + "&clazzid=" + classId
                + "&cpi=" + cpi
                + "&enc=" + courseEnc
                + "&t=" + System.currentTimeMillis()
                + "&pageHeader=1&v=2";
        String referer = "https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/stu"
                + "?courseid=" + courseId + "&clazzid=" + classId;

        String html = client.get(chapterUrl, referer);
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

        String studyEnc = courseEnc;
        Matcher em = STUDY_ENC.matcher(html);
        if (em.find()) studyEnc = em.group(1);

        List<JSONObject> nodes = parseChapterNodes(doc);

        JSONArray chapters = new JSONArray();
        for (JSONObject node : nodes) {
            if (node.optBoolean("leaf") && !node.optBoolean("completed") && node.optInt("pending", 0) > 0) {
                String chapterId = node.getString("chapterId");
                TaskInfo info = fetchTasks(client, courseId, classId, cpi, chapterId, referer);
                if (info == null || info.brushable) {
                    // cards 读取失败时仍交给浏览器尝试；解析成功且有可刷任务才保留
                    JSONObject ch = new JSONObject();
                    ch.put("chapterId", chapterId);
                    ch.put("label", node.optString("label"));
                    ch.put("name", node.optString("name"));
                    ch.put("level", node.optInt("level", 1));
                    ch.put("pending", node.optInt("pending", 1));
                    ch.put("tasks", info == null ? new JSONArray() : info.tasks);
                    chapters.put(ch);
                }
            }
        }

        String mode;
        boolean allCompleted = !nodes.isEmpty();
        for (JSONObject node : nodes) {
            if (!node.optBoolean("completed")) {
                allCompleted = false;
                break;
            }
        }
        if (nodes.isEmpty()) {
            mode = "empty";
        } else if (chapters.length() > 0) {
            mode = "start";
        } else if (finishedPoints > 0 || allCompleted) {
            // 没有可刷章节：课程已有完成记录，剩余待完成任务点都是做题/外链等不可刷类型
            mode = "finished";
        } else {
            mode = "empty";
        }

        JSONObject out = new JSONObject();
        out.put("courseId", courseId);
        out.put("courseName", courseName);
        out.put("classId", classId);
        out.put("cpi", cpi);
        out.put("enc", studyEnc);
        // 章节页（studentcourse）与学习页（studentstudy）的 enc 不一定是同一个，分别带回
        out.put("courseEnc", courseEnc);
        out.put("mode", mode);
        out.put("chapterCount", nodes.size());
        out.put("finishedPoints", finishedPoints);
        out.put("totalPoints", totalPoints);
        out.put("chapters", chapters);
        return out;
    }

    private static JSONObject findCourse(JSONArray list, String courseId) {
        for (int i = 0; i < list.length(); i++) {
            JSONObject c = list.optJSONObject(i);
            if (c != null && courseId.equals(c.optString("courseId"))) return c;
        }
        return null;
    }

    private static List<JSONObject> parseChapterNodes(Document doc) {
        List<JSONObject> out = new ArrayList<>();
        Elements items = doc.select("div.chapter_item[id^=cur]");
        for (Element item : items) {
            String id = item.id();
            if (id.length() <= 3) continue;
            String chapterId = id.substring(3).trim();
            if (chapterId.isEmpty()) continue;

            int level = (int) item.parents().stream()
                    .filter(p -> "ul".equals(p.tagName())).count() + 1;

            Element sbar = item.selectFirst(".catalog_name .catalog_sbar");
            if (sbar == null) sbar = item.selectFirst(".catalog_sbar");
            String label = sbar == null ? "" : sbar.text().trim();

            String name = "";
            Element cn = item.selectFirst(".catalog_name");
            if (cn != null) {
                name = cn.hasAttr("title") ? cn.attr("title").trim() : cn.text().trim();
                if (!label.isEmpty() && name.startsWith(label)) {
                    name = name.substring(label.length()).trim();
                }
            }

            Element task = ownTask(item);
            boolean completed = task != null && task.selectFirst(".catalog_state.icon_yiwanc") != null;
            Element pts = task == null ? null : task.selectFirst(".catalog_points_yi");
            int pending = 0;
            if (pts != null) {
                try {
                    pending = Integer.parseInt(pts.text().replaceAll("[^0-9]", ""));
                } catch (NumberFormatException ignored) {
                    pending = 0;
                }
            } else if (!completed) {
                pending = 1;   // 状态未知：按待完成处理
            }
            // 注意：Element.select 会把节点自身也算进去，叶子判定必须显式排除自身
            boolean leaf = true;
            for (Element e : item.getAllElements()) {
                if (e != item && e.hasClass("chapter_item") && e.id().startsWith("cur")) {
                    leaf = false;
                    break;
                }
            }

            JSONObject node = new JSONObject();
            node.put("chapterId", chapterId);
            node.put("label", label);
            node.put("name", name);
            node.put("level", level);
            node.put("completed", completed);
            node.put("pending", pending);
            node.put("leaf", leaf);
            out.add(node);
        }
        return out;
    }

    /** 节点自身的 catalog_task（遇到嵌套 chapter_item 即停止，避免读到子节点的状态）。 */
    private static Element ownTask(Element item) {
        for (Element e : item.getAllElements()) {
            if (e == item) continue;
            if (e.hasClass("chapter_item")) return null;
            if (e.hasClass("catalog_task")) return e;
        }
        return null;
    }

    private static final class TaskInfo {
        final JSONArray tasks;
        final boolean brushable;

        TaskInfo(JSONArray tasks, boolean brushable) {
            this.tasks = tasks;
            this.brushable = brushable;
        }
    }

    /** 读取单章节 cards 页；返回 null 表示无法判断（交给浏览器兜底）。 */
    private TaskInfo fetchTasks(ChaoxingClient client, String courseId, String classId,
                                String cpi, String chapterId, String referer) throws ApiException {
        JSONArray tasks = new JSONArray();
        java.util.Set<String> seen = new java.util.HashSet<>();
        boolean brushable = false;
        boolean anyParsed = false;

        // 章节可能按任务类型分成多个 cards 页（num=0 视频、num=1 文档……），
        // 逐页收集，直到某一页没有新任务或没有 attachments（最多 6 页，覆盖后续新增类型）
        for (int num = 0; num < 6; num++) {
            String url = "https://mooc1.chaoxing.com/mooc-ans/knowledge/cards"
                    + "?clazzid=" + classId
                    + "&courseid=" + courseId
                    + "&knowledgeid=" + chapterId
                    + "&num=" + num + "&ut=s"
                    + "&cpi=" + cpi
                    + "&v=2025-0424-1038-4&mooc2=1&isMicroCourse=false&editorPreview=0&crossId=";
            String html;
            try {
                html = client.get(url, referer);
            } catch (ApiException e) {
                if (Protocol.SESSION_EXPIRED.equals(e.code) || Protocol.RATE_LIMITED.equals(e.code)) throw e;
                if (num == 0) return null;
                break;
            }

            JSONObject mArg = parseJsObject(html, "mArg");
            if (mArg == null || !mArg.has("attachments")) {
                if (num == 0) return null;
                break;
            }
            anyParsed = true;
            JSONArray atts = mArg.optJSONArray("attachments");
            if (atts == null || atts.length() == 0) break;

            boolean added = false;
            for (int i = 0; i < atts.length(); i++) {
                JSONObject att = atts.optJSONObject(i);
                if (att == null) continue;
                JSONObject prop = att.optJSONObject("property");
                if (prop == null) {
                    String raw = att.optString("property", "");
                    if (raw.startsWith("{")) {
                        try {
                            prop = new JSONObject(raw);
                        } catch (Exception ignored) {
                            prop = null;
                        }
                    }
                }
                if (prop == null) prop = new JSONObject();

                boolean job = truthy(att.opt("job")) || truthy(prop.opt("job"));
                String module = prop.optString("module", att.optString("module", ""));
                String kind = kindOf(module);
                if (kind == null) kind = kindOf(att.optString("type", ""));
                if (kind == null) kind = "other";
                String jobid = firstNonEmpty(
                        att.optString("jobid", ""),
                        att.optString("jobId", ""),
                        prop.optString("jobid", ""));
                String name = firstNonEmpty(
                        prop.optString("name", ""),
                        att.optString("name", ""),
                        prop.optString("title", ""),
                        att.optString("title", ""));
                if (!job) continue;
                if (!jobid.isEmpty() && !seen.add(jobid)) continue;

                JSONObject t = new JSONObject();
                t.put("kind", kind);
                t.put("jobid", jobid);
                t.put("name", name);
                tasks.put(t);
                added = true;
                // 未知类型模块也先纳入：执行器会尝试滚动到底部完成（平台很多模块按滚动判定）
                if (BRUSHABLE.contains(kind) || "other".equals(kind)) brushable = true;
            }
            if (!added && num > 0) break;
        }
        return anyParsed ? new TaskInfo(tasks, brushable) : null;
    }

    private static String kindOf(String module) {
        if (module == null) return null;
        String m = module.trim().toLowerCase(Locale.ROOT);
        if (m.isEmpty()) return null;
        if (m.contains("audio")) return "audio";
        if (m.contains("video")) return "video";
        if (m.contains("doc") || m.equals("document") || m.contains("pdf") || m.contains("ppt")) return "doc";
        if (m.contains("work") || m.contains("homework")) return "work";
        if (m.contains("hyperlink") || m.contains("link")) return "link";
        return null;
    }

    private static boolean truthy(Object v) {
        if (v == null) return false;
        if (v instanceof Boolean b) return b;
        if (v instanceof Number n) return n.intValue() != 0;
        String s = String.valueOf(v).trim().toLowerCase(Locale.ROOT);
        return "true".equals(s) || "1".equals(s);
    }

    private static String firstNonEmpty(String... values) {
        for (String v : values) {
            if (v != null && !v.isBlank()) return v.trim();
        }
        return "";
    }

    /** 从页面脚本里取 `name = {...}` 的对象字面量并尽量转成 JSON。 */
    private static JSONObject parseJsObject(String html, String name) {
        // 页面里可能存在 `mArg = ""; try{ mArg = {...}` 的初始化写法，必须直接匹配到对象起点
        Matcher m = Pattern.compile("\\b" + Pattern.quote(name) + "\\s*=\\s*\\{").matcher(html);
        if (!m.find()) return null;
        int start = m.end() - 1;
        String raw = balancedObject(html, start);
        if (raw == null) return null;
        try {
            return new JSONObject(raw);
        } catch (Exception ignored) {
            // 继续尝试宽松修正
        }
        try {
            String fixed = raw
                    .replaceAll("([{,]\\s*)([A-Za-z_$][A-Za-z0-9_$]*)\\s*:", "$1\"$2\":")
                    .replaceAll(",\\s*([}\\]])", "$1");
            return new JSONObject(fixed);
        } catch (Exception ignored) {
            return null;
        }
    }

    private static String balancedObject(String s, int start) {
        int depth = 0;
        boolean inStr = false;
        char quote = 0;
        boolean esc = false;
        for (int i = start; i < s.length(); i++) {
            char c = s.charAt(i);
            if (inStr) {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == quote) inStr = false;
                continue;
            }
            if (c == '"' || c == '\'') {
                inStr = true;
                quote = c;
            } else if (c == '{') {
                depth++;
            } else if (c == '}') {
                depth--;
                if (depth == 0) return s.substring(start, i + 1);
            }
        }
        return null;
    }
}
