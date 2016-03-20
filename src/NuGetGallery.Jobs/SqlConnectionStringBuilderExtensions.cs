using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace NuGetGallery.Jobs
{
    public static class SqlConnectionStringBuilderExtensions
    {
        public static SqlConnection ConnectTo(this SqlConnectionStringBuilder self)
        {
            var c = new SqlConnection(self.ConnectionString);
            c.Open();
            return c;
        }
    }
}
