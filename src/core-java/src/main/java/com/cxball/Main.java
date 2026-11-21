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
    private final BrushPlanner brushPlanner = new BrushPlanner();
    private final NoticeCollector noticeCollector = new NoticeCollector();
    private final DiscussionCollector discussionCollector = new DiscussionCollector();
    /** 采集器注册表（§5.2）：新增数据类型 = 实现 Collector + 在此注册，不动主流程。 */
    private final java.util.List<Collector> collectors = java.util.List.of(
            new AssignmentCollector(),
            new PersonalTaskCollector(),
            new PracticeCollector(),
            new ExamCollector(),
            discussionCollector,
            new GroupTaskCollector(),
            new BrushCollector(),
            noticeCollector);
    private final MetaCollector metaCollector = new MetaCollector();
    private final PrintWriter out;
    private final java.util.concurrent.atomic.AtomicBoolean fetchRunning = new java.util.concurrent.atomic.AtomicBoolean(false);
    private volatile boolean cancelFetch = false;
    private final java.util.concurrent.atomic.AtomicBoolean brushPlanRunning = new java.util.concurrent.atomic.AtomicBoolean(false);
    private volatile boolean cancelBrushPlan = false;

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

            // 刷课规划（v3.28）：放到工作线程，主循环继续读 stdin，
            // 这样 brush.cancel 能在检查途中立即生效，不会“停不干净”。
            case "brush.plan" -> startBrushPlan(id, payload);

            case "brush.cancel" -> {
                cancelBrushPlan = true;
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

    /** 刷课章节规划：独立线程执行，主循环可继续处理 brush.cancel。 */
    private void startBrushPlan(String id, JSONObject payload) {
        if (!brushPlanRunning.compareAndSet(false, true)) {
            respond(Protocol.err(id, "brush.plan", Protocol.RATE_LIMITED, "正在检查章节，请稍候", true));
            return;
        }
        cancelBrushPlan = false;
        String courseId = payload.optString("courseId", "");
        int parallel = Math.max(1, Math.min(8, payload.optInt("parallel", 1)));
        Thread worker = new Thread(() -> {
            try {
                JSONObject p = new JSONObject();
                p.put("plan", brushPlanner.plan(client, courses, courseId, parallel,
                        () -> cancelBrushPlan));
                if (cancelBrushPlan) throw new ApiException(Protocol.CANCELLED, "章节检查已取消", false);
                respond(Protocol.ok(id, "brush.plan", p));
            } catch (ApiException e) {
                respond(Protocol.err(id, "brush.plan", e.code, e.getMessage(), e.retryable));
            } catch (Exception e) {
                respond(Protocol.err(id, "brush.plan", Protocol.INTERNAL,
                        "内部错误: " + e.getClass().getSimpleName() + " " + e.getMessage(), true));
            } finally {
                brushPlanRunning.set(false);
                cancelBrushPlan = false;
            }
        }, "brush-plan-worker");
        worker.setDaemon(true);
        worker.start();
    }

    /** 一次完整刷新：课程 →（按注册表逐类抓取）→ 结果；每阶段发 fetch.progress */
    private void doFetch(String id, JSONObject payload) throws Exception {
        JSONArray modules = payload.optJSONArray("modules");
        java.util.Set<String> enabled = new java.util.HashSet<>();
        if (modules != null) for (int i = 0; i < modules.length(); i++) enabled.add(modules.optString(i));

        // 主题讨论回复判定：当前账号昵称（C# 从 meta.nickname 传入），只用于精确比对回复人。
        discussionCollector.setUserName(payload.optString("userName", ""));

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
        java.util.concurrent.atomic.AtomicInteger overallDone =
                new java.util.concurrent.atomic.AtomicInteger(0);
        // 并行加载（§7.2.1）：按课程并发；每门课内部仍按采集器顺序串行。
        // parallel=1 时走原来的串行路径，保持旧行为。
        int parallel = Math.max(1, Math.min(8, payload.optInt("parallel", 1)));

        if (parallel <= 1 || targets.size() <= 1) {
            for (int i = 0; i < targets.size(); i++) {
                checkCancel();
                JSONObject c = targets.get(i);
                String cid = c.getString("courseId");
                String name = c.optString("courseName", cid);
                progress(id, "course", cid, i, targets.size(), name, overallDone.get(), overallTotal);
                boolean enriched = false;
                for (Collector col : courseCols) {
                    checkCancel();
                    try {
                        if (!enriched) {
                            courses.enrichSecrets(client, c);
                            enriched = true;
                        }
                        JSONArray arr = col.fetch(client, c);
                        for (int j = 0; j < arr.length(); j++) items.put(arr.get(j));
                    } catch (ApiException e) {
                        if (Protocol.SESSION_EXPIRED.equals(e.code) || Protocol.RATE_LIMITED.equals(e.code)) throw e;
                        failures.put(failure(cid, col.type(), e));
                    }
                }
                overallDone.incrementAndGet();
            }
        } else {
            // 每门课一个任务；工作线程各自持有独立客户端（Cookie 复制、各自节流），
            // 否则 ChaoxingClient 的 synchronized + 300ms 节流会把并发退化成串行。
            java.util.concurrent.ExecutorService pool =
                    java.util.concurrent.Executors.newFixedThreadPool(Math.min(parallel, targets.size()));
            java.util.concurrent.atomic.AtomicReference<ApiException> fatal =
                    new java.util.concurrent.atomic.AtomicReference<>();
            ThreadLocal<ChaoxingClient> workerClient = ThreadLocal.withInitial(client::copyForWorker);
            JSONArray[] courseItems = new JSONArray[targets.size()];
            JSONArray[] courseFailures = new JSONArray[targets.size()];
            java.util.List<java.util.concurrent.Future<?>> futures = new java.util.ArrayList<>();
            try {
                for (int i = 0; i < targets.size(); i++) {
                    final int idx = i;
                    futures.add(pool.submit(() -> {
                        if (cancelFetch || fatal.get() != null) return;
                        JSONObject c = targets.get(idx);
                        String cid = c.getString("courseId");
                        ChaoxingClient wc = workerClient.get();
                        JSONArray myItems = new JSONArray();
                        JSONArray myFailures = new JSONArray();
                        boolean enriched = false;
                        for (Collector col : courseCols) {
                            if (cancelFetch || fatal.get() != null) break;
                            try {
                                if (!enriched) {
                                    courses.enrichSecrets(wc, c);
                                    enriched = true;
                                }
                                JSONArray arr = col.fetch(wc, c);
                                for (int j = 0; j < arr.length(); j++) myItems.put(arr.get(j));
                            } catch (ApiException e) {
                                if (Protocol.SESSION_EXPIRED.equals(e.code) || Protocol.RATE_LIMITED.equals(e.code)) {
                                    fatal.compareAndSet(null, e);
                                    break;
                                }
                                myFailures.put(failure(cid, col.type(), e));
                            }
                        }
                        courseItems[idx] = myItems;
                        courseFailures[idx] = myFailures;
                        int done = overallDone.incrementAndGet();
                        progress(id, "course", cid, idx, targets.size(),
                                c.optString("courseName", cid), done, overallTotal);
                    }));
                }
                pool.shutdown();
                // 等待期间轮询取消标志：fetch.cancel 能让并行批次尽快停下
                while (!pool.awaitTermination(200, java.util.concurrent.TimeUnit.MILLISECONDS)) {
                    if (cancelFetch) {
                        pool.shutdownNow();
                        throw new ApiException(Protocol.CANCELLED, "刷新已被用户取消", false);
                    }
                }
                // 任务里的非 ApiException 异常会被 Future 吞掉，这里补捞一次
                for (java.util.concurrent.Future<?> f : futures) {
                    try {
                        f.get();
                    } catch (java.util.concurrent.ExecutionException ee) {
                        Throwable cause = ee.getCause();
                        if (cause instanceof ApiException ae) throw ae;
                        throw new ApiException(Protocol.PARSE_ERROR,
                                "并行刷新失败: " + (cause == null ? "未知错误" : cause.getMessage()), false);
                    }
                }
            } catch (InterruptedException ie) {
                pool.shutdownNow();
                Thread.currentThread().interrupt();
                throw new ApiException(Protocol.NETWORK_ERROR, "刷新被中断", true);
            } finally {
                pool.shutdownNow();
            }
            ApiException f = fatal.get();
            if (f != null) throw f;
            checkCancel();
            // 按原课程顺序合并，避免并行完成后顺序乱掉
            for (int i = 0; i < targets.size(); i++) {
                if (courseItems[i] != null)
                    for (int j = 0; j < courseItems[i].length(); j++) items.put(courseItems[i].get(j));
                if (courseFailures[i] != null)
                    for (int j = 0; j < courseFailures[i].length(); j++) failures.put(courseFailures[i].get(j));
            }
        }

        for (Collector col : globalCols) {
            checkCancel();
            progress(id, col.type(), null, 0, 1, col.label(), overallDone.get(), overallTotal);
            try {
                JSONArray arr = col.fetch(client, null);
                for (int j = 0; j < arr.length(); j++) items.put(arr.get(j));
            } catch (ApiException e) {
                if (Protocol.SESSION_EXPIRED.equals(e.code) || Protocol.RATE_LIMITED.equals(e.code)) throw e;
                failures.put(failure(null, col.type(), e));
            }
            overallDone.incrementAndGet();
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
