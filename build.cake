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

// Removed artifactory repo variables.............
var zipPath = new DirectoryPath("./artifact");

var assemblyInfo = ParseAssemblyInfo("GitSemVersioning/AssemblyInfo.cs");
var MSDAssemblyVersion = assemblyInfo.AssemblyVersion;
var MSDAssemblyInformationalVersion = assemblyInfo.AssemblyInformationalVersion;
public string completeAssemblyInformationalVersion = "";
public string completeAssemblyVersion = "";

var gitVersion = GitVersion(new GitVersionSettings {});
var commitsSinceVersionSource = gitVersion.CommitsSinceVersionSource;
var projectVersionNumber = gitVersion.MajorMinorPatch;

// Determine branch label for WiX product name
public string branchLabel = "";
if (gitVersion.BranchName == "develop") {
    branchLabel = "Alpha";
}
else if (gitVersion.BranchName.StartsWith("release/") || gitVersion.BranchName.StartsWith("hotfix/")) {
    branchLabel = "Beta";
}
else if (gitVersion.BranchName == "master") {
    branchLabel = ""; 
}
else {
    branchLabel = "Dev";
}

Information($"Branch: {gitVersion.BranchName} -> Label: '{branchLabel}'");

var gitUserName = Argument("gitusername", "PROVIDED_BY_GITHUB");
var gitUserPassword = Argument("gituserpassword", "PROVIDED_BY_GITHUB");
var githubRunAttempt = Argument("githubRunAttempt", "PROVIDED_BY_GITHUB");
var enableDevMSI = Argument<bool>("enableDevMSI", false);
var wixFile = Argument("wixFile","./GitSemVersioning.Setup/Product.wxs");
var wixProjFile = Argument("wixProjFile","./GitSemVersioning.Setup/GitSemVersioning.Setup.wixproj");

var githubRunNumber = Argument("githubRunNumber", "PROVIDED_BY_GITHUB");
var devCycleBaseRunNumber = Argument("devCycleBaseRunNumber", EnvironmentVariable("DEV_CYCLE_BASE_RUN_NUMBER") ?? PROVIDED_BY_GITHUB);

var suffix = (int.Parse(githubRunNumber) - int.Parse(devCycleBaseRunNumber)).ToString();
Information($"Calculated suffix: {suffix}");

if (gitVersion.BranchName == "develop") {
    completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-alpha.", commitsSinceVersionSource);
    completeAssemblyVersion = string.Concat(projectVersionNumber, ".", commitsSinceVersionSource);
}
else if (gitVersion.BranchName.StartsWith("release/") || gitVersion.BranchName.StartsWith("hotfix/")) {
    completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-beta.", commitsSinceVersionSource) + "-" + suffix;
    completeAssemblyVersion = string.Concat(projectVersionNumber, ".", commitsSinceVersionSource) + "-" + suffix;
}
else if (gitVersion.BranchName.StartsWith("feature/")) {
    completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-feature.", commitsSinceVersionSource) + "-" + suffix;
    completeAssemblyVersion = string.Concat(projectVersionNumber, ".", commitsSinceVersionSource) + "-" + suffix;
}
else if (gitVersion.BranchName.StartsWith("bugfix/")) {
    completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-bugfix.", commitsSinceVersionSource) + "-" + suffix;
    completeAssemblyVersion = string.Concat(projectVersionNumber, ".", commitsSinceVersionSource) + "-" + suffix;
}
else if (gitVersion.BranchName == "master") {
    completeAssemblyInformationalVersion = gitVersion.MajorMinorPatch;
    completeAssemblyVersion = gitVersion.MajorMinorPatch;
}

Information("BranchName:: " + gitVersion.BranchName);

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

