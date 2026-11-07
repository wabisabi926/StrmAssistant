using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/Config/Export", "GET", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetConfigExport
    {
    }
}
