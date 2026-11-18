package com.cxball;

import org.json.JSONArray;
import org.json.JSONObject;
import com.cxball.Protocol.ApiException;

/** 离线回归入口：使用模拟客户端，不连接学习通，不依赖测试框架。 */
public final class ActiveTaskSupportRegression {
    static final class FakeClient extends ChaoxingClient {
        String response = "";
        String uid = "123";
        String error;
        boolean failDetail;
        int submitRequests;
        @Override public String cookieValue(String name) { return uid; }
        @Override public synchronized String get(String url, String referer) throws ApiException {
            if (url.contains("activelist")) return """
                {"data":{"activeList":[{"activeType":35,"isnorm":0,"id":1,
                "nameOne":"测试任务","endTime":1}]}}
                """;
            if (url.contains("getMyGroupInfo")) submitRequests++;
            if (error != null && (url.contains("getMyGroupInfo") || failDetail))
                throw new ApiException(error, "模拟错误", true);
            if (url.contains("getActiveInfoForMe")) return "{}";
            return response;
        }
    }

    static void check(boolean value, String label) {
        if (!value) throw new AssertionError(label);
    }

    static String response(Object value) {
        return new JSONObject().put("result", 1).put("data", new JSONObject()
            .put("taskGroupInfo", new JSONObject().put("submitTime", value))).toString();
    }

    static JSONObject collect(FakeClient client) throws ApiException {
        JSONArray items = new PersonalTaskCollector().fetch(client, new JSONObject()
            .put("courseId", "1").put("classId", "2").put("courseName", "测试").put("fid", "3"));
        return items.getJSONObject(0);
    }

    public static void main(String[] args) throws Exception {
        FakeClient client = new FakeClient();
        client.response = response("2026-11-18 15:10");
        JSONObject item = collect(client);
        check("已完成".equals(item.getString("status")), "截止已过但有提交时间仍为已完成");
        check(item.has("submitTime") && !item.has("submissionQueryFailed"), "完成状态字段");
        for (Object empty : new Object[]{"", JSONObject.NULL}) {
            client.response = response(empty);
            check("未完成".equals(collect(client).getString("status")), "明确空提交时间为未完成");
        }
        for (String body : new String[]{"invalid JSON", "{}", "{\"result\":0}",
                "{\"result\":1,\"data\":{}}", "{\"result\":1,\"data\":{\"taskGroupInfo\":{}}}", response(123)}) {
            client.response = body;
            item = collect(client);
            check("提交状态未知".equals(item.getString("status")), "异常响应不误判未完成: " + body);
            check(item.getBoolean("submissionQueryFailed") && !item.has("submitTime"), "未知状态带失败标记");
        }
        client.uid = "";
        int before = client.submitRequests;
        check(collect(client).getBoolean("submissionQueryFailed"), "缺少 uid 为未知");
        check(before == client.submitRequests, "缺少 uid 不请求提交状态");
        client.uid = "123";
        client.error = Protocol.NETWORK_ERROR;
        check(collect(client).getBoolean("submissionQueryFailed"), "普通网络失败单条降级");
        for (String code : new String[]{Protocol.SESSION_EXPIRED, Protocol.RATE_LIMITED}) {
            client.error = code;
            for (boolean detail : new boolean[]{false, true}) {
                client.failDetail = detail;
                before = client.submitRequests;
                try {
                    collect(client);
                    throw new AssertionError("必须上抛 " + code);
                } catch (ApiException e) {
                    check(code.equals(e.code), "保留错误码");
                    if (detail) check(before == client.submitRequests, "详情发生致命错误后不再查提交时间");
                }
            }
        }
        client.error = Protocol.NETWORK_ERROR;
        String fallback = ActiveTaskSupport.detailLink(client, "1", "2", "3", 1, false, "test");
        check(fallback.contains("notGroup/taskDetailStuAndAnsDetailNotGroup_new"), "普通详情失败保留非分组查看页");
        System.out.println("PASS: personal task state and fatal-error regressions");
    }
}
