using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

[CheckBuildProjectConfigurations]
[ShutdownDotNetAfterServerBuild]
[GitHubActions("BuildAndPack",
    GitHubActionsImage.UbuntuLatest,
    GitHubActionsImage.WindowsLatest,
    GitHubActionsImage.MacOsLatest,
    ImportGitHubTokenAs = nameof(GithubToken),
    CacheExcludePatterns = new[] { "~/.nuget/packages/netescapades.enumgenerators" },
    OnPushTags = new [] {"*"},
    OnPushBranches = new[] {"master", "main"},
    OnPullRequestBranches = new[] {"*"},
    AutoGenerate = false,
    ImportSecrets = new[] {nameof(NuGetToken)},
    InvokedTargets = new[] {nameof(Clean), nameof(Test), nameof(TestPackage), nameof(PushToNuGet)}
)]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution(GenerateProjects = true)] readonly Solution Solution;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    [Parameter] readonly string GithubToken;
    [Parameter] readonly string NuGetToken;
    [Parameter] readonly AbsolutePath PackagesDirectory = RootDirectory / "packages";

    const string NugetOrgUrl = "https://api.nuget.org/v3/index.json";
    bool IsTag => GitHubActions.Instance?.GitHubRef?.StartsWith("refs/tags/") ?? false;

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            if (!string.IsNullOrEmpty(PackagesDirectory))
            {
                EnsureCleanDirectory(PackagesDirectory);
            }
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .When(!string.IsNullOrEmpty(PackagesDirectory), x=>x.SetPackageDirectory(PackagesDirectory))
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .When(IsServerBuild, x => x.SetProperty("ContinuousIntegrationBuild", "true"))
                .EnableNoRestore());
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    Target Pack => _ => _
        .DependsOn(Compile)
        .After(Test)
        .Produces(ArtifactsDirectory)
        .Executes(() =>
        {
            DotNetPack(s => s
                .SetConfiguration(Configuration)
                .SetProperty("PublishDir", ArtifactsDirectory)
                .EnableNoBuild()
                .EnableNoRestore()
                .When(IsServerBuild, x => x.SetProperty("ContinuousIntegrationBuild", "true"))
                .SetProject(Solution));
        });

    Target TestPackage => _ => _
        .DependsOn(Pack)
        .After(Test)
        .Produces(ArtifactsDirectory)
        .Executes(() =>
        {
            var projectFiles = new[]
            {
                Solution.tests.NetEscapades_EnumGenerators_Nuget_IntegrationTests.Path,
                Solution.tests.NetEscapades_EnumGenerators_Nuget_Attributes_IntegrationTests.Path,
            };

            if (!string.IsNullOrEmpty(PackagesDirectory))
            {
                DeleteDirectory(PackagesDirectory / "netescapades.enumgenerators");
                DeleteDirectory(PackagesDirectory / "netescapades.enumgenerators.attributes");
            }

            DotNetRestore(s => s
                .When(!string.IsNullOrEmpty(PackagesDirectory), x => x.SetPackageDirectory(PackagesDirectory))
                .SetConfigFile(RootDirectory / "NuGet.integration-tests.config")
                .CombineWith(projectFiles, (s, p) => s.SetProjectFile(p)));

            DotNetBuild(s => s
                .When(!string.IsNullOrEmpty(PackagesDirectory), x=>x.SetPackageDirectory(PackagesDirectory))
                .EnableNoRestore()
                .SetConfiguration(Configuration)
                .CombineWith(projectFiles, (s, p) => s.SetProjectFile(p)));

            DotNetTest(s => s
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .CombineWith(projectFiles, (s, p) => s.SetProjectFile(p)));
        });

    Target PushToNuGet => _ => _
        .DependsOn(Pack)
        .OnlyWhenStatic(() => IsTag && IsServerBuild && IsWin)
        .Requires(() => NuGetToken)
        .After(TestPackage)
        .Executes(() =>
        {
            var packages = ArtifactsDirectory.GlobFiles("*.nupkg");
            DotNetNuGetPush(s => s
                .SetApiKey(NuGetToken)
                .SetSource(NugetOrgUrl)
                .EnableSkipDuplicate()
                .CombineWith(packages, (x, package) => x
                    .SetTargetPath(package)));
        });
}
