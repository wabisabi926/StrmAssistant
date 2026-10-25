using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// 只读预览：列出「删除整季该版本」将会删除哪些文件、以及哪些候选因版本身份不明被跳过。
    /// 不执行任何删除，用于删除前核对。
    /// </summary>
    [Route("/Items/{Id}/DeleteVersionPreview", "GET")]
    [Authenticated]
    public class DeleteVersionPreview : IReturn<object>
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }

        [ApiMember(Name = "DeleteParent", Description = "Preview whole-season version deletion",
            IsRequired = false, DataType = "bool", ParameterType = "query")]
        public bool DeleteParent { get; set; }
    }
}
