#tool "nuget:?package=NuGet.CommandLine&version=5.5.1"
#tool "nuget:?package=GitVersion.CommandLine&version=5.3.5"
#tool "nuget:?package=OpenCover&version=4.7.922"
#addin "nuget:?package=Cake.Curl&version=4.1.0"
#addin "nuget:?package=Cake.Git&version=1.0.1"
#addin "nuget:?package=Cake.FileHelpers&version=3.0.0"
#tool "nuget:?package=Microsoft.TestPlatform&version=16.6.1"
#addin "nuget:?package=Newtonsoft.Json&version=9.0.1&prerelease"
#tool "nuget:?package=coverlet.console&version=3.1.2"
#tool "nuget:?package=Microsoft.CodeCoverage&version=16.9.4"
#addin "nuget:?package=Newtonsoft.Json&version=13.0.1"
using Cake.Common.Tools.GitVersion;
using Newtonsoft.Json; 
using System.Net.Http;
using System.Threading;
using Cake.Core.Text;
using System.Text.RegularExpressions;

public const string PROVIDED_BY_GITHUB = "PROVIDED_BY_GITHUB";

var solution = Argument("solution", "./GitSemVersioning.sln");
var target = Argument("do", "build");
var configuration = Argument("configuration", "Release");
var testResultsDir = Directory("./TestResults");
var buildVersion = "1.1";
var ouputDir = Directory("./obj");
List<string> allProjectAssemblyInfoPath = new List<string>();

// Removed sonarQube and artifactory arguments....

var gitVersion = GitVersion(new GitVersionSettings {});
var githubBuildNumber = gitVersion.CommitsSinceVersionSource;
var gitProjectVersionNumber = gitVersion.MajorMinorPatch;
var projectVersionNumber = gitVersion.MajorMinorPatch;
public string completeVersionForAssemblyInfo = gitVersion.MajorMinorPatch;
public string completeVersionForWix = gitVersion.MajorMinorPatch;
public string completeVersionForAssemblyInfo_unstable = "";
public string completeVersionForWix_unstable = "";
//string.Concat(gitVersion.MajorMinorPatch, ".", githubBuildNumber)
if (gitVersion.BranchName == "develop") {
    completeVersionForAssemblyInfo_unstable = string.Concat(projectVersionNumber, "-alpha.", githubBuildNumber);
    completeVersionForWix_unstable = string.Concat(projectVersionNumber, "-alpha.", githubBuildNumber);
}
else if (gitVersion.BranchName.StartsWith("release/") || gitVersion.BranchName.StartsWith("hotfix/")) {
    completeVersionForAssemblyInfo_unstable = string.Concat(projectVersionNumber, "-beta.", githubBuildNumber);
    completeVersionForWix_unstable = string.Concat(projectVersionNumber, "-beta.", githubBuildNumber);
}
else if (gitVersion.BranchName.StartsWith("feature/")) {
    completeVersionForAssemblyInfo_unstable = string.Concat(projectVersionNumber, "-feature.", githubBuildNumber);
    completeVersionForWix_unstable = string.Concat(projectVersionNumber, "-feature.", githubBuildNumber);
}
else if (gitVersion.BranchName.StartsWith("bugfix/")) {
    completeVersionForAssemblyInfo_unstable = string.Concat(projectVersionNumber, "-bugfix.", githubBuildNumber);
    completeVersionForWix_unstable = string.Concat(projectVersionNumber, "-bugfix.", githubBuildNumber);
}
else if (gitVersion.BranchName == "master") { 
    completeVersionForAssemblyInfo = gitVersion.MajorMinorPatch;
    completeVersionForWix = gitVersion.MajorMinorPatch;   
}

var gitUserName = Argument("gitusername", "PROVIDED_BY_GITHUB"); 
var gitUserPassword = Argument("gituserpassword", "PROVIDED_BY_GITHUB"); 

// Removed artifactory repo variables.............
var zipPath = new DirectoryPath("./artifact");

var EXG401UIAssemblyVersion = completeVersionForWix;

var assemblyInfo = ParseAssemblyInfo("GitSemVersioning/AssemblyInfo.cs");
var MSDAssemblyVersion = assemblyInfo.AssemblyVersion;
var MSDAssemblyVersion_unstable = assemblyInfo.AssemblyInformationalVersion;

Task("Clean").Does(() => {
	CleanDirectories("./artifact");
    CleanDirectories("./TestResults");
	CleanDirectories("**/bin/" + configuration);
	CleanDirectories("**/obj/" + configuration);
});

Task("Restore")
    .Does(() => {
        DotNetRestore("./GitSemVersioning.sln");
    });

Task("Build").IsDependentOn("Restore").Does(() =>
{
    DotNetBuild("./GitSemVersioning.sln", new DotNetBuildSettings
    {
        Configuration = configuration,
        OutputDirectory = ouputDir
    });

});

