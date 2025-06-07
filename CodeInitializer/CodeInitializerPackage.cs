using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace CodeInitializer
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(CodeInitializerPackage.PackageGuidString)]
    public sealed class CodeInitializerPackage : AsyncPackage
    {
        public const string PackageGuidString = "a9b13cd6-83b6-426d-a1bd-186957a3de00";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        }
    }
}
