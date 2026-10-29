using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/Douban/RefreshRating", "POST", IsHidden = true)]
    [Authenticated]
    public class RefreshDoubanRating
    {
        [ApiMember(Name = "Id", Description = "Item id", IsRequired = true, DataType = "long",
            ParameterType = "query")]
        public long Id { get; set; }
    }
}