Task("UpdateWebToolVersion")
    .Does(() =>
{
    var jsonPath = "./GitSemVersioning/appsettings.Development.json";
    if (!System.IO.File.Exists(jsonPath))
    {
        Error($"File not found: {jsonPath}");
        return;
    }

    Information($"Updating WebToolVersion in {jsonPath}");

    // Read and parse JSON
    var jsonContent = System.IO.File.ReadAllText(jsonPath);
    dynamic jsonObj = JsonConvert.DeserializeObject(jsonContent);

    // Update the WebToolVersion property
    jsonObj.WebToolVersion = gitVersion.BranchName == "master"
        ? gitProjectVersionNumber.ToString()
        : (!string.IsNullOrEmpty(gitVersion.PreReleaseLabel)
            ? char.ToUpper(gitVersion.PreReleaseLabel[0]) + gitVersion.PreReleaseLabel.Substring(1) + " "
            : "")
        + completeVersionForAssemblyInfo.ToString();


    // Write back to file
    var updatedJson = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
    System.IO.File.WriteAllText(jsonPath, updatedJson);

    Information("WebToolVersion updated to: " + gitProjectVersionNumber);

           // Optionally, print the file content after
       Information("After update:");
       Information(System.IO.File.ReadAllText(jsonPath));
       // --- Add these lines to commit and push the changed assembly file from local host runner back to origin repo ---
       StartProcess("git", new ProcessSettings {
           Arguments = $"add \"{jsonPath}\""
       });
       StartProcess("git", new ProcessSettings {
           Arguments = $"commit -m \"Update appsettings.Development.json version [CI skip]\"",
           RedirectStandardOutput = true,
           RedirectStandardError = true
       });
       StartProcess("git", new ProcessSettings {
           Arguments = "push",
           RedirectStandardOutput = true,
           RedirectStandardError = true
       });
});


// Note: ContinueOnError for test Task to allow Bamboo capture TestResults produced and halt pipeline from there.

Task("Test").ContinueOnError().Does(() =>
{
    var testProjects = GetFiles("./**/*.Test.csproj");
    foreach (var project in testProjects)
    {
        var projectName = project.GetFilenameWithoutExtension();
        var testSettings = new DotNetTestSettings
        {
            Loggers = new[] { $"trx;LogFileName={projectName}.trx" },
            ArgumentCustomization = args => args
                .Append("--collect:\"XPlat Code Coverage\"")
                .Append("/p:CollectCoverage=true")
                .Append("-- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover")
        };
        DotNetTest(project.FullPath, testSettings);
    }

    // Copy Test Results and Coverage Reports
    var testResultsDir = Directory("./TestResults");
    var coverageResultsDir = Directory("./CoverageResults");
    EnsureDirectoryExists(testResultsDir);
    EnsureDirectoryExists(coverageResultsDir);

    var trxFiles = GetFiles("./**/*.trx");
    foreach (var file in trxFiles)
    {
        CopyFileToDirectory(file, testResultsDir);
    }

    var coverageFiles = GetFiles("./**/coverage*.xml");
    foreach (var file in coverageFiles)
    {
        CopyFileToDirectory(file, coverageResultsDir);
    }

    Information("Test Results:");
    foreach (var file in GetFiles(testResultsDir.Path.FullPath + "/*.trx"))
    {
        Information(file.FullPath);
    }

    Information("Coverage Results:");
    foreach (var file in GetFiles(coverageResultsDir.Path.FullPath + "/*.xml"))
    {
        Information(file.FullPath);
    }

});

Task("SetVersion")
   .Does(() => {
       var assemblyInfoPath = "./AssemblyInfo.cs";
       if (!System.IO.File.Exists(assemblyInfoPath))
       {
           Error($"File not found: {assemblyInfoPath}");
           return;
       }
       Information($"Updating version in {assemblyInfoPath}");

       // Optionally, print the file content before
       Information("Before update:");
       Information(System.IO.File.ReadAllText(assemblyInfoPath));

       var versionPattern = "(?<=AssemblyVersion\\(\")(.+?)(?=\"\\))";
       var fileVersionPattern = "(?<=AssemblyFileVersion\\(\")(.+?)(?=\"\\))";

       var versionResult = ReplaceRegexInFiles(assemblyInfoPath, versionPattern, gitVersion.AssemblySemFileVer);
       var fileVersionResult = ReplaceRegexInFiles(assemblyInfoPath, fileVersionPattern, gitVersion.AssemblySemFileVer);

       Information($"AssemblyVersion updated: {versionResult}");
       Information($"AssemblyFileVersion updated: {fileVersionResult}");

       // Optionally, print the file content after
       Information("After update:");
       Information(System.IO.File.ReadAllText(assemblyInfoPath));
   });

