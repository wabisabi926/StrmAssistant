using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System.IO;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/Config/Import", "POST", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class PostConfigImport : IRequiresRequestStream
    {
        public Stream RequestStream { get; set; }
    }
}
