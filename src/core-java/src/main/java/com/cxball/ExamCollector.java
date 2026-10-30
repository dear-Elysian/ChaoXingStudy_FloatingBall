package com.cxball;

import org.json.JSONArray;
import org.json.JSONObject;
import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;

import com.cxball.Protocol.ApiException;

import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * 考试采集（2026-10-27 实测校准）：
 * GET https://mooc1.chaoxing.com/exam-ans/mooc2/exam/exam-list
 *   ?courseid&clazzid&cpi&ut=s&t=<ts>&stuenc&enc=<examEnc>&openc
 * 响应 HTML：li 列表，条目 p.overHidden2 为标题，p.status 为「已完成/未完成」，
 * 条目 onclik 为 viewExamAnswer('examId','examAnswerId') 或 goTest(...)。
 * 详情页（只读，不进入作答）：
 *   已完成：https://mooc1.chaoxing.com/exam-ans/exam/test/look?courseId&classId&examId&examAnswerId&cpi
 *   未完成：https://mooc1.chaoxing.com/exam-ans/exam/test/examcode/examnotes?courseId&classId&examId&cpi
 *
 * 只抓列表元信息；不点开、不开始、不提交考试。stuenc/examEnc/openc 由课程页隐藏域取得。
 */
public class ExamCollector implements Collector {

    private static final String LIST_URL = "https://mooc1.chaoxing.com/exam-ans/mooc2/exam/exam-list";
    private static final String LOOK_URL = "https://mooc1.chaoxing.com/exam-ans/exam/test/look";
    private static final String NOTES_URL = "https://mooc1.chaoxing.com/exam-ans/exam/test/examcode/examnotes";
    private static final String PAPER_URL = "https://mooc1.chaoxing.com/exam-ans/exam/lookPaper";
    private static final Pattern CALL = Pattern.compile("([A-Za-z_$][\\w$]*)\\s*\\(([^)]*)\\)");

    @Override public String type() { return "exam"; }

    @Override public String label() { return "考试"; }

    @Override public boolean perCourse() { return true; }

    @Override
    public JSONArray fetch(ChaoxingClient client, JSONObject course) throws ApiException {
        String courseId = course.getString("courseId");
        String classId = course.getString("classId");
        String name = course.optString("courseName");
        String cpi = course.optString("cpi", "");
        String stuenc = course.optString("stuenc", "");
        String examEnc = course.optString("examEnc", "");
        String openc = course.optString("openc", "");
        if (stuenc.isEmpty() || examEnc.isEmpty() || openc.isEmpty()) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]缺少 stuenc/examEnc/openc，需先 enrichSecrets", false);
        }

        String url = LIST_URL + "?courseid=" + courseId + "&clazzid=" + classId
                + "&cpi=" + cpi + "&ut=s&t=" + System.currentTimeMillis()
                + "&stuenc=" + stuenc + "&enc=" + examEnc + "&openc=" + openc;
        String referer = "https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/stu?courseid=" + courseId
                + "&clazzid=" + classId;
        String html = client.get(url, referer);
        if (!html.contains("考试列表") && !html.contains("暂无考试")) {
            throw new ApiException(Protocol.PARSE_ERROR,
                    "课程[" + name + "]考试页结构无法识别（疑似改版或 enc 失效）", false);
        }

        Document doc = Jsoup.parse(html);
        JSONArray out = new JSONArray();
        for (Element li : doc.select("li")) {
            Element titleEl = li.selectFirst("p.overHidden2");
            if (titleEl == null) continue;
            String title = titleEl.text().trim();
            if (title.isEmpty()) continue;

            String status = "";
            Element statusEl = li.selectFirst("p.status");
            if (statusEl != null) status = statusEl.text().trim();

            Element clickEl = li.selectFirst("[onclick]");
            String call = clickEl == null ? "" : clickEl.attr("onclick");
            ExamRef ref = parseCall(call);
            if (ref == null || ref.examId.isEmpty()) continue;

            String link;
            if (!ref.answerId.isEmpty()) {
                link = LOOK_URL + "?courseId=" + courseId + "&classId=" + classId
                        + "&examId=" + ref.examId + "&examAnswerId=" + ref.answerId + "&cpi=" + cpi;
            } else if (!ref.paperId.isEmpty()) {
                link = PAPER_URL + "?courseId=" + courseId + "&classId=" + classId
                        + "&paperId=" + ref.paperId + "&p=" + ref.position
                        + "&ut=s&cpi=" + cpi + "&examRelationId=" + ref.examId
                        + "&enc=" + ref.paperEnc + "&newMooc=true&openc=" + openc;
            } else {
                // 未完成/已结束：考试须知页是只读入口，不会直接开始作答
                link = NOTES_URL + "?courseId=" + courseId + "&classId=" + classId
                        + "&examId=" + ref.examId + "&cpi=" + cpi;
            }

            JSONObject item = Models.item("exam", courseId, name, title,
                    status.isEmpty() ? "未完成" : status, null, null, link, null);
            item.put("examId", ref.examId);
            if (!ref.answerId.isEmpty()) item.put("examAnswerId", ref.answerId);
            out.put(item);
        }
        return out;
    }

    /** 从 onclick 里解析考试入口参数；不支持的入口返回 null。 */
    private static ExamRef parseCall(String call) {
        Matcher m = CALL.matcher(call == null ? "" : call);
        if (!m.find()) return null;
        String fn = m.group(1);
        List<String> args = splitArgs(m.group(2));
        ExamRef r = new ExamRef();
        if ("viewExamAnswer".equals(fn) && args.size() >= 2) {
            r.examId = args.get(0);
            r.answerId = args.get(1);
            return r;
        }
        if ("goTest".equals(fn) && args.size() >= 2) {
            // goTest(courseId, tId, id, endTime, paperId, isRetest, lookpaperEnc, stopExam)
            r.examId = args.get(1);
            return r;
        }
        if ("lookUpPaper".equals(fn) || "lookUpPaperPreview".equals(fn)) {
            // lookUpPaper(id, courseId, paperId, position, enc)
            if (args.size() >= 3) {
                r.examId = args.get(0);
                r.paperId = args.get(2);
                r.position = args.size() > 3 && !args.get(3).isEmpty() ? args.get(3) : "test";
                r.paperEnc = args.size() > 4 ? args.get(4) : "";
                return r;
            }
        }
        return null;
    }

    /** 按逗号切分 JS 实参，去掉成对引号（参数为纯数字/十六进制串，无需处理转义）。 */
    private static List<String> splitArgs(String s) {
        List<String> out = new ArrayList<>();
        StringBuilder cur = new StringBuilder();
        char quote = 0;
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (quote != 0) {
                if (c == quote) quote = 0;
                else cur.append(c);
            } else if (c == '\'' || c == '"') {
                quote = c;
            } else if (c == ',') {
                out.add(cur.toString().trim());
                cur.setLength(0);
            } else {
                cur.append(c);
            }
        }
        out.add(cur.toString().trim());
        return out;
    }

    private static final class ExamRef {
        String examId = "";
        String answerId = "";
        String paperId = "";
        String position = "test";
        String paperEnc = "";
    }
}
