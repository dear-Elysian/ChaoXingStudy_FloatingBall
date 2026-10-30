package com.cxball;

import org.json.JSONObject;

/**
 * 附录 B 协议信封：请求 {id,type,ts,payload} / 响应 {id,type.ok, payload|error} / 事件 {type,for,ts,payload}
 * 错误码见文档附录 B.2。
 */
public final class Protocol {

    public static final String SESSION_EXPIRED = "SESSION_EXPIRED";
    public static final String NETWORK_ERROR   = "NETWORK_ERROR";
    public static final String RATE_LIMITED    = "RATE_LIMITED";
    public static final String PARSE_ERROR     = "PARSE_ERROR";
    public static final String CANCELLED       = "CANCELLED";
    public static final String UNKNOWN_TYPE    = "UNKNOWN_TYPE";
    public static final String INTERNAL        = "INTERNAL";

    private Protocol() {}

    /** 业务异常：携带协议错误码 */
    public static class ApiException extends Exception {
        public final String code;
        public final boolean retryable;
        public ApiException(String code, String message, boolean retryable) {
            super(message);
            this.code = code;
            this.retryable = retryable;
        }
    }

    public static JSONObject parseRequest(String line) throws ApiException {
        try {
            JSONObject o = new JSONObject(line);
            if (!o.has("id") || !o.has("type")) throw new Exception("missing id/type");
            return o;
        } catch (Exception e) {
            throw new ApiException(INTERNAL, "请求行不是合法协议 JSON: " + e.getMessage(), false);
        }
    }

    public static String ok(String id, String type, JSONObject payload) {
        JSONObject o = new JSONObject();
        o.put("id", id);
        o.put("type", type + ".result");
        o.put("ok", true);
        o.put("payload", payload == null ? new JSONObject() : payload);
        return o.toString();
    }

    public static String err(String id, String type, String code, String message, boolean retryable) {
        JSONObject o = new JSONObject();
        o.put("id", id);
        o.put("type", type + ".result");
        o.put("ok", false);
        JSONObject e = new JSONObject();
        e.put("code", code);
        e.put("message", message == null ? "" : message);
        e.put("retryable", retryable);
        o.put("error", e);
        return o.toString();
    }

    public static String event(String type, String forId, JSONObject payload) {
        JSONObject o = new JSONObject();
        o.put("type", type);
        if (forId != null) o.put("for", forId);
        o.put("ts", System.currentTimeMillis());
        o.put("payload", payload == null ? new JSONObject() : payload);
        return o.toString();
    }
}
