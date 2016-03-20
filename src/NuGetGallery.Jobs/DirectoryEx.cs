using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NuGetGallery.Jobs
{
    public class DirectoryEx
    {
        public static void EnsureExists(string path)
        {
            if (String.IsNullOrEmpty(path))
            {
                return;
            }
            
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public static void TryDelete(string path)
        {
            if (String.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path);
                }
            }
            catch
            {
                // ignored
            }
        }
    }
}
