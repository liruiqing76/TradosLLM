namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 一条术语命中（线上术语接口 /match 与 /search 的返回项）。
    /// 字段名与线上契约一致，HTTP 客户端反序列化用。
    /// </summary>
    public class TermHit
    {
        public int id;
        public string source;
        public string target;
        public int score;
    }

    /// <summary>POST {termBaseUrl}/match 的请求体。</summary>
    public class TermMatchRequest
    {
        public string src;
        public string tgt;
        public string text;
        public int max;
    }

    /// <summary>术语接口统一响应容器（matches 字段多端点共用）。</summary>
    public class TermServiceResponse
    {
        public TermHit[] matches;
    }
}