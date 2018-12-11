// The following environment variables need to be set for Publish target:
// NUGET_KEY
// GITHUB_TOKEN

#addin "Octokit"
            
using Octokit;

//////////////////////////////////////////////////////////////////////
// CONST
//////////////////////////////////////////////////////////////////////

var projectName = "PipelinesTestLogger";
var repositoryName = "PipelinesTestLogger";

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");

//////////////////////////////////////////////////////////////////////
// PREPARATION
//////////////////////////////////////////////////////////////////////

var isLocal = BuildSystem.IsLocalBuild;
var isRunningOnUnix = IsRunningOnUnix();
var isRunningOnWindows = IsRunningOnWindows();
var isRunningOnBuildServer = TFBuild.IsRunningOnVSTS;
var isPullRequest = !string.IsNullOrWhiteSpace(EnvironmentVariable("SYSTEM_PULLREQUEST_PULLREQUESTID"));  // See https://github.com/cake-build/cake/issues/2149
var buildNumber = TFBuild.Environment.Build.Number.Replace('.', '-');
var branch = TFBuild.Environment.Repository.Branch;

var releaseNotes = ParseReleaseNotes("./ReleaseNotes.md");

var version = releaseNotes.Version.ToString();
var semVersion = version + (isLocal ? string.Empty : string.Concat("-build-", buildNumber));
var msBuildSettings = new DotNetCoreMSBuildSettings()
    .WithProperty("Version", semVersion)
    .WithProperty("AssemblyVersion", version)
    .WithProperty("FileVersion", version);

var buildDir = Directory("./build");
var contentFilesDir = Directory("./src/PipelinesTestLogger/contentFiles/any/any");

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(context =>
{
    Information($"Building version {semVersion} of {projectName}.");
});

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Description("Cleans the build directories.")
    .Does(() =>
    {
        CleanDirectories(GetDirectories($"./src/*/bin/{ configuration }"));
        CleanDirectories(GetDirectories($"./tests/*Tests/*/bin/{ configuration }"));
        CleanDirectories(new DirectoryPath[] { buildDir, contentFilesDir });
        CreateDirectory(buildDir);
        CreateDirectory(contentFilesDir);
    });

Task("Restore")
    .Description("Restores all NuGet packages.")
    .IsDependentOn("Clean")
    .Does(() =>
    {                
        DotNetCoreRestore($"./{projectName}.sln", new DotNetCoreRestoreSettings
        {
            MSBuildSettings = msBuildSettings
        });
    });

Task("Build")
    .Description("Builds the solution.")
    .IsDependentOn("Restore")
    .Does(() =>
    {
        DotNetCoreBuild($"./{projectName}.sln", new DotNetCoreBuildSettings
        {
            Configuration = configuration,
            NoRestore = true,
            MSBuildSettings = msBuildSettings
        });
    });

Task("Test")
    .Description("Runs all tests.")
    .IsDependentOn("Build")
    .DoesForEach(GetFiles("./tests/*Tests/*.csproj"), project =>
    {
        DotNetCoreTestSettings testSettings = new DotNetCoreTestSettings()
        {
            NoBuild = true,
            NoRestore = true,
            Configuration = configuration
        };
        if (isRunningOnBuildServer)
        {
            testSettings.Filter = "TestCategory!=ExcludeFromBuildServer";
            testSettings.Logger = "trx";
        }

        Information($"Running tests in {project}");
        DotNetCoreTest(MakeAbsolute(project).ToString(), testSettings);
    })
    .DeferOnError();
    
Task("Pack")
    .Description("Packs the NuGet packages.")
    .IsDependentOn("Build")
    .Does(() =>
    {
        // Have to copy the build output into contentFiles for NuGet to find it
        CopyFiles(GetFiles($"./src/PipelinesTestLogger/bin/{ configuration }/**/*.dll"), contentFilesDir);

        var nuspec = MakeAbsolute(File("./src/PipelinesTestLogger/PipelinesTestLogger.nuspec"));        
        NuGetPack(nuspec, new NuGetPackSettings
        {
            Version = semVersion,
            BasePath = nuspec.GetDirectory(),
            OutputDirectory = buildDir,
            Symbols = false
        });
    });

Task("Zip")
    .Description("Zips the build output.")
    .IsDependentOn("Build")
    .Does(() =>
    {  
        foreach(var projectDir in GetDirectories("./src/*"))
        {
            CopyFiles(new FilePath[] { "LICENSE", "README.md", "ReleaseNotes.md" }, $"{ projectDir.FullPath }/bin/{ configuration }");  
            var files = GetFiles($"{ projectDir.FullPath }/bin/{ configuration }/**/*");
            files.Remove(files.Where(x => x.GetExtension() == ".nupkg").ToList());
            var zipFile = File($"{ projectDir.GetDirectoryName() }-v{ semVersion }.zip");
            Zip(
                $"{ projectDir.FullPath }/bin/{ configuration }",
                $"{ buildDir }/{ zipFile }",
                files);
        }   
    });

Task("NuGet")
    .Description("Pushes the packages to the NuGet gallery.")
    .IsDependentOn("Pack")
    .WithCriteria(() => isLocal)
    .Does(() =>
    {
        var nugetKey = EnvironmentVariable("NUGET_KEY");
        if (string.IsNullOrEmpty(nugetKey))
        {
            throw new InvalidOperationException("Could not resolve NuGet API key.");
        }

        foreach (var nupkg in GetFiles($"{ buildDir }/*.nupkg"))
        {
            NuGetPush(nupkg, new NuGetPushSettings 
            {
                ApiKey = nugetKey,
                Source = "https://api.nuget.org/v3/index.json"
            });
        }
    });

Task("GitHub")
    .Description("Generates a release on GitHub.")
    .IsDependentOn("Pack")
    .IsDependentOn("Zip")
    .WithCriteria(() => isLocal)
    .Does(() =>
    {
        var githubToken = EnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(githubToken))
        {
            throw new InvalidOperationException("Could not resolve GitHub token.");
        }
        
        var github = new GitHubClient(new ProductHeaderValue("CakeBuild"))
        {
            Credentials = new Credentials(githubToken)
        };
        var release = github.Repository.Release.Create("daveaglick", repositoryName, new NewRelease("v" + semVersion) 
        {
            Name = semVersion,
            Body = string.Join(Environment.NewLine, releaseNotes.Notes),
            TargetCommitish = "master"
        }).Result;
        
        foreach(var zipFile in GetFiles($"{ buildDir }/*.zip"))
        {
            using (var zipStream = System.IO.File.OpenRead(zipFile.FullPath))
            {
                var releaseAsset = github.Repository.Release.UploadAsset(
                    release,
                    new ReleaseAssetUpload(zipFile.GetFilename().FullPath, "application/zip", zipStream, null)).Result;
            }
        }
    });

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////
    
Task("Default")
    .IsDependentOn("Test");
    
Task("Release")
    .Description("Generates a GitHub release and pushes the NuGet package.")
    .IsDependentOn("GitHub")
    .IsDependentOn("NuGet");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);