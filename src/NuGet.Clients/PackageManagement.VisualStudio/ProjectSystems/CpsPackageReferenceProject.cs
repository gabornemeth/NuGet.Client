// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.Threading;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.ProjectManagement.Projects;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using Tasks = System.Threading.Tasks;
using System.IO;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.ProjectSystem;

namespace NuGet.PackageManagement.VisualStudio
{
    /// <summary>
    /// Represents a project object associated with new VS "15" CPS project with package references.
    /// Key feature/difference is the project restore info is pushed by nomination API and stored in 
    /// a cache. Factory method retrieving the info from the cache should be provided.
    /// </summary>
    public class CpsPackageReferenceProject : PackageReferenceNuGetProjectBase
    {
        private readonly string _projectName;
        private readonly string _projectUniqueName;
        private readonly string _projectFullPath;

        private readonly Func<PackageSpec> _packageSpecFactory;
        private IScriptExecutor _scriptExecutor;
        private readonly Project _envDTEProject;
        private readonly UnconfiguredProject _unconfiguredProject;

        public CpsPackageReferenceProject(
            string projectName,
            string projectUniqueName,
            string projectFullPath,
            Func<PackageSpec> packageSpecFactory,
            Project dteProject,
            UnconfiguredProject unconfiguredProject)
        {
            if (projectFullPath == null)
            {
                throw new ArgumentNullException(nameof(projectFullPath));
            }

            if (packageSpecFactory == null)
            {
                throw new ArgumentNullException(nameof(packageSpecFactory));
            }

            _projectName = projectName;
            _projectUniqueName = projectUniqueName;
            _projectFullPath = projectFullPath;

            _packageSpecFactory = packageSpecFactory;
            _envDTEProject = dteProject;
            _unconfiguredProject = unconfiguredProject;

            InternalMetadata.Add(NuGetProjectMetadataKeys.Name, _projectName);
            InternalMetadata.Add(NuGetProjectMetadataKeys.UniqueName, _projectUniqueName);
            InternalMetadata.Add(NuGetProjectMetadataKeys.FullPath, _projectFullPath);
        }

        #region IDependencyGraphProject

        /// <summary>
        /// Making this timestamp as the current time means that a restore with this project in the graph
        /// will never no-op. We do this to keep this work-around implementation simple.
        /// </summary>
        public override DateTimeOffset LastModified => DateTimeOffset.Now;

        public override string MSBuildProjectPath => _projectFullPath;

        public override PackageSpec PackageSpec
        {
            get
            {
                return _packageSpecFactory();
            }
        }

        public override String ProjectName
        {
            get
            {
                return _projectName;
            }
        }

        public override IReadOnlyList<PackageSpec> GetPackageSpecsForRestore(
            ExternalProjectReferenceContext context)
        {
            var packageSpec = _packageSpecFactory();
            if (packageSpec != null)
            {
                return new[] { packageSpec };
            }

            return new PackageSpec[0];
        }

        public override async Tasks.Task<IReadOnlyList<ExternalProjectReference>> GetProjectReferenceClosureAsync(
            ExternalProjectReferenceContext context)
        {
            await Tasks.TaskScheduler.Default;

            var externalProjectReferences = new HashSet<ExternalProjectReference>();

            var packageSpec = _packageSpecFactory();
            if (packageSpec != null)
            {
                var projectReferences = GetProjectReferences(packageSpec);

                var reference = new ExternalProjectReference(
                    packageSpec.RestoreMetadata.ProjectPath,
                    packageSpec,
                    packageSpec.RestoreMetadata.ProjectPath,
                    projectReferences);

                externalProjectReferences.Add(reference);
            }

            return DependencyGraphProjectCacheUtility
                .GetExternalClosure(_projectFullPath, externalProjectReferences)
                .ToList();
        }