Task("SetVersionInAssemblyInWix").Does(() => {
    //Information($"Last MSD version to be search as: {MSDAssemblyVersion} and replace with: {completeVersionForAssemblyInfo}");
    //Information($"Last MSD version to be search as: {MSDAssemblyVersion_unstable} and replace with: {completeVersionForAssemblyInfo_unstable}");
    GetAllAssemblyinfoPath();
    foreach (var path in allProjectAssemblyInfoPath)
    {
        ReplaceVersionInWix(path, MSDAssemblyVersion, completeVersionForAssemblyInfo);
        ReplaceVersionInWix(path, MSDAssemblyVersion_unstable, completeVersionForAssemblyInfo_unstable);
    }
});
// Replaces version based on bambooBranch version
public void ReplaceVersionInWix(string fileName, string searchWith, string replaceWith)
{
    var configData = System.IO.File.ReadAllText(fileName, Encoding.UTF8);
    configData = Regex.Replace(configData, searchWith, replaceWith);
    System.IO.File.WriteAllText(fileName, configData, Encoding.UTF8);
}
//Get all project Assembly info Path
public void GetAllAssemblyinfoPath()
{
 // get the list of directories and subdirectories
  var files = System.IO.Directory.EnumerateFiles("GitSemVersioning", "AssemblyInfo.cs", SearchOption.AllDirectories);
  foreach (var path in files)
  {
   if(!path.Contains("Test"))
   {
      allProjectAssemblyInfoPath.Add(path);
   }                
  }         
}   

Task("Tagmaster").Does(() => {
    Information("GitVersion object details: {0}", JsonConvert.SerializeObject(gitVersion, Formatting.Indented));
    //Sanity check
    var isGitHubActions = EnvironmentVariable("GITHUB_ACTIONS") == "true";
    if(!isGitHubActions)
    {
        Information("Task is not running by automation pipeline, skip.");
        return;
    }

    //List and check existing tags
    Information("BranchName: {0}", gitVersion.BranchName);
    Information("Previous Releases:");
    var currentTags = GitTags(".");
    foreach(var tag in currentTags)
    {
        Information(tag.FriendlyName);
    }
    //comment below line to consider all branches
    if (gitVersion.BranchName != "master" && gitVersion.BranchName != "develop" && !gitVersion.BranchName.StartsWith("release/") && !gitVersion.BranchName.StartsWith("hotfix/"))
    {
        Information($"Current branch '{gitVersion.BranchName}' is not master/develop/releaes/hotfix. Skipping tagging.");
        return;
    }
    if(string.IsNullOrEmpty(gitUserName) || string.IsNullOrEmpty(gitUserPassword) ||
        gitUserName == "PROVIDED_BY_GITHUB" || gitUserPassword == "PROVIDED_BY_GITHUB")
    {
        throw new Exception("Git Username/Password not provided to automation script.");
    }

    string branchTag;
    if (gitVersion.BranchName == "master")
    {
        branchTag = $"v{gitVersion.MajorMinorPatch}";
    }
    else if (
        gitVersion.BranchName == "develop" ||
        gitVersion.BranchName.StartsWith("release/") ||
        gitVersion.BranchName.StartsWith("hotfix/")
    )
    {
        if (string.IsNullOrEmpty(gitVersion.PreReleaseLabel))
        {
            Information("PreReleaseLabel is not present. Skipping tagging.");
            return;
        }
        branchTag = $"v{gitVersion.MajorMinorPatch}-{gitVersion.PreReleaseLabel}.{gitVersion.CommitsSinceVersionSource}";
    }
    else
    {
        throw new Exception($"Branch '{gitVersion.BranchName}' is not supported for tagging.");
    }
    
    if(currentTags.Any(t => t.FriendlyName == branchTag))
    {
        Information($"Tag {branchTag} already exists, skip tagging.");
        return;
    }
    //Tag locally
    var workingDir = MakeAbsolute(Directory("./"));
    Information($"Tagging branch as: {branchTag} in resolved working dir: {workingDir}");
    GitTag(workingDir, branchTag);
    //Push tag to origin
    Information($"Pushing Tag to origin");
    var originUrl = "origin";
    // Push the tag to the remote repository
    var pushTagResult = StartProcess("git", new ProcessSettings
    {
        Arguments = new ProcessArgumentBuilder()
            .Append("push")
            .Append(originUrl)
            .Append(branchTag),
        RedirectStandardOutput = true,
        RedirectStandardError = true
    });

    // Log output for debugging
    if (pushTagResult != 0)
    {
        Error("Failed to push tag to origin.");
        Environment.Exit(1);
    }
    else
    {
        Information("Tag successfully pushed to origin.");
    }
});


Task("full")
    .IsDependentOn("Clean")
    .IsDependentOn("Build")
    .IsDependentOn("Test")
    .IsDependentOn("Tagmaster")
    .IsDependentOn("SetVersionInAssemblyInWix");
    //.IsDependentOn("UpdateWebToolVersion");

RunTarget(target);