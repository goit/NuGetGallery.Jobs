using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Dapper;

using Microsoft.Extensions.Configuration;

using NLog;

using NuGet;

using ILogger = NuGet.ILogger;

namespace NuGetGallery.Jobs.PackageEditJob
{
    public class HandlePackageEdits
    {
        private static Logger Log = LogManager.GetCurrentClassLogger();

        private const string HashAlgorithmName = Constants.Sha512HashAlgorithmId;

        public static readonly string GetEditsBaseSql = @"
            SELECT pr.Id, p.NormalizedVersion AS Version, p.Hash, e.*
            FROM PackageEdits e
            INNER JOIN Packages p ON p.[Key] = e.PackageKey
            INNER JOIN PackageRegistrations pr ON pr.[Key] = p.PackageRegistrationKey";

        private static readonly Regex ManifestSelector = new Regex(@"^[^/]*\.nuspec$", RegexOptions.IgnoreCase);

        public HandlePackageEdits(IConfiguration configuration)
        {
            this.Configuration = configuration;

            this.PackageDatabase = new SqlConnectionStringBuilder(this.Configuration["Data:DefaultConnection:ConnectionString"]);
            this.FileStorageDirectory = this.Configuration["Gallery.FileStorageDirectory"];

            this.PackagesPath = Path.Combine(this.FileStorageDirectory, Constants.PackagesFolderName);
            this.PackagesTempPath = Path.Combine(this.FileStorageDirectory, Constants.PackagesTempFolderName, "NuGetGallery.Jobs");
            this.PackagesBackupPath = Path.Combine(this.FileStorageDirectory, Constants.PackageBackupsFolderName);
        }

        public IConfiguration Configuration { get; set; }

        public SqlConnectionStringBuilder PackageDatabase { get; set; }

        public string FileStorageDirectory { get; set; }

        public string PackagesPath { get; set; }

        public string PackagesTempPath { get; set; }

        public string PackagesBackupPath { get; set; }


