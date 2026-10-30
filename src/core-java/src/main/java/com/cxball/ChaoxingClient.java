package com.cxball;

import java.net.CookieManager;
import java.net.CookiePolicy;
import java.net.CookieStore;
import java.net.HttpCookie;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.Random;

import org.json.JSONArray;
import org.json.JSONObject;

import com.cxball.Protocol.ApiException;

/**
 * 学习通 HTTP 客户端（只读）。
 * 约束（附录 A/B）：请求串行、间隔 300–500ms、禁并发；Cookie 只在内存，禁落盘禁日志；
 * 检测"温馨提示/登录页"判定会话失效。
 */
public class ChaoxingClient {

    private final HttpClient http;
    private final HttpClient httpNoFollow;   // stucoursemiddle 提取 302 Location 用（跟随重定向会落到登录页）
    private final CookieStore cookieStore;
    private final Random rnd = new Random();
    private long lastRequestAt = 0L;
    private String ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    public ChaoxingClient() {
        CookieManager cm = new CookieManager(null, CookiePolicy.ACCEPT_ALL);
        this.cookieStore = cm.getCookieStore();
        this.http = HttpClient.newBuilder()
                .cookieHandler(cm)
                .connectTimeout(Duration.ofSeconds(15))
                // 必须用 ALWAYS：i.chaoxing.com/base 的跳转链含 https→http 降级跳（?ws=1 → ?t=），
                // NORMAL 会拒跟降级跳导致拿到 302 空页（MetaCollector 曾因此解析为空 meta）。
                .followRedirects(HttpClient.Redirect.ALWAYS)
                .build();
        this.httpNoFollow = HttpClient.newBuilder()
                .cookieHandler(cm)
                .connectTimeout(Duration.ofSeconds(15))
                .followRedirects(HttpClient.Redirect.NEVER)
                .build();
    }

    /** auth.setCookies：把 C# 抽取的全域 Cookie 注入内存 CookieStore（不落盘） */
    public synchronized void setCookies(JSONArray cookies, String ua) {
        if (ua != null && !ua.isBlank()) this.ua = ua;
        for (int i = 0; i < cookies.length(); i++) {
            JSONObject c = cookies.getJSONObject(i);
            HttpCookie hc = new HttpCookie(c.getString("name"), c.getString("value"));
            String domain = c.optString("domain", ".chaoxing.com");
            hc.setDomain(domain.startsWith(".") ? domain : "." + domain);
            hc.setPath(c.optString("path", "/"));
            hc.setVersion(0);
            try {
                cookieStore.add(URI.create("https://" + domain.replaceFirst("^\\.", "")), hc);
            } catch (Exception ignored) { /* 单条坏 Cookie 不阻断 */ }
        }
    }

    public boolean hasCookies() {
        return !cookieStore.getCookies().isEmpty();
    }

    /** GET，referer 可为 null */
    public synchronized String get(String url, String referer) throws ApiException {
        HttpRequest.Builder b = HttpRequest.newBuilder(URI.create(url))
                .timeout(Duration.ofSeconds(20))
                .header("User-Agent", ua)
                .GET();
        if (referer != null) b.header("Referer", referer);
        return execute(b.build());
    }

    /** POST application/x-www-form-urlencoded */
    public synchronized String postForm(String url, String formBody, String referer) throws ApiException {
        HttpRequest.Builder b = HttpRequest.newBuilder(URI.create(url))
                .timeout(Duration.ofSeconds(20))
                .header("User-Agent", ua)
                .header("Content-Type", "application/x-www-form-urlencoded")
                .POST(HttpRequest.BodyPublishers.ofString(formBody));
        if (referer != null) b.header("Referer", referer);
        return execute(b.build());
    }

    /**
     * POST（AJAX 形态）：额外带 X-Requested-With: XMLHttpRequest。
     * 用途：groupweb 主题讨论列表接口缺少该头（或缺少 tags body）会返回「你没有权限操作」。
     */
    public synchronized String postAjax(String url, String formBody, String referer) throws ApiException {
        HttpRequest.Builder b = HttpRequest.newBuilder(URI.create(url))
                .timeout(Duration.ofSeconds(20))
                .header("User-Agent", ua)
                .header("Content-Type", "application/x-www-form-urlencoded")
                .header("X-Requested-With", "XMLHttpRequest")
                .POST(HttpRequest.BodyPublishers.ofString(formBody));
        if (referer != null) b.header("Referer", referer);
        return execute(b.build());
    }

    /**
     * 不跟随重定向的 GET：返回 3xx 的 Location 头，无重定向返回 null。
     * 用途：stucoursemiddle 的 302 Location 内嵌 stuenc/cpi；若跟随会落到登录页（实测 pageHeader=-1 被拒）。
     */
    public synchronized String redirectLocation(String url, String referer) throws ApiException {
        HttpRequest.Builder b = HttpRequest.newBuilder(URI.create(url))
                .timeout(Duration.ofSeconds(20))
                .header("User-Agent", ua)
                .GET();
        if (referer != null) b.header("Referer", referer);
        throttle();
        final HttpResponse<String> resp;
        try {
            resp = httpNoFollow.send(b.build(), HttpResponse.BodyHandlers.ofString());
        } catch (Exception e) {
            throw new ApiException(Protocol.NETWORK_ERROR, "网络请求失败: " + e.getMessage(), true);
        }
        int sc = resp.statusCode();
        if (sc >= 300 && sc < 400) {
            return resp.headers().firstValue("Location").orElse(null);
        }
        String body = resp.body() == null ? "" : resp.body();
        if (body.contains("<title>用户登录</title>")) {
            throw new ApiException(Protocol.SESSION_EXPIRED, "会话失效（跳转登录页）", false);
        }
        return null;
    }

    private String execute(HttpRequest req) throws ApiException {
        throttle();
        final HttpResponse<String> resp;
        try {
            resp = http.send(req, HttpResponse.BodyHandlers.ofString());
        } catch (Exception e) {
            throw new ApiException(Protocol.NETWORK_ERROR, "网络请求失败: " + e.getMessage(), true);
        }
        int sc = resp.statusCode();
        String body = resp.body() == null ? "" : resp.body();
        if (sc == 403 || body.contains("<title>用户登录</title>") || body.contains("长时间没有操作") || body.contains("没有此页面访问权限")) {
            throw new ApiException(Protocol.SESSION_EXPIRED, "会话失效或无权限（HTTP " + sc + "）", false);
        }
        if (body.contains("滑块") || body.contains("安全验证")) {
            throw new ApiException(Protocol.RATE_LIMITED, "触发平台风控验证", false);
        }
        if (sc >= 400) {
            throw new ApiException(Protocol.NETWORK_ERROR, "HTTP " + sc, true);
        }
        return body;
    }

    /** 串行节流：距上次请求不足 300ms 则补足到 300–500ms 随机 */
    private void throttle() {
        long now = System.currentTimeMillis();
        long gap = now - lastRequestAt;
        long min = 300;
        if (gap < min) {
            long sleep = min + rnd.nextInt(201) - gap;
            try { Thread.sleep(Math.max(sleep, 50)); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
        }
        lastRequestAt = System.currentTimeMillis();
    }
}
