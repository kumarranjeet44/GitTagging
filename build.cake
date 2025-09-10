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

string globalHotfixTag = "";
Task("CalculateHotfixTag").Does(() => {
    if (!gitVersion.BranchName.StartsWith("hotfix/"))
    {
        Information("Not a hotfix branch, skipping hotfix tag calculation.");
        return;
    }
        
    var currentTags = GitTags(".");
    string baseVersionTag = $"v{gitVersion.MajorMinorPatch}-beta.{gitVersion.CommitsSinceVersionSource}";
    
    // Find the next available hotfix number for this version
    int hotfixNumber = 1;
    string candidateTag;
    
    do
    {
        candidateTag = $"{baseVersionTag}.{hotfixNumber}";
        
        // Check if this tag already exists
        if (!currentTags.Any(t => t.FriendlyName == candidateTag))
        {
            break; // Found an available tag
        }
        
        hotfixNumber++;
        
        // Safety check to avoid infinite loop
        if (hotfixNumber > 999)
        {
            throw new Exception($"Too many hotfix tags for version {baseVersionTag}. Maximum 999 hotfixes supported.");
        }
        
    } while (true);
    
    globalHotfixTag = candidateTag;
    Information($"Calculated and set global hotfix tag: {globalHotfixTag} (hotfix #{hotfixNumber})");
});

