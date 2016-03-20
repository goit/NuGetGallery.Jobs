using System;
using System.Collections.Generic;

using Hangfire.Dashboard;

namespace NuGetGallery.Jobs
{
    public class HangfireAuthorizationFilter : IAuthorizationFilter
    {
        public bool Authorize(IDictionary<string, object> owinEnvironment)
        {
            return true;
        }
    }
}
