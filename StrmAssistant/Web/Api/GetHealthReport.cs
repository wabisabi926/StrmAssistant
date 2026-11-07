using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/Health", "GET", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetHealthReport
    {
    }
}
