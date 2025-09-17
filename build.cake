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
public static string azureAccessToken = "";
public static string azureTokenResponse = "";
public static bool isTokenValid = false;

var gitUserName = Argument("gitusername", "PROVIDED_BY_GITHUB");
var gitUserPassword = Argument("gituserpassword", "PROVIDED_BY_GITHUB");
var githubRunAttempt = Argument("githubRunAttempt", "PROVIDED_BY_GITHUB");
var githubRunNumber = Argument("githubRunNumber", "PROVIDED_BY_GITHUB");
var devCycleBaseRunNumber = Argument("devCycleBaseRunNumber", EnvironmentVariable("DEV_CYCLE_BASE_RUN_NUMBER") ?? PROVIDED_BY_GITHUB);
var enableDevMSI = Argument<bool>("enableDevMSI", false);
var wixFile = Argument("wixFile", "./GitSemVersioning.Setup/Product.wxs");
var wixProjFile = Argument("wixProjFile", "./GitSemVersioning.Setup/GitSemVersioning.Setup.wixproj");
var solution = Argument("solution", "./GitSemVersioning.sln");
var target = Argument("do", "build");
var configuration = Argument("configuration", "Release");

// Azure and ACS configuration from environment variables
var azureClientId = Argument("azureClientId", EnvironmentVariable("AZURE_CLIENT_ID") ?? PROVIDED_BY_GITHUB);
var azureClientSecret = Argument("azureClientSecret", EnvironmentVariable("AZURE_CLIENT_SECRET") ?? PROVIDED_BY_GITHUB);
var azureTenantId = Argument("azureTenantId", EnvironmentVariable("AZURE_TENANT_ID") ?? PROVIDED_BY_GITHUB);
var acsClientScope = Argument("acsClientScope", EnvironmentVariable("ACS_CLIENT_SCOPE") ?? PROVIDED_BY_GITHUB);
var acsApplicationId = Argument("acsApplicationId", EnvironmentVariable("ACS_APPLICATION_ID") ?? PROVIDED_BY_GITHUB);

var testResultsDir = Directory("./TestResults");
var assemblyInfo = ParseAssemblyInfo("./GitSemVersioning/AssemblyInfo.cs");
var outputDir = Directory("./obj");
var licenseclientKeyFile = ""; //Argument("licenseclientKeyFile", $"{sourceDir}/{projectName}/uop.acs.licenseclient.key");


var gitVersion = GitVersion(new GitVersionSettings { });
var commitsSinceVersionSource = gitVersion.CommitsSinceVersionSource;
var projectVersionNumber = gitVersion.MajorMinorPatch;
List<string> allProjectAssemblyInfoPath = new List<string>();
var MSDAssemblyVersion = assemblyInfo.AssemblyVersion;
var MSDAssemblyInformationalVersion = assemblyInfo.AssemblyInformationalVersion;
var suffix = (int.Parse(githubRunNumber) - int.Parse(devCycleBaseRunNumber)).ToString();

Information($"MSDAssemblyVersion: {MSDAssemblyVersion}");
Information($"MSDAssemblyInformationalVersion: {MSDAssemblyInformationalVersion}");

var branchLabelInWixProductName = "";
var completeAssemblyInformationalVersion = "";
var completeAssemblyVersion = string.Concat(projectVersionNumber, ".", commitsSinceVersionSource);

if (gitVersion.BranchName == "develop")
{
    branchLabelInWixProductName = "Alpha";
    completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-alpha.", commitsSinceVersionSource) + "-" + suffix;
}
else if (gitVersion.BranchName.StartsWith("release/") || gitVersion.BranchName.StartsWith("hotfix/"))
{
    branchLabelInWixProductName = "Beta";
    completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-beta.", commitsSinceVersionSource) + "-" + suffix;
}
else if (gitVersion.BranchName.StartsWith("feature/"))
{
    branchLabelInWixProductName = "Dev";
    completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-feature.", commitsSinceVersionSource) + "-" + suffix;
}
else if (gitVersion.BranchName.StartsWith("bugfix/"))
{
    branchLabelInWixProductName = "Dev";
    completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-bugfix.", commitsSinceVersionSource) + "-" + suffix;
}
else if (gitVersion.BranchName == "master")
{
    branchLabelInWixProductName = "";
    completeAssemblyInformationalVersion = projectVersionNumber;
    completeAssemblyVersion = projectVersionNumber;
}