// before building MSI, update the ProductVersion in AssemblyInfo.cs file so that while installing MSI, it will show the correct version, not previous version
// before build execute ACS registration task as it is required to update the licenseclient file if production tag major version increased
Task("Build").IsDependentOn("Restore").IsDependentOn("SetVersionInAssemblyInWix").IsDependentOn("SetBranchLabelInWix").Does(() =>
{
    Information($"Major.Minor.Patch.Revison for AssemblyVersion(AssemblyInfo.cs) to be use in Wix as Version: {assemblyInfo.AssemblyVersion}");
    Information($"Major.Minor.Patch.Revison for AssemblyInformationalVersion(AssemblyInfo.cs) as: {assemblyInfo.AssemblyInformationalVersion}");
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
        ? projectVersionNumber.ToString()
        : (!string.IsNullOrEmpty(gitVersion.PreReleaseLabel)
            ? char.ToUpper(gitVersion.PreReleaseLabel[0]) + gitVersion.PreReleaseLabel.Substring(1) + " "
            : "")
        + completeAssemblyInformationalVersion.ToString();


    // Write back to file
    var updatedJson = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
    System.IO.File.WriteAllText(jsonPath, updatedJson);

    Information("WebToolVersion updated to: " + projectVersionNumber);

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


 Task("SetBranchLabelInWix").ContinueOnError().Does(() => {
    Information($"Setting branch label in WiX file: '{branchLabel}' for branch: {gitVersion.BranchName}");
    Information($"Major.Minor.Patch.Revison for AssemblyVersion(AssemblyInfo.cs) to be use in Wix as Version: {assemblyInfo.AssemblyVersion}");
    Information($"Major.Minor.Patch.Revison for AssemblyInformationalVersion(AssemblyInfo.cs) as: {assemblyInfo.AssemblyInformationalVersion}");
    if (!System.IO.File.Exists(wixFile))
     {
         Error($"File not found: {wixFile}");
         return;
     }
    // Update the WiX file to use dynamic branch label
    var wixContent = System.IO.File.ReadAllText(wixFile);
    
    // Define the different possible product name patterns
    var possiblePatterns = new[] {
        "$(var.ProductName) Alpha $(var.VERSION)",
        "$(var.ProductName) Beta $(var.VERSION)", 
        "$(var.ProductName) Dev $(var.VERSION)",
        "$(var.ProductName) $(var.VERSION)"
    };
    
    // Determine the new product name format
    string newProductName;
     if (string.IsNullOrEmpty(branchLabel))
     {
         newProductName = $"$(var.ProductName) {gitVersion.MajorMinorPatch}"; 
     }
     else if (branchLabel == "Alpha")
     {
         newProductName = $"$(var.ProductName) {branchLabel} $(var.VERSION)";
     }
     else
     {
         newProductName = $"$(var.ProductName) {branchLabel} $(var.VERSION)-{suffix}";
     }
    
    // Replace any existing pattern with the new one
     foreach (var pattern in possiblePatterns)
     {
         if (wixContent.Contains($"Name=\"{pattern}\""))
         {
             wixContent = wixContent.Replace($"Name=\"{pattern}\"", $"Name=\"{newProductName}\"");
             Information($"Replaced WiX product name pattern: {pattern} -> {newProductName}");
             break;
         }
     }
    
    // Write the updated content back
    System.IO.File.WriteAllText(wixFile, wixContent);
    Information($"WiX file updated with branch label: {branchLabel}");
});  


Task("SetVersionInAssemblyInWix").Does(() => 
{
    GetAllAssemblyinfoPath();
    foreach (var path in allProjectAssemblyInfoPath)
    {
        ReplaceVersionInWix(path, MSDAssemblyVersion, completeAssemblyVersion);
        ReplaceVersionInWix(path, MSDAssemblyInformationalVersion, completeAssemblyInformationalVersion);
    }
});
// Replaces version based on bambooBranch version
public void ReplaceVersionInWix(string fileName, string searchWith, string replaceWith)
{
    var configData = System.IO.File.ReadAllText(fileName, Encoding.UTF8);
    configData = Regex.Replace(configData, searchWith, replaceWith);
    Information($"Replaced '{searchWith}' with '{replaceWith}' in {fileName}");
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


// Function to check if current master tag major version is less than new major version
bool IsMajorVersionUpgrade()
{
    try
    {
        var masterTags = GitTags(".").Where(tag => !tag.FriendlyName.Contains("-"));
        if (!masterTags.Any()) return false;
        
        var latestVersion = masterTags
            .Select(tag => System.Version.Parse(tag.FriendlyName.TrimStart('v')))
            .OrderByDescending(v => v)
            .First();
        
        return gitVersion.Major > latestVersion.Major;
    }
    catch
    {
        return false;
    }
}

Task("Tagmaster").Does(() => {
    Information($"GitHub Run Number: {githubRunNumber}");
    Information("GitVersion object details: {0}", JsonConvert.SerializeObject(gitVersion, Formatting.Indented));
    
    // Check if this is a major version upgrade
    bool isMajorUpgrade = IsMajorVersionUpgrade();
    Information($"Is Major Version Upgrade: {isMajorUpgrade}");
    
    if (isMajorUpgrade)
    {
        Information("🚀 MAJOR VERSION UPGRADE DETECTED!");
        Information("This indicates breaking changes or significant new features.");
        // Add any special handling for major version upgrades here
    }
    
    //Sanity check
    var isGitHubActions = EnvironmentVariable("GITHUB_ACTIONS") == "true";
    if(!isGitHubActions)
    {
        Information("Task is not running by automation pipeline, skip.");
        return;
    }

    //List and check existing tags
    Information($"Current branch {gitVersion.BranchName}");

    //comment below line to consider all branches
    if (gitVersion.BranchName != "master" && gitVersion.BranchName != "develop" && !gitVersion.BranchName.StartsWith("release/") && !gitVersion.BranchName.StartsWith("hotfix/") && !enableDevMSI)
    {
        Information($"Current branch '{gitVersion.BranchName}' is not master/develop/releaes/hotfix/enableDevMSI(True). Skip tagging.");
        return;
    }
    if(string.IsNullOrEmpty(gitUserName) || string.IsNullOrEmpty(gitUserPassword) ||
        gitUserName == "PROVIDED_BY_GITHUB" || gitUserPassword == "PROVIDED_BY_GITHUB")
    {
        throw new Exception("Git Username/Password not provided to automation script.");
    }

    //List and check existing tags
    Information("Previous Releases:");
    var currentTags = GitTags(".");
    foreach(var tag in currentTags)
    {
        Information(tag.FriendlyName);
    }
    string branchTag;
     if (gitVersion.BranchName == "master")
     {
         branchTag = $"v{gitVersion.MajorMinorPatch}";
     }
     else if (gitVersion.BranchName == "develop")
     {
         branchTag = $"v{gitVersion.MajorMinorPatch}-alpha.{gitVersion.CommitsSinceVersionSource}";
     }
    else if (gitVersion.BranchName.StartsWith("release/") || gitVersion.BranchName.StartsWith("hotfix/"))
     {
         branchTag = $"v{gitVersion.MajorMinorPatch}-beta.{gitVersion.CommitsSinceVersionSource}-{suffix}";
     }
     else if (enableDevMSI)
     {
         branchTag = $"v{gitVersion.MajorMinorPatch}-feature.{gitVersion.CommitsSinceVersionSource}-{suffix}";
         if (gitVersion.BranchName.StartsWith("bugfix/"))
         {
             branchTag = $"v{gitVersion.MajorMinorPatch}-bugfix.{gitVersion.CommitsSinceVersionSource}-{suffix}";
         }    
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
    .IsDependentOn("Tagmaster");

RunTarget(target);