Task("AddToHotfixMappings")
    .WithCriteria(() => !string.IsNullOrEmpty(Argument("hotfixBranch", "")))
    .WithCriteria(() => !string.IsNullOrEmpty(Argument("hotfixSuffix", "")))
    .Does(() => {
    
    var hotfixBranch = Argument("hotfixBranch", "");
    var hotfixSuffix = Argument("hotfixSuffix", "");
    
    Information($"Adding hotfix mapping: {hotfixBranch} -> {hotfixSuffix}");
    
    // Get current HOTFIX_MAPPINGS (this would be retrieved from repository variable in real GitHub Actions)
    var hotfixMappingsJson = EnvironmentVariable("HOTFIX_MAPPINGS") ?? "{}";
    var hotfixMappings = JsonConvert.DeserializeObject<Dictionary<string, string>>(hotfixMappingsJson);
    
    // Add or update the mapping
    hotfixMappings[hotfixBranch] = hotfixSuffix;
    
    // Serialize back to JSON
    var updatedJson = JsonConvert.SerializeObject(hotfixMappings, Formatting.None);
    
    Information($"Updated HOTFIX_MAPPINGS: {updatedJson}");
    
    // Update repository variable directly using GitHub CLI
    var isGitHubActions = EnvironmentVariable("GITHUB_ACTIONS") == "true";
    if (isGitHubActions)
    {
        var updateResult = StartProcess("gh", new ProcessSettings
        {
            Arguments = new ProcessArgumentBuilder()
                .Append("variable")
                .Append("set")
                .Append("HOTFIX_MAPPINGS")
                .Append("--body")
                .AppendQuoted(updatedJson),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        if (updateResult == 0)
        {
            Information("Successfully updated HOTFIX_MAPPINGS repository variable");
        }
        else
        {
            Error("Failed to update HOTFIX_MAPPINGS repository variable");
        }
    }
    else
    {
        Information("Not running in GitHub Actions - would update HOTFIX_MAPPINGS to: {0}", updatedJson);
    }
});

Task("AddCurrentHotfixToMappings").Does(() => {

    if (!gitVersion.BranchName.StartsWith("hotfix/"))
    {
        Information("Not a hotfix branch, skipping hotfix tag calculation.");
        return;
    }
    
    var hotfixBranch = gitVersion.BranchName;
    Information($"Processing hotfix branch: {hotfixBranch}");
    
    // Get current HOTFIX_MAPPINGS (this would be retrieved from repository variable in real GitHub Actions)
    var hotfixMappingsJson = EnvironmentVariable("HOTFIX_MAPPINGS") ?? "{}";
    var hotfixMappings = JsonConvert.DeserializeObject<Dictionary<string, string>>(hotfixMappingsJson);
    
    Information($"Current HOTFIX_MAPPINGS: {hotfixMappingsJson}");
    
    // Check if this branch already has a mapping
    if (hotfixMappings.ContainsKey(hotfixBranch))
    {
        Information($"Branch {hotfixBranch} already has mapping: {hotfixMappings[hotfixBranch]}");
        return;
    }
    
    // Find the next available suffix number
    var existingSuffixes = hotfixMappings.Values.ToList();
    int nextSuffix = 1;
    
    while (existingSuffixes.Contains(nextSuffix.ToString()))
    {
        nextSuffix++;
        if (nextSuffix > 999) // Safety check
        {
            throw new Exception("Too many hotfix suffixes. Maximum 999 supported.");
        }
    }
    
    var newSuffix = nextSuffix.ToString();
    hotfixMappings[hotfixBranch] = newSuffix;
    
    // Serialize back to JSON
    var updatedJson = JsonConvert.SerializeObject(hotfixMappings, Formatting.None);
    
    Information($"Added new mapping: {hotfixBranch} -> {newSuffix}");
    Information($"Updated HOTFIX_MAPPINGS: {updatedJson}");
    
    // Update repository variable directly using GitHub CLI
    var isGitHubActions = EnvironmentVariable("GITHUB_ACTIONS") == "true";
    if (isGitHubActions)
    {
        var updateResult = StartProcess("gh", new ProcessSettings
        {
            Arguments = new ProcessArgumentBuilder()
                .Append("variable")
                .Append("set")
                .Append("HOTFIX_MAPPINGS")
                .Append("--body")
                .AppendQuoted(updatedJson),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        if (updateResult == 0)
        {
            Information("Successfully updated HOTFIX_MAPPINGS repository variable");
        }
        else
        {
            Error("Failed to update HOTFIX_MAPPINGS repository variable");
        }
    }
    else
    {
        Information("Not running in GitHub Actions - would update HOTFIX_MAPPINGS to: {0}", updatedJson);
    }
});

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

Task("Build").IsDependentOn("Restore").IsDependentOn("AddCurrentHotfixToMappings").Does(() =>
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

Task("Set-Hashtable")
    .Does(() =>
{
    var ht = new Dictionary<string, string> {
        { "key1", "value1" },
        { "key2", "value2" }
    };

    // Serialize dictionary to JSON
    var json = Newtonsoft.Json.JsonConvert.SerializeObject(ht);

    // Write to GitHub Actions env file
    var githubEnv = EnvironmentVariable("GITHUB_ENV");
    //print all env variable here
    Information("GITHUB_ENV value: {0}", githubEnv ?? "NOT SET");
    
    if (!string.IsNullOrEmpty(githubEnv))
    {
        Information("GITHUB_ENV file exists: {0}", System.IO.File.Exists(githubEnv));
        
        if (System.IO.File.Exists(githubEnv))
        {
            Information("=== GITHUB_ENV File Content ===");
            var githubEnvContent = System.IO.File.ReadAllText(githubEnv);
            Information("Content: {0}", string.IsNullOrEmpty(githubEnvContent) ? "[EMPTY]" : githubEnvContent);
            Information("=== End GITHUB_ENV Content ===");
        }
        
        System.IO.File.AppendAllText(githubEnv, $"MY_HASHTABLE={json}{Environment.NewLine}");
        
        // Print content after writing
        Information("=== GITHUB_ENV After Writing ===");
        Information("Updated Content: {0}", System.IO.File.ReadAllText(githubEnv));
        Information("=== End Updated Content ===");
    }

    Information("Hashtable stored as JSON: {0}", json);
});

Task("Use-Hashtable")  
    .IsDependentOn("Set-Hashtable")
    .Does(() =>
{
    var json = EnvironmentVariable("MY_HASHTABLE");

    if (!string.IsNullOrEmpty(json))
    {
        var ht = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
        Information("Key1: {0}", ht["key1"]);
        Information("Key2: {0}", ht["key2"]);
    }
    else
    {
        Warning("MY_HASHTABLE environment variable is not set.");
    }
});

Task("Tagmaster").Does(() => {
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
    else if (gitVersion.BranchName.StartsWith("hotfix/"))
    {
        branchTag = globalHotfixTag;
    }
    else if (
        gitVersion.BranchName == "develop" ||
        gitVersion.BranchName.StartsWith("release/")
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
    .IsDependentOn("Tagmaster");
    //.IsDependentOn("SetVersionInAssemblyInWix");
    //.IsDependentOn("UpdateWebToolVersion");

RunTarget(target);