Information($"Branch: {gitVersion.BranchName} -> Label: '{branchLabelInWixProductName}'");
Information($"completeAssemblyInformationalVersion: {completeAssemblyInformationalVersion}");
Information($"completeAssemblyVersion: {completeAssemblyVersion}");
Information($"Calculated suffix for tagging on Multiple releases, hotfix, bugfix and feature: {suffix}");

Task("Clean").Does(() =>
{
    CleanDirectories("./artifact");
    CleanDirectories("./TestResults");
    CleanDirectories("**/bin/" + configuration);
    CleanDirectories("**/obj/" + configuration);
});

Task("Restore")
    .Does(() =>
    {
        DotNetRestore("./GitSemVersioning.sln");
    });

// before building MSI, update the ProductVersion in AssemblyInfo.cs file so that while installing MSI, it will show the correct version, not previous version
// before build execute ACS registration task as it is required to update the licenseclient file if production tag major version increased
Task("Build").IsDependentOn("Restore").IsDependentOn("ACSRegistrationForMajorUpgrade").IsDependentOn("SetVersionsInAssemblyFile").IsDependentOn("SetProductNameInWix").Does(() =>
{
    Information($"Updated AssemblyVersion(AssemblyInfo.cs) to be use in WiX as Version: {ParseAssemblyInfo("./GitSemVersioning/AssemblyInfo.cs").AssemblyVersion}");
    Information($"Updated AssemblyInformationalVersion(AssemblyInfo.cs) as: {ParseAssemblyInfo("./GitSemVersioning/AssemblyInfo.cs").AssemblyInformationalVersion}");

    DotNetBuild("./GitSemVersioning.sln", new DotNetBuildSettings
    {
        Configuration = configuration,
        OutputDirectory = outputDir
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

Task("SetProductNameInWix").ContinueOnError().Does(() =>
{

    Information($"Branch Label for Product Name in WiX : {branchLabelInWixProductName}");
    if (!System.IO.File.Exists(wixFile))
    {
        Error($"File not found: {wixFile}");
        return;
    }

    // Determine the new product name format
    string newProductName;
    if (string.IsNullOrEmpty(branchLabelInWixProductName))
    {
        newProductName = $"$(var.ProductName) {gitVersion.MajorMinorPatch}";
    }
    else
    {
        newProductName = $"$(var.ProductName) {branchLabelInWixProductName} $(var.VERSION)-{suffix}";
    }

    // Update the WiX file to use dynamic product name
    var currentProductNameInWix = "$(var.ProductName) $(var.VERSION)";
    var wixContent = System.IO.File.ReadAllText(wixFile);
    wixContent = wixContent.Replace($"Name=\"{currentProductNameInWix}\"", $"Name=\"{newProductName}\"");
    System.IO.File.WriteAllText(wixFile, wixContent);

    Information($"Replaced WiX product name pattern: {currentProductNameInWix} -> {newProductName}");
});

Task("ACSRegistrationForMajorUpgrade").IsDependentOn("GetAzureToken").Does(async () =>
{

    // Check if both conditions are met: release branch and major version upgrade
    if (!gitVersion.BranchName.StartsWith("release/") || !IsMajorVersionUpgrade())
    {
        Information("ACSRegistrationForMajorUpgrade skipped: Both release branch and major version upgrade required.");
        Information($"Branch : {gitVersion.BranchName} and IsMajorVersionUpgrade ---> {IsMajorVersionUpgrade()} : Not a release branch and Major version not incremented.");
        return;
    }
    else
    {
        Information($"Starting ACS Registration for Major Version Upgrade IsMajorVersionUpgrade ---> {IsMajorVersionUpgrade()}");
        return;
    }

    //Uncomment below for real ACS registration
    // if (!isTokenValid || string.IsNullOrEmpty(azureAccessToken) || acsApplicationId.Equals(PROVIDED_BY_GITHUB))
    // {
    //     Error("Missing Azure token or ACS Application ID.");
    //     throw new Exception("ACS Registration failed: Missing required authentication or configuration.");
    // }

    // try
    // {
    //     using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
    //     {
    //         var request = new HttpRequestMessage(HttpMethod.Post,
    //             $"https://uopacs.honeywell.com/api/registrations/automations?applicationId={acsApplicationId}&applicationVersion={gitVersion.MajorMinorPatch}");
    //         request.Headers.Add("accept", "application/json");
    //         request.Headers.Add("Authorization", $"Bearer {azureAccessToken}");

    //         var response = await client.SendAsync(request);
    //         var content = await response.Content.ReadAsStringAsync();

    //         // Check if registration was successful, fail build if critical and failed
    //         if (!response.IsSuccessStatusCode)
    //         {
    //             var errorMessage = $"ACS Registration failed with status {response.StatusCode}: {content}";
    //             Error(errorMessage);

    //             var errorEntry = $"ACS FAILURE - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nBranch: {gitVersion.BranchName}\nStatus: {response.StatusCode}\nResponse: {content}\n{new string('-', 50)}\n";
    //             System.IO.File.AppendAllText(licenseclientKeyFile, errorEntry);

    //             throw new Exception(errorMessage);
    //         }

    //         var logEntry = $"ACS SUCCESS - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
    //                       $"Branch: {gitVersion.BranchName}\n" +
    //                       $"Status: {response.StatusCode}\n" +
    //                       $"Response: {content}\n" +
    //                       new string('-', 50) + "\n";

    //         System.IO.File.AppendAllText(licenseclientKeyFile, logEntry);
    //         Information("ACS Registration completed successfully.");
    //     }
    // }
    // catch (Exception ex) when (!(ex is Exception && ex.Message.Contains("ACS Registration failed")))
    // {
    //     var errorEntry = $"ACS ERROR - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nError: {ex.Message}\n{new string('-', 50)}\n";
    //     System.IO.File.AppendAllText(licenseclientKeyFile, errorEntry);
    //     Error($"ACS Registration failed: {ex.Message}");
    //     throw; // Re-throw to fail the build
    // }
});

Task("GetAzureToken").Does(async () =>
{
    // Check if both conditions are met: release branch and major version upgrade
    if (!gitVersion.BranchName.StartsWith("release/") || !IsMajorVersionUpgrade())
    {
        Information("GetAzureToken skipped: Both release branch and major version upgrade required.");
        Information($"Branch : {gitVersion.BranchName} and IsMajorVersionUpgrade ---> {IsMajorVersionUpgrade()} : Major version not incremented.");
        return;
    }
    else
    {
        Information($"Starting GetAzureToken IsMajorVersionUpgrade ---> {IsMajorVersionUpgrade()}");
        return;
    }
    //Uncomment below for real ACS registration
    // // Validate environment variables
    // if (azureClientId.Equals(PROVIDED_BY_GITHUB) || azureClientSecret.Equals(PROVIDED_BY_GITHUB) ||
    //     azureTenantId.Equals(PROVIDED_BY_GITHUB) || acsClientScope.Equals(PROVIDED_BY_GITHUB))
    // {
    //     var errorMessage = "Missing Azure configuration. Set AZURE_CLIENT_ID, AZURE_CLIENT_SECRET, AZURE_TENANT_ID, ACS_CLIENT_SCOPE.";
    //     Error(errorMessage);
    //     isTokenValid = false;
    //     throw new Exception(errorMessage);
    // }

    // try
    // {
    //     using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
    //     {
    //         var request = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{azureTenantId}/oauth2/v2.0/token");
    //         request.Content = new FormUrlEncodedContent(new[] {
    //             new KeyValuePair<string, string>("client_id", azureClientId),
    //             new KeyValuePair<string, string>("scope", acsClientScope),
    //             new KeyValuePair<string, string>("client_secret", azureClientSecret),
    //             new KeyValuePair<string, string>("grant_type", "client_credentials")
    //         });

    //         var response = await client.SendAsync(request);

    //         if (!response.IsSuccessStatusCode)
    //         {
    //             var errorContent = await response.Content.ReadAsStringAsync();
    //             var errorMessage = $"Azure token request failed with status {response.StatusCode}: {errorContent}";
    //             Error(errorMessage);
    //             isTokenValid = false;
    //             azureAccessToken = "";
    //             throw new Exception(errorMessage);
    //         }

    //         azureTokenResponse = await response.Content.ReadAsStringAsync();
    //         var tokenData = JsonConvert.DeserializeObject<dynamic>(azureTokenResponse);
    //         azureAccessToken = tokenData.access_token;
    //         isTokenValid = true;

    //         Information("Azure token retrieved successfully.");
    //     }
    // }
    // catch (Exception ex) when (!(ex.Message.Contains("Azure token request failed") || ex.Message.Contains("Missing Azure configuration")))
    // {
    //     var errorMessage = $"Azure token acquisition failed: {ex.Message}";
    //     Error(errorMessage);
    //     isTokenValid = false;
    //     azureAccessToken = "";
    //     throw new Exception(errorMessage);
    // }
});

// Function to check if current master tag major version is less than new major version, modify this function so that is can also check alpha/beta tags
// alpha and beta tags may have major version increment, so need to check those tags also, so check develop, release, hotfix, bugfix and feature branches not just master branch
bool IsMajorVersionUpgrade()
{
    try
    {
        var masterTags = GitTags(".").Where(tag => !tag.FriendlyName.Contains("-"));
        if (!masterTags.Any()) return false;

        Information("Available master tags:");
        foreach (var tag in masterTags)
        {
            Information($"Tag: {tag.FriendlyName}");
        }

        var latestVersion = masterTags
            .Select(tag => System.Version.Parse(tag.FriendlyName.TrimStart('v')))
            .OrderByDescending(v => v)
            .First();
        Information($"Current MajorMinorPatch: {gitVersion.MajorMinorPatch}  ---> {latestVersion}");

        return gitVersion.Major > latestVersion.Major;
    }
    catch
    {
        return false;
    }
}

//Execute this task only if release branch and major version increased
Task("UpdateKeyFileToOrigin")
   .Does(() =>
   {
       if (gitVersion.BranchName == "master" || gitVersion.BranchName == "develop" || gitVersion.BranchName.StartsWith("hotfix/") || gitVersion.BranchName.StartsWith("feature/") || !IsMajorVersionUpgrade())
       {
           Information($"Current branch '{gitVersion.BranchName}' is not release branch and IsMajorVersionUpgrade is {IsMajorVersionUpgrade()}. Skip updating key file to origin.");
           return;
       }

       if (!System.IO.File.Exists(licenseclientKeyFile))
       {
           Error($"File not found: {licenseclientKeyFile}");
           return;
       }
       Information($"Updating key file back to origin :: {licenseclientKeyFile}");

       //Add these lines to commit and push the changed licenseclientKeyFile from local host runner back to origin repo
       StartProcess("git", new ProcessSettings
       {
           Arguments = $"add \"{licenseclientKeyFile}\""
       });
       StartProcess("git", new ProcessSettings
       {
           Arguments = $"commit -m \"Update {licenseclientKeyFile}\"",
           RedirectStandardOutput = true,
           RedirectStandardError = true
       });
       var updateKeyFileResult = StartProcess("git", new ProcessSettings
       {
           Arguments = "push",
           RedirectStandardOutput = true,
           RedirectStandardError = true
       });
       // Log output for debugging
       if (updateKeyFileResult != 0)
       {
           Error("Failed to update licenseclientKeyFile to origin.");
           Environment.Exit(1);
       }
       else
       {
           Information("licenseclientKeyFile successfully updated to origin.");
       }
   });

Task("SetVersionsInAssemblyFile").Does(() =>
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
        if (!path.Contains("Test"))
        {
            allProjectAssemblyInfoPath.Add(path);
        }
    }
}



Task("Tagmaster").Does(() =>
{
    Information($"GitHub Run Number: {githubRunNumber}");
    Information("GitVersion object details: {0}", JsonConvert.SerializeObject(gitVersion, Formatting.Indented));

    //Sanity check
    var isGitHubActions = EnvironmentVariable("GITHUB_ACTIONS") == "true";
    if (!isGitHubActions)
    {
        Information("Task is not running by automation pipeline, skip.");
        return;
    }

    //List and check existing tags
    Information($"Current branch {gitVersion.BranchName}");

    if (!enableDevMSI && (gitVersion.BranchName.StartsWith("feature/") || gitVersion.BranchName.StartsWith("bugfix/")))
    {
        Information($"Building in feature or bugfix branch and  enableDevMSI is {enableDevMSI}, will skip Artifact Generic push.");
        return;
    }
    if (string.IsNullOrEmpty(gitUserName) || string.IsNullOrEmpty(gitUserPassword) ||
        gitUserName == "PROVIDED_BY_GITHUB" || gitUserPassword == "PROVIDED_BY_GITHUB")
    {
        throw new Exception("Git Username/Password not provided to automation script.");
    }

    //List and check existing tags
    Information("Previous Releases:");
    var currentTags = GitTags(".");
    foreach (var tag in currentTags)
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
        branchTag = $"v{gitVersion.MajorMinorPatch}-alpha.{gitVersion.CommitsSinceVersionSource}-{suffix}";
    }
    else if (gitVersion.BranchName.StartsWith("release/") || gitVersion.BranchName.StartsWith("hotfix/"))
    {
        branchTag = $"v{gitVersion.MajorMinorPatch}-beta.{gitVersion.CommitsSinceVersionSource}-{suffix}";
    }
    else if (enableDevMSI && (gitVersion.BranchName.StartsWith("feature/") || gitVersion.BranchName.StartsWith("bugfix/")))
    {
        if (gitVersion.BranchName.StartsWith("bugfix/"))
        {
            branchTag = $"v{gitVersion.MajorMinorPatch}-bugfix.{gitVersion.CommitsSinceVersionSource}-{suffix}";
        }
        else
        {
            branchTag = $"v{gitVersion.MajorMinorPatch}-feature.{gitVersion.CommitsSinceVersionSource}-{suffix}";
        }
    }
    else
    {
        throw new Exception($"Branch '{gitVersion.BranchName}' is not supported for tagging.");
    }
    if (currentTags.Any(t => t.FriendlyName == branchTag))
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
    StartProcess("git", new ProcessSettings
    {
        Arguments = $"add \"{jsonPath}\""
    });
    StartProcess("git", new ProcessSettings
    {
        Arguments = $"commit -m \"Update appsettings.Development.json version [CI skip]\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true
    });
    StartProcess("git", new ProcessSettings
    {
        Arguments = "push",
        RedirectStandardOutput = true,
        RedirectStandardError = true
    });
});

Task("full")
    .IsDependentOn("Clean")
    .IsDependentOn("Build")
    .IsDependentOn("Test")
    .IsDependentOn("Tagmaster");
    //.IsDependentOn("UpdateKeyFileToOrigin");
RunTarget(target);