        private static string[] GetProjectReferences(PackageSpec packageSpec)
        {
            return packageSpec
                .TargetFrameworks
                .SelectMany(f => GetProjectReferences(f.Dependencies, f.FrameworkName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IEnumerable<string> GetProjectReferences(IEnumerable<LibraryDependency> libraries, NuGetFramework targetFramework)
        {
            return libraries
                .Where(l => l.LibraryRange.TypeConstraint == LibraryDependencyTarget.ExternalProject)
                .Select(l => l.Name);
        }

        public override bool IsRestoreRequired(IEnumerable<VersionFolderPathResolver> pathResolvers, ISet<PackageIdentity> packagesChecked, ExternalProjectReferenceContext context)
        {
            // TODO: when the real implementation of NuGetProject for CPS PackageReference is completed, more
            // sophisticated restore no-op detection logic is required. Always returning true means that every build
            // will result in a restore.

            var packageSpec = _packageSpecFactory();
            return packageSpec != null;
        }

        #endregion

        #region NuGetProject

        public override Tasks.Task<IEnumerable<PackageReference>> GetInstalledPackagesAsync(CancellationToken token)
        {
            PackageReference[] installedPackages;

            var packageSpec = _packageSpecFactory();
            if (packageSpec != null)
            {
                installedPackages = GetPackageReferences(packageSpec);
            }
            else
            {
                installedPackages = new PackageReference[0];
            }

            return Tasks.Task.FromResult<IEnumerable<PackageReference>>(installedPackages);
        }

        private static PackageReference[] GetPackageReferences(PackageSpec packageSpec)
        {
            var frameworkSorter = new NuGetFrameworkSorter();

            return packageSpec
                .TargetFrameworks
                .SelectMany(f => GetPackageReferences(f.Dependencies, f.FrameworkName))
                .GroupBy(p => p.PackageIdentity)
                .Select(g => g.OrderBy(p => p.TargetFramework, frameworkSorter).First())
                .ToArray();
        }

        private static IEnumerable<PackageReference> GetPackageReferences(IEnumerable<LibraryDependency> libraries, NuGetFramework targetFramework)
        {
            return libraries
                .Where(l => l.LibraryRange.TypeConstraint == LibraryDependencyTarget.Package)
                .Select(l => ToPackageReference(l, targetFramework));
        }

        private static PackageReference ToPackageReference(LibraryDependency library, NuGetFramework targetFramework)
        {
            var identity = new PackageIdentity(
                library.LibraryRange.Name,
                library.LibraryRange.VersionRange.MinVersion);

            return new PackageReference(identity, targetFramework);
        }

        public override async Task<Boolean> InstallPackageAsync(PackageIdentity packageIdentity, DownloadResourceResult downloadResourceResult, INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            
            nuGetProjectContext.Log(MessageLevel.Info, Strings.InstallingPackage, packageIdentity);

            var configuredProject = await _unconfiguredProject.GetSuggestedConfiguredProjectAsync();
            var result = await
                configuredProject.Services.PackageReferences.AddAsync
                (packageIdentity.Id, packageIdentity.Version.ToString());
            var existingReference = result.Reference;
            if (!result.Added)
            {
                await existingReference.Metadata.SetPropertyValueAsync("Version", packageIdentity.Version.ToFullString());
            }

            //TODO: Set additional metadata here.
            return true;
        }

        public override async Task<Boolean> UninstallPackageAsync(PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            var configuredProject = await _unconfiguredProject.GetSuggestedConfiguredProjectAsync();
            await configuredProject.Services.PackageReferences.RemoveAsync(packageIdentity.Id);
            return true;
        }

        private IScriptExecutor ScriptExecutor
        {
            get
            {
                if (_scriptExecutor == null)
                {
                    _scriptExecutor = ServiceLocator.GetInstanceSafe<IScriptExecutor>();
                }

                return _scriptExecutor;
            }
        }

        public override async Task<bool> ExecuteInitScriptAsync(
            PackageIdentity identity,
            string packageInstallPath,
            INuGetProjectContext projectContext,
            bool throwOnFailure)
        {
            if (ScriptExecutor != null)
            {
                var packageReader = new PackageFolderReader(packageInstallPath);

                var toolItemGroups = packageReader.GetToolItems();

                if (toolItemGroups != null)
                {
                    // Init.ps1 must be found at the root folder, target frameworks are not recognized here,
                    // since this is run for the solution.
                    var toolItemGroup = toolItemGroups
                                        .Where(group => group.TargetFramework.IsAny)
                                        .FirstOrDefault();

                    if (toolItemGroup != null)
                    {
                        var initPS1RelativePath = toolItemGroup.Items
                            .Where(p => p.StartsWith(
                                PowerShellScripts.InitPS1RelativePath,
                                StringComparison.OrdinalIgnoreCase))
                            .FirstOrDefault();

                        if (!string.IsNullOrEmpty(initPS1RelativePath))
                        {
                            initPS1RelativePath = PathUtility
                                .ReplaceAltDirSeparatorWithDirSeparator(initPS1RelativePath);

                            return await ScriptExecutor.ExecuteAsync(
                                identity,
                                packageInstallPath,
                                initPS1RelativePath,
                                _envDTEProject,
                                projectContext,
                                throwOnFailure);
                        }
                    }
                }
            }

            return false;
        }

#endregion
        public override string AssetsFile
        {
            get
            {
                var packageSpec = _packageSpecFactory();
                var restoreOutputPath = packageSpec?.RestoreMetadata.OutputPath;
                if(!string.IsNullOrEmpty(restoreOutputPath))
                {
                    return Path.Combine(restoreOutputPath, LockFileFormat.AssetsFileName);
                }
                return null;
            }
        }

        public override String JsonConfigPath
        {
            get
            {
                throw new NotImplementedException();
            }
        }
    }
}