        public void Run()
        {
            try
            {
                DirectoryEx.EnsureExists(this.PackagesBackupPath);

                IList<PackageEdit> edits;
                using (var connection = this.PackageDatabase.ConnectTo())
                {
                    edits = connection.Query<PackageEdit>(GetEditsBaseSql).ToList();
                }

                Log.Info("Fetched {2} queued edits from {0}/{1}", PackageDatabase.DataSource, PackageDatabase.InitialCatalog, edits.Count);

                // Group by package and take just the most recent edit for each package
                edits = edits.GroupBy(e => e.PackageKey)
                        .Select(g => g.OrderByDescending(e => e.Timestamp).FirstOrDefault())
                        .Where(e => e != null)
                        .ToList();

                foreach (var edit in edits)
                {
                    Exception thrown = null;
                    try
                    {
                        this.ApplyEdit(edit);
                    }
                    catch (Exception ex)
                    {
                        thrown = ex;
                    }

                    if (thrown != null)
                    {
                        using (var connection = this.PackageDatabase.ConnectTo())
                        {
                            connection.Query<int>(@"
                            UPDATE  PackageEdits
                            SET
                                    TriedCount = TriedCount + 1,
                                    LastError = @error
                            WHERE   [Key] = @key", new { error = thrown.ToString(), key = edit.Key });
                        }

                    }
                }

            }
            finally
            {
                DirectoryEx.TryDelete(this.PackagesTempPath);
            }
        }

        private void ApplyEdit(PackageEdit edit)
        {
            // copy the original file
            string packageName = $"{edit.Id}.{edit.Version}.nupkg".ToLower(CultureInfo.InvariantCulture);
            string originalPath = Path.Combine(this.PackagesPath, packageName);
            string backupPath = Path.Combine(this.PackagesBackupPath, packageName);

            var tempDir = Path.Combine(this.PackagesTempPath, "HandlePackageEdits");
            string directory = Path.Combine(tempDir, edit.Id, edit.Version);
            string tempPath = Path.Combine(directory, packageName);

            try
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Log.Info("Downloaded original copy of {0} {1}", edit.Id, edit.Version);
                File.Copy(originalPath, tempPath, true);

                // Load the zip file and find the manifest
                using (var originalStream = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite))
                using (var archive = new ZipArchive(originalStream, ZipArchiveMode.Update))
                {
                    // Find the nuspec
                    var nuspecEntries = archive.Entries.Where(e => ManifestSelector.IsMatch(e.FullName)).ToArray();
                    if (nuspecEntries.Length == 0)
                    {
                        throw new InvalidDataException(
                            string.Format(
                                CultureInfo.CurrentCulture, 
                                "Package has no manifest: {0} {1} (URL: {2})", 
                                edit.Id, 
                                edit.Version, 
                                tempPath));
                    }

                    if (nuspecEntries.Length > 1)
                    {
                        throw new InvalidDataException(
                            string.Format(
                                CultureInfo.CurrentCulture, 
                                "Package has multiple manifests: {0} {1} (URL: {2})", 
                                edit.Id, 
                                edit.Version, 
                                tempPath));
                    }

                    // We now have the nuspec
                    var manifestEntry = nuspecEntries.Single();

                    // Load the manifest with a constrained stream
                    Log.Info("Rewriting package file for {0} {1}", edit.Id, edit.Version);
                    using (var manifestStream = manifestEntry.Open())
                    {
                        var manifest = Manifest.ReadFrom(manifestStream, validateSchema: false);

                        // Modify the manifest as per the edit
                        edit.ApplyTo(manifest.Metadata);

                        // Save the manifest back
                        manifestStream.Seek(0, SeekOrigin.Begin);
                        manifestStream.SetLength(0);
                        manifest.Save(manifestStream);
                    }

                    Log.Info("Rewrote package file for {0} {1}", edit.Id, edit.Version);
                }

                // replace original file and back it up
                Log.Info("Replacing original package file for {0} {1} ({2}, backup location {3}).", edit.Id, edit.Version, originalPath, backupPath);
                File.Replace(tempPath, originalPath, backupPath);

                // Calculate new size and hash
                string hash;
                long size;
                using (var originalStream = File.OpenRead(originalPath))
                {
                    size = originalStream.Length;

                    var hashAlgorithm = HashAlgorithm.Create(HashAlgorithmName);
                    if (hashAlgorithm == null)
                    {
                        throw new InvalidOperationException($"Failed to create instance of hash algorithm {HashAlgorithmName}.");
                    }

                    hash = Convert.ToBase64String(hashAlgorithm.ComputeHash(originalStream));
                }

                // Update the database
                Log.Info("Updating package record for {0} {1}", edit.Id, edit.Version);
                using (var connection = this.PackageDatabase.ConnectTo())
                {
                    var parameters =
                        new DynamicParameters(
                            new
                                {
                                    edit.Authors, 
                                    edit.Copyright, 
                                    edit.Description, 
                                    edit.IconUrl, 
                                    edit.LicenseUrl, 
                                    edit.ProjectUrl, 
                                    edit.ReleaseNotes, 
                                    edit.RequiresLicenseAcceptance, 
                                    edit.Summary, 
                                    edit.Title, 
                                    edit.Tags, 
                                    edit.Key, 
                                    edit.PackageKey, 
                                    edit.UserKey, 
                                    PackageFileSize = size, 
                                    Hash = hash, 
                                    HashAlgorithm = HashAlgorithmName
                                });

                    // Prep SQL for merging in authors
                    StringBuilder loadAuthorsSql = new StringBuilder();
                    var authors = edit.Authors.Split(',');
                    for (int i = 0; i < authors.Length; i++)
                    {
                        loadAuthorsSql.Append("INSERT INTO [PackageAuthors]([PackageKey],[Name]) VALUES(@PackageKey, @Author"+ i + ")");
                        parameters.Add("Author" + i, authors[i]);
                    }

                    connection.Query<int>(@"
                            BEGIN TRANSACTION
                                -- Form a comma-separated list of authors
                                DECLARE @existingAuthors nvarchar(MAX)
                                SELECT @existingAuthors = COALESCE(@existingAuthors + ',', '') + Name
                                FROM PackageAuthors
                                WHERE PackageKey = @PackageKey

                                -- Copy packages data to package history table
                                INSERT INTO [PackageHistories]
                                SELECT      [Key] AS PackageKey,
                                            @UserKey AS UserKey,
                                            GETUTCDATE() AS Timestamp,
                                            Title,
                                            @existingAuthors AS Authors,
                                            Copyright,
                                            Description,
                                            IconUrl,
                                            LicenseUrl,
                                            ProjectUrl,
                                            ReleaseNotes,
                                            RequiresLicenseAcceptance,
                                            Summary,
                                            Tags,
                                            Hash,
                                            HashAlgorithm,
                                            PackageFileSize,
                                            LastUpdated,
                                            Published
                                FROM        [Packages]
                                WHERE       [Key] = @PackageKey

                                -- Update the packages table
                                UPDATE  [Packages]
                                SET     Copyright = @Copyright,
                                        Description = @Description,
                                        IconUrl = @IconUrl,
                                        LicenseUrl = @LicenseUrl,
                                        ProjectUrl = @ProjectUrl,
                                        ReleaseNotes = @ReleaseNotes,
                                        RequiresLicenseAcceptance = @RequiresLicenseAcceptance,
                                        Summary = @Summary,
                                        Title = @Title,
                                        Tags = @Tags,
                                        LastEdited = GETUTCDATE(),
                                        LastUpdated = GETUTCDATE(),
                                        UserKey = @UserKey,
                                        Hash = @Hash,
                                        HashAlgorithm = @HashAlgorithm,
                                        PackageFileSize = @PackageFileSize,
                                        FlattenedAuthors = @Authors
                                WHERE   [Key] = @PackageKey

                                -- Update Authors
                                DELETE FROM [PackageAuthors] 
                                WHERE PackageKey = @PackageKey

                                " + loadAuthorsSql + @"
                            
                                -- Clean this edit and all previous edits.
                                DELETE FROM [PackageEdits]
                                WHERE [PackageKey] = @PackageKey
                                AND [Key] <= @Key

                                COMMIT TRANSACTION", parameters);
                }

                Log.Info("Updated package record for {0} {1}", edit.Id, edit.Version);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to update package information. Package {0} {1}. Exception {2}", edit.Id, edit.Version, ex.Message);
                throw;
            }
            finally
            {
                DirectoryEx.TryDelete(directory);
            }
        }
    }
}

