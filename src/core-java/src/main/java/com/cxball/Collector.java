package com.cxball;

import org.json.JSONArray;
import org.json.JSONObject;

import com.cxball.Protocol.ApiException;

/**
 * 采集器接口（§5.2）：每类数据一个实现，注册进 Main 的采集器注册表。
 * 新增类型 = 实现本接口 + 注册，不动主流程。
 *
 * 只读约束（§5.2）：实现类只允许发起只读请求，禁止任何写操作。
 */
public interface Collector {

    /** Item.type，如 assignment / notice / exam（预留）。 */
    String type();

    /** 进度文案里的模块名（全局采集器用，如「收件箱」）。 */
    String label();

    /** true = 逐门已选课程抓取（fetch 收到课程对象）；false = 全局一次抓取（fetch 的 course 为 null）。 */
    boolean perCourse();

    JSONArray fetch(ChaoxingClient client, JSONObject course) throws ApiException;
}
