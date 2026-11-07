using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// 只读探针：返回某个条目在服务端看到的 MediaSources 名字与关键属性。
    /// 用途：验证「多版本美化服务端化」的改写点——客户端（Kodi / Apple TV）读的就是这里的 Name。
    /// 不做任何写入，可安全地反复调用。
    /// </summary>
    [Route("/StrmAssistant/MediaSources/Probe", "GET", IsHidden = true)]
    [Authenticated]
    public class ProbeMediaSources
    {
        [ApiMember(Name = "Id", Description = "Item internal id", IsRequired = true,
            DataType = "string", ParameterType = "query")]
        public string Id { get; set; }
    }
}
