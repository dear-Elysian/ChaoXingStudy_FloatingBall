package com.cxball;

import com.cxball.Protocol.ApiException;
import org.json.JSONObject;
import org.jsoup.Jsoup;
import org.jsoup.nodes.Document;
import org.jsoup.nodes.Element;

/**
 * 账号元数据采集（方案 §3.1）：登录成功后抓 i.chaoxing.com/base 个人空间页，
 * 解析昵称（.user-block p.user-name）、头像（.user-block img.head-img）、学校（#siteName，去“图书馆”后缀）。
 * 字段缺失时对应 key 不输出，调用方按“尽力而为”处理。
 */
public class MetaCollector {

    private static final String BASE_URL = "https://i.chaoxing.com/base";
    private static final String BASE_REFERER = "https://i.chaoxing.com/";

    public JSONObject fetchMeta(ChaoxingClient client) throws ApiException {
        String html = client.get(BASE_URL, BASE_REFERER);
        Document doc = Jsoup.parse(html);
        JSONObject meta = new JSONObject();

        Element name = doc.selectFirst(".user-block p.user-name");
        if (name != null && !name.text().isBlank())
            meta.put("nickname", name.text().trim());

        Element avatar = doc.selectFirst(".user-block img.head-img");
        if (avatar != null) {
            String src = avatar.attr("src").trim();
            if (!src.isEmpty()) meta.put("avatar", src);
        }

        Element site = doc.selectFirst("#siteName");
        if (site != null) {
            String school = site.hasAttr("title") ? site.attr("title").trim() : site.text().trim();
            if (school.endsWith("图书馆"))
                school = school.substring(0, school.length() - "图书馆".length());
            if (!school.isEmpty()) meta.put("school", school);
        }
        return meta;
    }
}
