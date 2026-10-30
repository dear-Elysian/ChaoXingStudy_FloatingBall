package com.cxball;

import java.net.URI;

import org.json.JSONArray;
import org.json.JSONObject;
import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;

import com.cxball.Protocol.ApiException;

/**
 * 收件箱消息采集（附录 A.3，2026-10-20 实测校准）：
 * myNotice 页列表实为 AJAX 加载（原"服务端渲染 HTML"假设作废），数据接口：
 *   POST https://notice.chaoxing.com/pc/notice/getNoticeList
 *   form: type=&notice_type=&lastValue=&sort=&folderUUID=&kw=&startTime=&endTime=
 * 响应 JSON：notices.list[]，字段 content(多行)/createrName/isread(1已读)/insertTime(ms)/
 *   attachment(JSON 字符串，[0].att_web 含 url 与 courseId)。
 * 本期只取第一页；分页游标 lastValue 预留。
 */
public class NoticeCollector implements Collector {

    private static final String BASE_URL = "https://i.chaoxing.com/base";
    private static final String LIST_URL = "https://notice.chaoxing.com/pc/notice/getNoticeList";
    private static final String LIST_BODY = "type=&notice_type=&lastValue=&sort=&folderUUID=&kw=&startTime=&endTime=";
    private static final String LIST_REFERER = "https://notice.chaoxing.com/pc/notice/myNotice";

    /** myNotice 入口页的 s 参数（附录 A.3）：从 base 页 iframe src 提取，仅作 Referer，不解析 DOM。 */
    private volatile String listReferer = LIST_REFERER;
    private volatile boolean refererResolved = false;

    /** 换账号/重新登录后，入口页 s 可能失效，下次抓取重新解析。 */
    public void resetReferer() {
        listReferer = LIST_REFERER;
        refererResolved = false;
    }

    @Override public String type() { return "notice"; }

    @Override public String label() { return "收件箱"; }

    @Override public boolean perCourse() { return false; }

    @Override
    public JSONArray fetch(ChaoxingClient client, JSONObject course) throws ApiException {
        String referer = resolveReferer(client);
        String body = client.postForm(LIST_URL, LIST_BODY, referer);
        JSONObject root;
        try {
            root = new JSONObject(body);
        } catch (Exception e) {
            throw new ApiException(Protocol.PARSE_ERROR, "收件箱响应不是 JSON（疑似改版），需校准解析器", false);
        }
        JSONObject notices = root.optJSONObject("notices");
        JSONArray list = notices == null ? null : notices.optJSONArray("list");
        if (list == null) {
            throw new ApiException(Protocol.PARSE_ERROR, "收件箱 JSON 缺少 notices.list（疑似改版）", false);
        }

        JSONArray out = new JSONArray();
        for (int i = 0; i < list.length(); i++) {
            JSONObject n = list.getJSONObject(i);
            String title = n.optString("content", "").replaceAll("[\\r\\n]+", "；")
                    .replaceAll("\\s+", " ").trim();
            if (title.isBlank()) continue;

            String sender = n.optString("createrName", "");
            String status = n.optInt("isread", 1) == 1 ? "已读" : "未读";
            long its = n.optLong("insertTime", 0);
            Long published = its > 0 ? its : null;

            // 兜底顺序：详情页 → 附件 URL → 收件箱列表页（保证右键「详情」始终有可打开地址）
            String link = LIST_REFERER, courseId = "";
            String att = n.optString("attachment", "");
            if (!att.isBlank()) {
                try {
                    JSONArray arr = new JSONArray(att);
                    JSONObject first = arr.isEmpty() ? null : arr.optJSONObject(0);
                    JSONObject web = first == null ? null : first.optJSONObject("att_web");
                    if (web != null) {
                        String attUrl = web.optString("url", "");
                        if (!attUrl.isBlank()) link = attUrl;
                        if (web.has("courseId")) courseId = String.valueOf(web.opt("courseId"));
                    }
                } catch (Exception ignored) { /* 附件结构异常不阻断 */ }
            }

            // 收件箱详情页（2026-10-22 实测）：/pc/notice/<uuid>/detail?sendTag=<sendTag>
            // uuid 含 "$" 属合法路径字符；有 uuid 时优先于附件链接，右键「详情」打开消息原文。
            String uuid = n.optString("uuid", "");
            if (!uuid.isBlank())
                link = "https://notice.chaoxing.com/pc/notice/" + uuid + "/detail?sendTag=" + n.optInt("sendTag", 0);

            out.put(Models.item("notice", courseId, sender, title, status, null, published, link, null));
        }
        return out;
    }

    /**
     * 从 i.chaoxing.com/base 的 iframe src 提取 myNotice?s=，并访问入口页建立上下文。
     * 解析失败一律回落到固定 Referer，不让收件箱因入口页改版整体失败。
     */
    private synchronized String resolveReferer(ChaoxingClient client) {
        if (refererResolved) return listReferer;
        try {
            String base = client.get(BASE_URL, "https://i.chaoxing.com/");
            Document doc = Jsoup.parse(base, BASE_URL);
            Element frame = doc.selectFirst("iframe[src*=myNotice]");
            if (frame == null) frame = doc.selectFirst("iframe[src*=notice]");
            if (frame == null) return listReferer;   // 未识别到入口：保留默认，下次刷新再试

            String src = frame.absUrl("src");
            if (src.isBlank()) src = frame.attr("src");
            if (src.startsWith("//")) src = "https:" + src;
            else if (src.startsWith("/")) src = "https://notice.chaoxing.com" + src;
            else if (!src.startsWith("http")) src = URI.create(LIST_REFERER).resolve(src).toString();
            if (!src.contains("myNotice")) return listReferer;

            try { client.get(src, BASE_URL); } catch (ApiException ignored) { /* 入口页失败仍可尝试列表接口 */ }
            listReferer = src;
            refererResolved = true;
        } catch (Exception ignored) { /* 保留默认 Referer，下次刷新再试 */ }
        return listReferer;
    }
}
