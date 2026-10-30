package com.cxball;

import org.json.JSONObject;

/** 附录 B.4 数据模型：Course / Item */
public final class Models {

    private Models() {}

    public static JSONObject course(String courseId, String classId, String courseName,
                                    String teacherName, String stuenc, String enc) {
        JSONObject o = new JSONObject();
        o.put("courseId", nullToEmpty(courseId));
        o.put("classId", nullToEmpty(classId));
        o.put("courseName", nullToEmpty(courseName));
        o.put("teacherName", nullToEmpty(teacherName));
        if (stuenc != null) o.put("stuenc", stuenc);
        if (enc != null) o.put("enc", enc);
        return o;
    }

    public static JSONObject item(String type, String courseId, String courseName, String title,
                                  String status, Long deadline, Long publishedAt, String link, String raw) {
        JSONObject o = new JSONObject();
        o.put("type", type);                       // assignment | notice | exam(预留)
        o.put("courseId", nullToEmpty(courseId));
        o.put("courseName", nullToEmpty(courseName));
        o.put("title", nullToEmpty(title));
        o.put("status", nullToEmpty(status));
        if (deadline != null) o.put("deadline", deadline);
        if (publishedAt != null) o.put("publishedAt", publishedAt);
        o.put("link", nullToEmpty(link));
        if (raw != null) o.put("raw", raw);        // 原始片段，仅调试期保留
        return o;
    }

    private static String nullToEmpty(String s) { return s == null ? "" : s; }
}
