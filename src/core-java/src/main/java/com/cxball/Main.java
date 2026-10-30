package com.cxball;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;

import org.json.JSONArray;
import org.json.JSONObject;

import com.cxball.Protocol.ApiException;

/**
 * Java 核心入口：stdio JSON-Lines 协议循环（附录 B）。
 * 用法：java -jar core.jar --stdio
 * 消息：sys.handshake/ping/shutdown、auth.setCookies、session.validate、course.list、
 *       fetch.start/fetch.cancel（+ fetch.progress 事件）
 *
 * 安全约束：Cookie 只存内存，禁落盘、禁写日志（stdout 只出协议行，stderr 出日志）。
 */
public class Main {

    private static final String CORE_VERSION = "core/0.1.0";
    private static final int PROTOCOL = 1;

    private final ChaoxingClient client = new ChaoxingClient();
    private final CourseCollector courses = new CourseCollector();
    private final NoticeCollector noticeCollector = new NoticeCollector();
    /** 采集器注册表（§5.2）：新增数据类型 = 实现 Collector + 在此注册，不动主流程。 */
    private final java.util.List<Collector> collectors = java.util.List.of(
            new AssignmentCollector(),
            new PracticeCollector(),
            new ExamCollector(),
            new DiscussionCollector(),
            noticeCollector);
    private final MetaCollector metaCollector = new MetaCollector();
    private final PrintWriter out;
    private final java.util.concurrent.atomic.AtomicBoolean fetchRunning = new java.util.concurrent.atomic.AtomicBoolean(false);
    private volatile boolean cancelFetch = false;

    public static void main(String[] args) throws Exception {
        new Main(new PrintWriter(new java.io.OutputStreamWriter(System.out, StandardCharsets.UTF_8), true)).run();
    }

    Main(PrintWriter out) { this.out = out; }

    private void run() throws Exception {
        BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        String line;
        while ((line = in.readLine()) != null) {
            line = line.trim();
            if (line.isEmpty()) continue;
            String id = null, type = null;
            try {
                JSONObject req = Protocol.parseRequest(line);
                id = req.getString("id");
                type = req.getString("type");
                JSONObject payload = req.optJSONObject("payload");
                if (payload == null) payload = new JSONObject();
                dispatch(id, type, payload);
                if ("sys.shutdown".equals(type)) break;
            } catch (ApiException e) {
                respond(Protocol.err(id, type == null ? "unknown" : type, e.code, e.getMessage(), e.retryable));
            } catch (Exception e) {
                respond(Protocol.err(id, type == null ? "unknown" : type,
                        Protocol.INTERNAL, "内部错误: " + e.getClass().getSimpleName() + " " + e.getMessage(), true));
            }
        }
    }

    private void dispatch(String id, String type, JSONObject payload) throws Exception {
        switch (type) {
            case "sys.handshake" -> {
                JSONObject p = new JSONObject();
                p.put("protocol", PROTOCOL);
                p.put("core", CORE_VERSION);
                respond(Protocol.ok(id, type, p));
            }
            case "sys.ping" -> respond(Protocol.ok(id, type, new JSONObject()));

            case "auth.setCookies" -> {
                client.setCookies(payload.getJSONArray("cookies"), payload.optString("ua", null));
                noticeCollector.resetReferer();   // 新会话/新账号的 myNotice?s= 可能不同
                respond(Protocol.ok(id, type, new JSONObject()));
            }

            case "session.validate" -> {
                // 轻量实测：拉课程列表，成功即会话有效
                JSONObject p = new JSONObject();
                try {
                    courses.fetchCourses(client);
                    p.put("valid", true);
                } catch (ApiException e) {
                    if (Protocol.SESSION_EXPIRED.equals(e.code)) p.put("valid", false);
                    else throw e;
                }
                respond(Protocol.ok(id, type, p));
            }

            case "course.list" -> {
                JSONObject p = new JSONObject();
                p.put("courses", courses.fetchCourses(client));
                respond(Protocol.ok(id, type, p));
            }

            case "user.meta" -> {
                // 账号元数据（§3.1）：昵称/头像/学校，尽力而为，字段可缺
                JSONObject p = new JSONObject();
                p.put("meta", metaCollector.fetchMeta(client));
                respond(Protocol.ok(id, type, p));
            }

            // fetch.start 放到工作线程执行：主循环继续读 stdin，fetch.cancel 才能立即生效（附录 B.3）
            case "fetch.start" -> startFetch(id, payload);

            case "fetch.cancel" -> {
                cancelFetch = true;
                respond(Protocol.ok(id, type, new JSONObject()));
            }

            case "sys.shutdown" -> respond(Protocol.ok(id, type, new JSONObject()));

            default -> respond(Protocol.err(id, type, Protocol.UNKNOWN_TYPE, "未知消息类型: " + type, false));
        }
    }

    private void startFetch(String id, JSONObject payload) {
        if (!fetchRunning.compareAndSet(false, true)) {
            respond(Protocol.err(id, "fetch.start", Protocol.RATE_LIMITED, "已有刷新在进行", true));
            return;
        }
        cancelFetch = false;
        Thread worker = new Thread(() -> {
            try {
                doFetch(id, payload);
            } catch (ApiException e) {
                respond(Protocol.err(id, "fetch.start", e.code, e.getMessage(), e.retryable));
            } catch (Exception e) {
                respond(Protocol.err(id, "fetch.start", Protocol.INTERNAL,
                        "内部错误: " + e.getClass().getSimpleName() + " " + e.getMessage(), true));
            } finally {
                fetchRunning.set(false);
            }
        }, "fetch-worker");
        worker.setDaemon(true);
        worker.start();
    }

