using System;

using Microsoft.AspNet.Builder;
using Microsoft.AspNet.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Hangfire;
using Hangfire.SqlServer;

namespace NuGetGallery.Jobs
{
    public class Startup
    {
        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit http://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            app.UseIISPlatformHandler();
            app.UseStaticFiles();

            SqlServerStorage sjs = new SqlServerStorage("Server=(localdb)\\mssqllocaldb;Database=TestDatabase;Trusted_Connection=True;MultipleActiveResultSets=true");
            JobStorage.Current = sjs;
            BackgroundJobServerOptions backgroundJobServerOptions = new BackgroundJobServerOptions()
            {
                Queues = new[] { "critical", "normal", "low" }
            };
            var dashboardOptions = new DashboardOptions
                                       {
                                           AuthorizationFilters =
                                               new[] { new HangfireAuthorizationFilter() }
                                       };

            app.UseHangfireDashboard("/hf", dashboardOptions, sjs);
            app.UseHangfireServer(backgroundJobServerOptions, sjs);

            app.UseMvc();
        }

        // Entry point for the application.
        public static void Main(string[] args) => WebApplication.Run<Startup>(args);
    }
}
