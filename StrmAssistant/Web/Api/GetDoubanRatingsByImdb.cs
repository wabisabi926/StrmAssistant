using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/Douban/RatingsByImdb", "GET", IsHidden = true)]
    [Authenticated]
    public class GetDoubanRatingsByImdb
    {
        [ApiMember(Name = "Ids", Description = "Comma separated IMDb ids (tt...)", IsRequired = true,
            DataType = "string", ParameterType = "query")]
        public string Ids { get; set; }
    }
}
