// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NuGet.ProjectManagement.Projects
{
    /// <summary>
    /// A NuGet integrated MSBuild project.k
    /// These projects contain a project.json or package references in CSProj
    /// </summary>
    public abstract class BuildIntegratedNuGetProject : NuGetProject, INuGetIntegratedProject, IDependencyGraphProject
    {
        public abstract string MSBuildProjectPath { get; }

        protected BuildIntegratedNuGetProject() { }

        public abstract bool IsRestoreRequired(
            IEnumerable<VersionFolderPathResolver> pathResolvers,
            ISet<PackageIdentity> packagesChecked,
            ExternalProjectReferenceContext context);

        /// <summary>
        /// Retrieve the full closure of project to project references.
        /// Warnings and errors encountered will be logged.
        /// </summary>
        public abstract Task<IReadOnlyList<ExternalProjectReference>> GetProjectReferenceClosureAsync(
            ExternalProjectReferenceContext context);

        /// <summary>
        /// project.json path
        /// </summary>
        public abstract string JsonConfigPath { get; }

        /// <summary>
        /// Parsed project.json file
        /// </summary>
        public abstract PackageSpec PackageSpec { get; }

        public abstract IReadOnlyList<PackageSpec> GetPackageSpecsForRestore(
            ExternalProjectReferenceContext referenceContext);
        
        /// <summary>
        /// Project name
        /// </summary>
        public abstract string ProjectName { get; }

        public abstract DateTimeOffset LastModified { get; }

        /// <summary>
        /// Script executor hook
        /// </summary>
        public abstract Task<bool> ExecuteInitScriptAsync(
            PackageIdentity identity,
            string packageInstallPath,
            INuGetProjectContext projectContext,
            bool throwOnFailure);


    }
}
