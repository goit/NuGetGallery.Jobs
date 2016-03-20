using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NuGetGallery.Jobs
{
    public class Constants
    {
        public const string NuGetPackageFileExtension = ".nupkg";
        public const string PackagesFolderName = "packages";
        public const string PackagesTempFolderName = "packages-temp";
        public const string PackageBackupsFolderName = "package-backups";

        public const string Sha512HashAlgorithmId = "SHA512";
    }
}