    /** 一次完整刷新：课程 →（按注册表逐类抓取）→ 结果；每阶段发 fetch.progress */
    private void doFetch(String id, JSONObject payload) throws Exception {
        JSONArray modules = payload.optJSONArray("modules");
        java.util.Set<String> enabled = new java.util.HashSet<>();
        if (modules != null) for (int i = 0; i < modules.length(); i++) enabled.add(modules.optString(i));

        // courseIds 缺省 = 不过滤（全部课程）；显式空数组 = 一门课程也不抓（收件箱仍会刷新）
        boolean filterCourses = payload.has("courseIds") && !payload.isNull("courseIds");
        java.util.Set<String> selected = new java.util.HashSet<>();
        JSONArray ids = payload.optJSONArray("courseIds");
        if (ids != null) for (int i = 0; i < ids.length(); i++) selected.add(String.valueOf(ids.get(i)));

        JSONObject result = new JSONObject();
        JSONArray failures = new JSONArray();
        JSONArray items = new JSONArray();
        result.put("fetchedAt", System.currentTimeMillis());

        // 阶段一：课程列表（基础设施，不属于任何数据类型采集器）
        // 此阶段还不知道本次要抓多少门课，overall 进度传 0 表示本事件不带总进度。
        progress(id, "course", null, 0, 1, "课程列表", 0, 0);
        JSONArray courseList;
        try {
            courseList = courses.fetchCourses(client);
        } catch (ApiException e) {
            // 会话失效属整体失败
            if (Protocol.SESSION_EXPIRED.equals(e.code) || Protocol.RATE_LIMITED.equals(e.code)) throw e;
            courseList = new JSONArray();
            failures.put(failure(null, "course", e));
        }
        result.put("courses", courseList);
        checkCancel();

        // 阶段二：逐门课程抓取（课程优先）：一门课的所有启用采集器都跑完后才算完成，
        // 因此进度按课程只展示一次，不会出现「作业第 3 门」后「随堂练习第 3 门」的重复。
        java.util.List<Collector> courseCols = new java.util.ArrayList<>();
        java.util.List<Collector> globalCols = new java.util.ArrayList<>();
        for (Collector col : collectors) {
            if (!enabled.isEmpty() && !enabled.contains(col.type())) continue;
            if (col.perCourse()) courseCols.add(col); else globalCols.add(col);
        }

        java.util.List<JSONObject> targets = new java.util.ArrayList<>();
        if (!courseCols.isEmpty()) {
            for (int i = 0; i < courseList.length(); i++) {
                JSONObject c = courseList.getJSONObject(i);
                if (!filterCourses || selected.contains(c.getString("courseId"))) targets.add(c);
            }
        }

        int overallTotal = targets.size() + globalCols.size();   // 每门课 1 步 + 每个全局采集器 1 步
        int overallDone = 0;
        // 同一门课程可能被多个 perCourse 采集器使用：secrets 只补全一次，避免重复请求中间页/课程页。
        java.util.Set<String> enriched = new java.util.HashSet<>();
        for (int i = 0; i < targets.size(); i++) {
            checkCancel();
            JSONObject c = targets.get(i);
            String cid = c.getString("courseId");
            String name = c.optString("courseName", cid);
            progress(id, "course", cid, i, targets.size(), name, overallDone, overallTotal);
            for (Collector col : courseCols) {
                checkCancel();
                try {
                    if (!enriched.contains(cid)) {
                        courses.enrichSecrets(client, c);
                        enriched.add(cid);
                    }
                    JSONArray arr = col.fetch(client, c);
                    for (int j = 0; j < arr.length(); j++) items.put(arr.get(j));
                } catch (ApiException e) {
                    if (Protocol.SESSION_EXPIRED.equals(e.code) || Protocol.RATE_LIMITED.equals(e.code)) throw e;
                    failures.put(failure(cid, col.type(), e));
                }
            }
            overallDone++;
        }

        for (Collector col : globalCols) {
            checkCancel();
            progress(id, col.type(), null, 0, 1, col.label(), overallDone, overallTotal);
            try {
                JSONArray arr = col.fetch(client, null);
                for (int j = 0; j < arr.length(); j++) items.put(arr.get(j));
            } catch (ApiException e) {
                if (Protocol.SESSION_EXPIRED.equals(e.code) || Protocol.RATE_LIMITED.equals(e.code)) throw e;
                failures.put(failure(null, col.type(), e));
            }
            overallDone++;
        }

        result.put("items", items);
        result.put("failures", failures);
        respond(Protocol.ok(id, "fetch.start", result));
    }

    private void checkCancel() throws ApiException {
        if (cancelFetch) throw new ApiException(Protocol.CANCELLED, "刷新已被用户取消", false);
    }

    private void progress(String forId, String stage, String courseId, int done, int total,
                          String currentTitle, int overallDone, int overallTotal) {
        JSONObject p = new JSONObject();
        p.put("stage", stage);
        if (courseId != null) p.put("courseId", courseId);
        p.put("done", done);
        p.put("total", total);
        p.put("currentTitle", currentTitle == null ? "" : currentTitle);
        if (overallTotal > 0) {
            // 全程进度（跨采集器不归零）；overallTotal=0 表示本事件不带全程进度（课程列表阶段）
            p.put("overallDone", overallDone);
            p.put("overallTotal", overallTotal);
        }
        respond(Protocol.event("fetch.progress", forId, p));
    }

    private static JSONObject failure(String courseId, String stage, ApiException e) {
        JSONObject f = new JSONObject();
        if (courseId != null) f.put("courseId", courseId);
        f.put("stage", stage);
        f.put("code", e.code);
        f.put("message", e.getMessage());
        return f;
    }

    private synchronized void respond(String line) {
        out.println(line);
    }
}
