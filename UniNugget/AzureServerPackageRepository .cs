﻿using AutoMapper.Internal;
using Microsoft.WindowsAzure.StorageClient;

namespace Nuget.Server.AzureStorage
{
    using AutoMapper;
    using Microsoft.Azure;
    using Microsoft.WindowsAzure.Storage.Blob;
    using Nuget.Server.AzureStorage.Domain.Services;
    using Nuget.Server.AzureStorage.Doman.Entities;
    using NuGet;
    using NuGet.Server.DataServices;
    using NuGet.Server.Infrastructure;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.Versioning;

    /// <summary>
    /// An class used to provide diffrent FileSystem to <see cref="ServerPackageRepository" />
    /// </summary>
    public class AzureServerPackageRepository : IServerPackageRepository
    {
        private readonly Microsoft.WindowsAzure.Storage.CloudStorageAccount _storageAccount;
        private readonly Microsoft.WindowsAzure.CloudStorageAccount _helper;
        private readonly CloudBlobClient _blobClient;
        private readonly IPackageLocator _packageLocator;
        private readonly IAzurePackageSerializer _packageSerializer;

        public PackageSaveModes PackageSaveMode { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureServerPackageRepository"/> class.
        /// </summary>
        /// <param name="packageLocator">The package locator.</param>
        /// <param name="packageSerializer">The package serializer.</param>
        public AzureServerPackageRepository(IPackageLocator packageLocator, IAzurePackageSerializer packageSerializer)
        {
            _packageLocator = packageLocator;
            _packageSerializer = packageSerializer;
            var azureConnectionString = CloudConfigurationManager.GetSetting("StorageConnectionString");
            _storageAccount = Microsoft.WindowsAzure.Storage.CloudStorageAccount.Parse(azureConnectionString);
            _blobClient = _storageAccount.CreateCloudBlobClient();
            _helper = Microsoft.WindowsAzure.CloudStorageAccount.Parse(azureConnectionString);
        }
        public AzureServerPackageRepository(IPackageLocator packageLocator, 
                                            IAzurePackageSerializer packageSerializer,
                                            Microsoft.WindowsAzure.Storage.CloudStorageAccount storageAccount)
        {
            _packageLocator = packageLocator;
            _packageSerializer = packageSerializer;

            _storageAccount = storageAccount;
            _blobClient = _storageAccount.CreateCloudBlobClient();
        }

        /// <summary>
        /// Gets the metadata package.
        /// </summary>
        /// <param name="package">The package.</param>
        /// <returns></returns>
        public Package GetMetadataPackage(IPackage package)
        {
            var derived = new DerivedPackageData() {
                //Created = ?
                //FullPath = ?
                IsAbsoluteLatestVersion = package.IsAbsoluteLatestVersion,
                IsLatestVersion = package.IsLatestVersion
                //LastUpdated = ?
                //PackageHash = package.GetHash(),
                //PackageSize = ??
                //Path = ??
                //SupportedFrameworks = package.GetSupportedFrameworks(),
            };
            //var pkg = new Package(package, new DerivedPackageData());
            return new Package(package, derived);
        }

        /// <summary>
        /// Gets the updates.
        /// </summary>
        /// <param name="packages">The packages.</param>
        /// <param name="includePrerelease">if set to <c>true</c> [include prerelease].</param>
        /// <param name="includeAllVersions">if set to <c>true</c> [include all versions].</param>
        /// <param name="targetFrameworks">The target frameworks.</param>
        /// <param name="versionConstraints">The version constraints.</param>
        /// <returns></returns>
        /// <exception cref="System.NotImplementedException"></exception>
        public IEnumerable<IPackage> GetUpdates(
            IEnumerable<IPackageName> packages,
            bool includePrerelease,
            bool includeAllVersions,
            IEnumerable<FrameworkName> targetFrameworks,
            IEnumerable<IVersionSpec> versionConstraints)
        {
            return this.GetUpdatesCore(packages, includePrerelease, includeAllVersions, targetFrameworks, versionConstraints);
        }

        /// <summary>
        /// Searches the specified search term.
        /// </summary>
        /// <param name="searchTerm">The search term.</param>
        /// <param name="targetFrameworks">The target frameworks.</param>
        /// <param name="allowPrereleaseVersions">if set to <c>true</c> [allow prerelease versions].</param>
        /// <returns></returns>
        /// <exception cref="System.NotImplementedException"></exception>
        public IQueryable<IPackage> Search(string searchTerm, IEnumerable<string> targetFrameworks, bool allowPrereleaseVersions)
        {
            var packages = GetPackages().ToList();
            return 
                GetPackages()
                .Find(searchTerm)
                .FilterByPrerelease(allowPrereleaseVersions)
                .Where(p => p.Listed)
                .AsQueryable<IPackage>();
        }

        /// <summary>
        /// Adds the package, deletes old packages if deleteOldPackages is enabled
        /// </summary>
        /// <param name="package">The package.</param>
        public void AddPackage(IPackage package)
        {
            var name = _packageLocator.GetContainerName(package);
            name = name.Replace('.', '-');
            var container = _blobClient.GetContainerReference(name);

            var exists = CreateIfNotExist(name);
            UpdateContainerMetadata(package, container, exists);

            var blobName = package.Version.ToString();

            // packageLocator.GetItemName(package);
            var blob = container.GetBlockBlobReference(blobName);
            blob.UploadFromStream(package.GetStream());
            UpdateBlobMetadata(package, blob);

            UpdateLatestVersionFlags(container);
        }

        private void UpdateLatestVersionFlags(CloudBlobContainer container)
        {
            var blobsWithMetadata = container.ListBlobs().OfType<CloudBlockBlob>().Select(x => new
            {
                Blob = x,
                Metadata = _packageSerializer.ReadFromMetadata(x)
            }).ToList();

            foreach (var blobWithMetadata in blobsWithMetadata)
                blobWithMetadata.Metadata.IsLatestVersion = false;

            var latest = blobsWithMetadata.OrderBy(x => x.Metadata.Version).Reverse().First();
            latest.Metadata.IsLatestVersion = true;
            latest.Metadata.IsAbsoluteLatestVersion = true;

            foreach (var blobWithMetadata in blobsWithMetadata) 
                _packageSerializer.SaveToMetadata(blobWithMetadata.Metadata,blobWithMetadata.Blob);

        }

        /// <summary>
        /// Gets the packages.
        /// </summary>
        public IQueryable<IPackage> GetPackages()
        {
            return _blobClient
                .ListContainers(NsasConstants.ContainerPrefix)
                .Select(x => x.ListBlobs())
                .SelectMany(x => x)
                .OfType<CloudBlockBlob>()
                .Select(x => _packageSerializer.ReadFromMetadata(x))
                .AsQueryable<IPackage>();
        }

        /// <summary>
        /// Gets the packages from the specified container
        /// </summary>
        public IQueryable<IPackage> GetPackages(string containerName)
        {
            return _blobClient
                .GetContainerReference(containerName)
                .ListBlobs()
                .OfType<CloudBlockBlob>()
                .Select(x => _packageSerializer.ReadFromMetadata(x))
                .AsQueryable<IPackage>();
        }

        /// <summary>
        /// Removes the package.
        /// </summary>
        /// <param name="package">The package.</param>
        public void RemovePackage(IPackage package)
        {
            var name = _packageLocator.GetContainerName(package);
            name = name.Replace('.', '-');
            var container = _blobClient.GetContainerReference(name);

            if (container.Exists())
            {
                var blobName = _packageLocator.GetItemName(package);
                var blob = container.GetBlockBlobReference(blobName);
                blob.DeleteIfExists();
            }
        }

        /// <summary>
        /// Removes the package.
        /// </summary>
        /// <param name="packageId">The package identifier.</param>
        /// <param name="version">The version.</param>
        public void RemovePackage(string packageId, SemanticVersion version)
        {
            RemovePackage(new AzurePackage
            {
                Id = packageId,
                Version = version,
            });
        }

        /// <summary>
        /// Gets the source.
        /// </summary>
        /// <value>
        /// The source.
        /// </value>
        public string Source
        {
            get
            {
                return "/";
            }
        }

        /// <summary>
        /// Gets a value indicating whether [supports prerelease packages].
        /// </summary>
        /// <value>
        /// <c>true</c> if [supports prerelease packages]; otherwise, <c>false</c>.
        /// </value>
        public bool SupportsPrereleasePackages
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Updates the container metadata.
        /// </summary>
        /// <param name="package">The package.</param>
        /// <param name="container">The container.</param>
        /// <param name="exists">if set to <c>true</c> [exists].</param>
        private void UpdateContainerMetadata(IPackage package, CloudBlobContainer container, bool exists)
        {
            container.Metadata[Constants.LatestModificationDate] = DateTimeOffset.Now.ToString();
            if (!exists)
            {
                container.Metadata[Constants.Created] = DateTimeOffset.Now.ToString();
            }
            container.Metadata[Constants.LastUploadedVersion] = package.Version.ToString();
            container.Metadata[Constants.PackageId] = package.Id;
            container.SetMetadata();
        }

        /// <summary>
        /// Updates the BLOB metadata.
        /// </summary>
        /// <param name="package">The package.</param>
        /// <param name="blob">The BLOB.</param>
        private void UpdateBlobMetadata(IPackage package, CloudBlockBlob blob)
        {
            blob.Metadata[Constants.LatestModificationDate] = DateTimeOffset.Now.ToString();
            var azurePackage = Mapper.Map<AzurePackage>(package);
            //blob.Metadata[AzurePropertiesConstants.Package] = JsonConvert.SerializeObject(azurePackage);
            _packageSerializer.SaveToMetadata(azurePackage, blob);
            blob.SetMetadata();
        }

        /// <summary>
        /// Gets the latest BLOB.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <returns></returns>
        private CloudBlockBlob GetLatestBlob(CloudBlobContainer container)
        {
            container.FetchAttributes();
            var latest = container.Metadata[Constants.LastUploadedVersion];

            return container.GetBlockBlobReference(latest);
        }

        /// <summary>
        /// Gets the package blob.
        /// </summary>
        /// <param name="package">The package.</param>
        public CloudBlockBlob GetBlob(IPackage package)
        {
            var name = _packageLocator.GetContainerName(package);
            name = name.Replace('.', '-');
            var container = _blobClient.GetContainerReference(name);

            var blobName = _packageLocator.GetItemName(package);
            var blob = container.GetBlockBlobReference(blobName);
            return blob;

        }

        /// <summary>
        /// Gets the package blob.
        /// </summary>
        /// <param name="packageId">The package identifier.</param>
        /// <param name="version">The version.</param>
        public CloudBlockBlob GetBlob(string packageId, SemanticVersion version)
        {
            return GetBlob(new AzurePackage
            {
                Id = packageId,
                Version = version,
            });
        }

        /// <summary>
        /// Checks  if a container exists and creates if if it doesnt
        /// </summary>
        /// <param name="containerName">The container name in question</param>
        /// <returns>True if the container was created, false else</returns>
        public bool CreateIfNotExist(string containerName)
        {
            var cli = _helper.CreateCloudBlobClient();
            var con = cli.GetContainerReference(containerName);
            return con.CreateIfNotExist();
        }
    }
}