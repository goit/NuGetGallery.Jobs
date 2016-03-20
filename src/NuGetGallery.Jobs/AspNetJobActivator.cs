using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Hangfire;

namespace NuGetGallery.Jobs
{
    public class AspNetJobActivator : JobActivator
    {
        private readonly IServiceProvider provider;

        public AspNetJobActivator(IServiceProvider provider)
        {
            this.provider = provider;
        }

        public override object ActivateJob(Type jobType)
        {
            return provider.GetService(jobType) ?? Activator.CreateInstance(jobType);
        }
    }
}
