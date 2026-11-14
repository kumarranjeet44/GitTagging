#tool "nuget:?package=NuGet.CommandLine&version=5.5.1"
#tool "nuget:?package=GitVersion.CommandLine&version=5.3.5"
#addin "nuget:?package=Cake.Curl&version=4.1.0"
#addin "nuget:?package=Cake.Git&version=1.0.1"
#addin "nuget:?package=Cake.FileHelpers&version=4.0.1"
#addin "nuget:?package=Newtonsoft.Json&version=9.0.1&prerelease"

using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;

public const string PROVIDED_BY_GITHUB = "PROVIDED_BY_GITHUB";
public static string azureAccessToken = "";
public static string azureTokenResponse = "";
public static bool isTokenValid = false;

// Azure and ACS configuration from environment variables
var azureClientId = Argument("azureClientId", EnvironmentVariable("AZURE_CLIENT_ID") ?? PROVIDED_BY_GITHUB);
var azureClientSecret = Argument("azureClientSecret", EnvironmentVariable("AZURE_CLIENT_SECRET") ?? PROVIDED_BY_GITHUB);
var azureTenantId = Argument("azureTenantId", EnvironmentVariable("AZURE_TENANT_ID") ?? PROVIDED_BY_GITHUB);
var acsClientScope = Argument("acsClientScope", EnvironmentVariable("ACS_CLIENT_SCOPE") ?? PROVIDED_BY_GITHUB);
var acsApplicationId = Argument("acsApplicationId", EnvironmentVariable("ACS_APPLICATION_ID") ?? PROVIDED_BY_GITHUB);

var target = Argument("do", "full");
var configuration = Argument("configuration", "Release");
var projectName = Argument("projectName", "Honeywell.UOP.GitTagging");
var enableDevMSI = Argument<bool>("enableDevMSI", false);
var enableDevTag = Argument<bool>("enableDevTag", false);
var githubRunNumber = Argument("githubRunNumber", PROVIDED_BY_GITHUB);
var gitUserName = Argument("gitusername", PROVIDED_BY_GITHUB);
var gitUserPassword = Argument("gituserpassword", PROVIDED_BY_GITHUB);
var ARTIFACTORY_API = Argument("artifactoryapikey", PROVIDED_BY_GITHUB);
var devCycleBaseRunNumber = Argument("devCycleBaseRunNumber", EnvironmentVariable("DEV_CYCLE_BASE_RUN_NUMBER") ?? PROVIDED_BY_GITHUB);


var solutionFile = Argument("solution", "./GitSemVersioning.sln");
var sourceDir = Argument("sourceDir", "./GitSemVersioning");
var assemblyInfoPath = Argument("assemblyInfoPath", $"{sourceDir}/AssemblyInfo.cs");
var licenseclientKeyFile = Argument("licenseclientKeyFile", $"{sourceDir}/uop.acs.licenseclient.key");

var msiPath = new DirectoryPath("./artifact");
var msiFullPath = new FilePath($"./GitSemVersioning.Setup/bin/{configuration}/GitSemVersioning.msi");
var wixFile = Argument("wixFile", "./GitSemVersioning.Setup/Product.wxs");
var wixProjFile = Argument("wixProjFile", "./GitSemVersioning.Setup/GitSemVersioning.Setup.wixproj");

var gitVersion = GitVersion(new GitVersionSettings { });
var commitsSinceVersionSource = gitVersion.CommitsSinceVersionSource;
var projectVersionNumber = gitVersion.MajorMinorPatch;
List<string> allProjectAssemblyInfoPath = new List<string>();
var assemblyInfo = ParseAssemblyInfo(assemblyInfoPath);
var MSDAssemblyVersion = assemblyInfo.AssemblyVersion;
var MSDAssemblyInformationalVersion = assemblyInfo.AssemblyInformationalVersion;
var tagSuffix = githubRunNumber.ToString();

var completeAssemblyVersion = string.Concat(projectVersionNumber, ".", commitsSinceVersionSource);
var (label, version) = SetBranchLabelAndVersion(gitVersion, projectVersionNumber, commitsSinceVersionSource.ToString(), tagSuffix);
var branchLabelInWixProductName = label;
var completeAssemblyInformationalVersion = version;

Information($"Calculated tagSuffix for tagging on Multiple releases, hotfix, bugfix and feature: {tagSuffix}");
Information($"Branch: {gitVersion.BranchName} -> Branch Label: {branchLabelInWixProductName}");
Information($"Assembly Version: {completeAssemblyInformationalVersion}");
Information($"Complete Assembly Version: {completeAssemblyVersion}");

Task("Clean").Does(() =>
{
    CleanDirectories("./artifact");
});

Task("Restore").Does(() =>
{
    DotNetRestore(solutionFile);
});

// Before Build, execute SetVersionsInAssemblyFile and SetProductNameInWix to update Product version and Product Name in WiX file
// Execute ACS registration task to update licenseclient file if production tag major version increased, MSI shold have udpated licenseclient file
//.IsDependentOn("ACSRegistrationForMajorUpgrade")
Task("Build").IsDependentOn("Restore").IsDependentOn("SetVersionsInAssemblyFile").IsDependentOn("SetProductNameInWix").Does(() =>
{
    Information($"Complete Assembly Informational Version: {completeAssemblyInformationalVersion}");
    Information($"Complete Assembly Version: {completeAssemblyVersion}");

    var settings = new MSBuildSettings
    {
        Configuration = configuration,
    };
    MSBuild(solutionFile, settings);
    EnsureDirectoryExists(msiPath);

    if (gitVersion.BranchName != "master")
    {
        MoveFile(msiFullPath, msiPath.FullPath + "/GitSemVersioning.Setup." + completeAssemblyInformationalVersion + ".msi");
    }
    else
    {
        MoveFile(msiFullPath, msiPath.FullPath + "/GitSemVersioning.Setup." + completeAssemblyVersion + ".msi");
    }
});

Task("SetVersionsInAssemblyFile").Does(() =>
{
    Information($"Complete Assembly Informational Version: {completeAssemblyInformationalVersion}");
    Information($"Complete Assembly Version: {completeAssemblyVersion}");
    GetAllAssemblyinfoPath();
    foreach (var path in allProjectAssemblyInfoPath)
    {
        ReplaceVersionInWix(path, MSDAssemblyVersion, completeAssemblyVersion);
        ReplaceVersionInWix(path, MSDAssemblyInformationalVersion, completeAssemblyInformationalVersion);
    }
});

Task("SetProductNameInWix").Does(() =>
{
    Information($"Branch: {gitVersion.BranchName} -> Branch Label for Product Name in WiX: {branchLabelInWixProductName}");

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
        newProductName = $"$(var.ProductName) {branchLabelInWixProductName} $(var.VERSION)-{tagSuffix}";
    }
    // Update the WiX file to use dynamic product name
    var currentProductNameInWix = "$(var.ProductName) $(var.VERSION)";
    var wixContent = System.IO.File.ReadAllText(wixFile);
    wixContent = wixContent.Replace($"Name=\"{currentProductNameInWix}\"", $"Name=\"{newProductName}\"");
    System.IO.File.WriteAllText(wixFile, wixContent);
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

    if (!isTokenValid || string.IsNullOrEmpty(azureAccessToken) || acsApplicationId.Equals(PROVIDED_BY_GITHUB))
    {
        Error("Missing Azure token or ACS Application ID.");
        throw new Exception("ACS Registration failed: Missing required authentication or configuration.");
    }
    Information($"Starting ACS Registration for Major Version Upgrade IsMajorVersionUpgrade ---> {IsMajorVersionUpgrade()}");

    try
    {
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://uopacs.honeywell.com/api/registrations/automations?applicationId={acsApplicationId}&applicationVersion={gitVersion.MajorMinorPatch}");
            request.Headers.Add("accept", "application/json");
            request.Headers.Add("Authorization", $"Bearer {azureAccessToken}");

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            // Check if registration was successful, fail Build if critical and failed
            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = $"ACS Registration failed with status {response.StatusCode}: {content}";
                Error(errorMessage);

                var errorEntry = $"ACS FAILURE - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nBranch: {gitVersion.BranchName}\nStatus: {response.StatusCode}\nResponse: {content}\n{new string('-', 50)}\n";
                System.IO.File.WriteAllText(licenseclientKeyFile, errorEntry);

                throw new Exception(errorMessage);
            }

            var logEntry = $"ACS SUCCESS - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                          $"Branch: {gitVersion.BranchName}\n" +
                          $"Status: {response.StatusCode}\n" +
                          $"Response: {content}\n" +
                          new string('-', 50) + "\n";

            System.IO.File.WriteAllText(licenseclientKeyFile, content);
            Information($"ACS Registration completed successfully with content : {content}");
        }
    }
    catch (Exception ex) when (!(ex is Exception && ex.Message.Contains("ACS Registration failed")))
    {
        var errorEntry = $"ACS ERROR - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nError: {ex.Message}\n{new string('-', 50)}\n";
        System.IO.File.WriteAllText(licenseclientKeyFile, errorEntry);
        Error($"ACS Registration failed: {ex.Message}");
        throw; // Re-throw to fail the Build
    }
});

Task("GetAzureToken").Does(async () =>
{
    // Check if both conditions are met: release branch and major version upgrade
    if (!gitVersion.BranchName.StartsWith("release/") || !IsMajorVersionUpgrade())
    {
        Information("GetAzureToken skipped: Both release branch and major version upgrade required.");
        Information($"Branch : {gitVersion.BranchName} and IsMajorVersionUpgrade ---> {IsMajorVersionUpgrade()} : Not a release branch and Major version not incremented.");
        return;
    }
    Information($"Starting GetAzureToken IsMajorVersionUpgrade ---> {IsMajorVersionUpgrade()}");
    // Validate environment variables
    if (azureClientId.Equals(PROVIDED_BY_GITHUB) || azureClientSecret.Equals(PROVIDED_BY_GITHUB) ||
        azureTenantId.Equals(PROVIDED_BY_GITHUB) || acsClientScope.Equals(PROVIDED_BY_GITHUB))
    {
        var errorMessage = "Missing Azure configuration. Set AZURE_CLIENT_ID, AZURE_CLIENT_SECRET, AZURE_TENANT_ID, ACS_CLIENT_SCOPE.";
        Error(errorMessage);
        isTokenValid = false;
        throw new Exception(errorMessage);
    }

    try
    {
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{azureTenantId}/oauth2/v2.0/token");
            request.Content = new FormUrlEncodedContent(new[] {
                new KeyValuePair<string, string>("client_id", azureClientId),
                new KeyValuePair<string, string>("scope", acsClientScope),
                new KeyValuePair<string, string>("client_secret", azureClientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                var errorMessage = $"Azure token request failed with status {response.StatusCode}: {errorContent}";
                Error(errorMessage);
                isTokenValid = false;
                azureAccessToken = "";
                throw new Exception(errorMessage);
            }

            azureTokenResponse = await response.Content.ReadAsStringAsync();
            var tokenData = JsonConvert.DeserializeObject<dynamic>(azureTokenResponse);
            azureAccessToken = tokenData.access_token;
            isTokenValid = true;

            Information("Azure token retrieved successfully.");
        }
    }
    catch (Exception ex) when (!(ex.Message.Contains("Azure token request failed") || ex.Message.Contains("Missing Azure configuration")))
    {
        var errorMessage = $"Azure token acquisition failed: {ex.Message}";
        Error(errorMessage);
        isTokenValid = false;
        azureAccessToken = "";
        throw new Exception(errorMessage);
    }
});

Task("Tagmaster").Does(() =>
{
    //Sanity check
    Information("GitVersion object details: {0}", JsonConvert.SerializeObject(gitVersion, Formatting.Indented));
    Information($"enableDevTag: {enableDevTag}");
    Information($"Calculated tagSuffix for tagging on Multiple releases, hotfix, bugfix and feature: {tagSuffix}");

    var isGitHubActions = EnvironmentVariable("GITHUB_ACTIONS") == "true";
    if (!isGitHubActions)
    {
        Information("Task is not running by automation pipeline, skip.");
        return;
    }
    // Check for multiple release branches
    Information("Checking for multiple release branches...");
    var releaseBranches = GitBranches(".")
        .Where(b => b.FriendlyName.StartsWith("origin/release/"))
        .ToList();

    Information($"Found {releaseBranches.Count} release branches:");
    foreach (var branch in releaseBranches)
    {
        Information($"  - {branch.FriendlyName}");
    }

    if (releaseBranches.Count > 1)
    {
        Error($"Multiple release branches detected ({releaseBranches.Count}). Only one release branch should exist at a time.Please consolidate to a single release branch.");
        throw new Exception($"Build failed: Multiple release branches found. Please consolidate to a single release branch.");
    }

    // --- ADDED OR RE-CONFIGURED FEATURE CONDITION GATE FOR TESTING ---
    if (!enableDevTag && (gitVersion.BranchName.StartsWith("feature/") || gitVersion.BranchName.StartsWith("bugfix/") || gitVersion.BranchName == "develop"))
    {
        Information($"Building in feature/bugfix/develop/release/hotfix branch and  enableDevTag is {enableDevTag}, will skip Tagging.");
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
    string releaseTitle = $"GitTagging Stable release v{gitVersion.MajorMinorPatch}"; // Default base title

    if (gitVersion.BranchName == "master")
    {
        branchTag = $"v{gitVersion.MajorMinorPatch}";
        releaseTitle = $"GitTagging Production release {branchTag}";
    }
    else if (enableDevTag && gitVersion.BranchName == "develop")
    {
        branchTag = $"v{gitVersion.MajorMinorPatch}-alpha.{gitVersion.CommitsSinceVersionSource}-{tagSuffix}";
        releaseTitle = $"GitTagging Alpha release {branchTag}";
    }
    else if (!enableDevTag && (gitVersion.BranchName.StartsWith("release/") || gitVersion.BranchName.StartsWith("hotfix/")))
    {
        branchTag = $"v{gitVersion.MajorMinorPatch}-beta.{gitVersion.CommitsSinceVersionSource}-{tagSuffix}";
        releaseTitle = $"GitTagging Beta release {branchTag}";
    }
    else if (enableDevTag && (gitVersion.BranchName.StartsWith("feature/") || gitVersion.BranchName.StartsWith("bugfix/")))
    {
        if (gitVersion.BranchName.StartsWith("bugfix/"))
        {
            branchTag = $"v{gitVersion.MajorMinorPatch}-bugfix.{gitVersion.CommitsSinceVersionSource}-{tagSuffix}";
            releaseTitle = $"GitTagging Bugfix release {branchTag}";
        }
        else
        {
            branchTag = $"v{gitVersion.MajorMinorPatch}-feature.{gitVersion.CommitsSinceVersionSource}-{tagSuffix}";
            releaseTitle = $"GitTagging Feature release {branchTag}";
        }
    }
    else
    {
        throw new Exception($"Branch '{gitVersion.BranchName}' is not supported for tagging.");
    }
    if (currentTags.Any(t => t.FriendlyName == branchTag))
    {
        Information($"Tag {branchTag} already exists. Reusing it for the GitHub release.");
    }
    else
    {
        //Tag locally
        var workingDir = MakeAbsolute(Directory("./"));
        Information($"Tagging branch as: {branchTag} in resolved working dir: {workingDir}");
        GitTag(workingDir, branchTag);

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
    }

    // =========================================================================
    // --- NEW INJECTED RELEASE COMPILATION WORK (ZERO DUPLICATION) ---
    // =========================================================================

    // 1. Scan git commit history to capture Jira keys (e.g. UOPMSD-845)
    var jiraKeys = new HashSet<string>();
    try 
    {
        var commits = GitLog(".", 50); // Scans the last 50 commits
        var regex = new Regex(@"UOPMSD-\d+", RegexOptions.IgnoreCase);
        foreach(var c in commits)
        {
            var matches = regex.Matches(c.Message);
            foreach(Match m in matches)
            {
                jiraKeys.Add(m.Value.ToUpper());
            }
        }
    }
    catch(Exception ex)
    {
        Information("Could not extract git logs for changelog parsing: " + ex.Message);
    }

    // 2. Format ticket keys into click-ready absolute hyperlinks
    var bodyBuilder = new StringBuilder();
    foreach(var key in jiraKeys)
    {
        bodyBuilder.AppendLine($"https://honeywell.atlassian.net/browse/{key}");
    }
    string compiledNotes = bodyBuilder.ToString().Trim();

    // 3. Pipe compiled values safely out to GITHUB_OUTPUT (Respects your zero-commit security block)
    var githubOutputFile = EnvironmentVariable("GITHUB_OUTPUT");
    if (!string.IsNullOrEmpty(githubOutputFile))
    {
        var delimiter = $"EOF_{Guid.NewGuid()}";
        var outputs = new List<string> {
            $"release_tag={branchTag}",
            $"release_title={releaseTitle}",
            $"release_notes<<{delimiter}",
            compiledNotes,
            delimiter
        };
        System.IO.File.AppendAllLines(githubOutputFile, outputs);
        Information("Successfully exported release configurations to pipeline runner variables.");
    }
});

Task("CreateRelease").Does(() =>
{
    var releaseTag = EnvironmentVariable("RELEASE_TAG");
    var releaseName = EnvironmentVariable("RELEASE_TITLE");
    var releaseNotes = EnvironmentVariable("RELEASE_NOTES") ?? "";
    if (string.IsNullOrWhiteSpace(releaseTag) || string.IsNullOrWhiteSpace(releaseName))
    {
        throw new CakeException("RELEASE_TAG and RELEASE_TITLE are required to create a GitHub release.");
    }

    var token = EnvironmentVariable("GH_TOKEN");
    var repository = EnvironmentVariable("GITHUB_REPOSITORY");
    var apiUrl = EnvironmentVariable("GITHUB_API_URL");
    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(apiUrl))
    {
        throw new CakeException("GH_TOKEN, GITHUB_REPOSITORY, and GITHUB_API_URL are required to create a GitHub release.");
    }

    var msiFiles = GetFiles(msiPath + "/UOP.MSD-PID Population Tool.Setup.*.msi").ToArray();
    if (msiFiles.Length != 1)
    {
        throw new CakeException($"Expected exactly one versioned MSI in {msiPath}; found {msiFiles.Length}.");
    }

    using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
    {
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("uop-msd-build");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var releaseData = JsonConvert.SerializeObject(new
        {
            tag_name = releaseTag,
            name = releaseName,
            body = $"{releaseName}  \nList of tickets released in this build are:  \n{(string.IsNullOrWhiteSpace(releaseNotes) ? "No Jira IDs found in commit messages." : releaseNotes)}",
            generate_release_notes = false,
            prerelease = gitVersion.BranchName != "master"
        });
        using (var content = new StringContent(releaseData, Encoding.UTF8, "application/json"))
        using (var response = client.PostAsync($"{apiUrl.TrimEnd('/')}/repos/{repository}/releases", content).GetAwaiter().GetResult())
        {
            var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new CakeException($"GitHub release creation failed ({response.StatusCode}): {responseBody}");
            }

            var release = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseBody);
            var uploadUrl = release["upload_url"].ToString().Split('{')[0];
            using (var stream = System.IO.File.OpenRead(msiFiles[0].FullPath))
            using (var asset = new StreamContent(stream))
            {
                asset.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                var assetUrl = uploadUrl + "?name=" + Uri.EscapeDataString(msiFiles[0].GetFilename().ToString());
                using (var uploadResponse = client.PostAsync(assetUrl, asset).GetAwaiter().GetResult())
                {
                    var uploadBody = uploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!uploadResponse.IsSuccessStatusCode)
                    {
                        throw new CakeException($"GitHub release asset upload failed ({uploadResponse.StatusCode}): {uploadBody}");
                    }
                }
            }
        }
    }
    Information($"Created GitHub release {releaseTag} with MSI asset.");
});


Task("UpdateAssemblyFileBackToOrigin")
   .Does(() =>
   {
       if (!System.IO.File.Exists(assemblyInfoPath))
       {
           Error($"File not found: {assemblyInfoPath}");
           return;
       }
       Information($"Updating version in {assemblyInfoPath}");
       // --- Add these lines to commit and push the changed assembly file from local host runner back to origin repo ---
       StartProcess("git", new ProcessSettings
       {
           Arguments = $"add \"{assemblyInfoPath}\""
       });
       StartProcess("git", new ProcessSettings
       {
           Arguments = $"commit -m \"Update AssemblyInfo.cs version [CI skip]\"",
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
//Execute this task only if release branch and major version increased
Task("UpdateKeyFileBackToOrigin")
   .Does(() =>
   {
       if (!gitVersion.BranchName.StartsWith("release/") || !IsMajorVersionUpgrade())
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

Task("Setaisenvironment").Does(() =>
{
    if (gitVersion.BranchName.StartsWith("release/"))
    {
        Information("Building in release branch, aisclient.config AIS_ENVIRONMENT variable value updated from DEV to QA");
        ReplaceString("GitSemVersioning/aisclient.config", "DEV", "QA");
        return;
    }

    if (gitVersion.BranchName == "master" || gitVersion.BranchName.StartsWith("hotfix/"))
    {
        Information("Building in master/hotfix branch, aisclient.config AIS_ENVIRONMENT variable value updated from DEV to PROD");
        ReplaceString("GitSemVersioning/aisclient.config", "DEV", "PROD");
        return;
    }

    Information("Building in develop/feature/bugfix branch, aisclient.config AIS_ENVIRONMENT variable value is DEV");
});


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
        var lastRegisteredMasterTag = masterTags
            .Select(tag => System.Version.Parse(tag.FriendlyName.TrimStart('v')))
            .OrderByDescending(v => v)
            .First();
        Information($"Current MajorMinorPatch: {gitVersion.MajorMinorPatch}  ---> Last Registered master tag: {lastRegisteredMasterTag}");

        return gitVersion.Major > lastRegisteredMasterTag.Major;
    }
    catch
    {
        return false;
    }
}

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
    // get the list of directories and subdirectories using configurable source directory
    var files = System.IO.Directory.EnumerateFiles(sourceDir, "AssemblyInfo.cs", SearchOption.AllDirectories);
    foreach (var path in files)
    {
        if (!path.Contains("Test"))
        {
            allProjectAssemblyInfoPath.Add(path);
        }
    }
}
public void ReplaceString(string FileName, string SearchWith, string ReplaceWith)
{
    var configData = System.IO.File.ReadAllText(FileName, Encoding.UTF8);
    configData = configData.Replace(SearchWith, ReplaceWith);
    System.IO.File.WriteAllText(FileName, configData, Encoding.UTF8);
}



public (string branchLabel, string assemblyVersion) SetBranchLabelAndVersion(GitVersion gitVersion, string projectVersionNumber, string commitsSinceVersionSource, string tagSuffix)
{
    string branchLabelInWixProductName = "";
    string completeAssemblyInformationalVersion = "";

    if (gitVersion.BranchName == "develop")
    {
        branchLabelInWixProductName = "Alpha";
        completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-alpha.", commitsSinceVersionSource) + "-" + tagSuffix;
    }
    else if (gitVersion.BranchName.StartsWith("release/") || gitVersion.BranchName.StartsWith("hotfix/"))
    {
        branchLabelInWixProductName = "Beta";
        completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-beta.", commitsSinceVersionSource) + "-" + tagSuffix;
    }
    else if (gitVersion.BranchName.StartsWith("feature/"))
    {
        branchLabelInWixProductName = "Dev";
        completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-feature.", commitsSinceVersionSource) + "-" + tagSuffix;
    }
    else if (gitVersion.BranchName.StartsWith("bugfix/"))
    {
        branchLabelInWixProductName = "Dev";
        completeAssemblyInformationalVersion = string.Concat(projectVersionNumber, "-bugfix.", commitsSinceVersionSource) + "-" + tagSuffix;
    }
    else if (gitVersion.BranchName == "master")
    {
        branchLabelInWixProductName = "";
        completeAssemblyInformationalVersion = projectVersionNumber;
        completeAssemblyVersion = projectVersionNumber;
    }

    return (branchLabelInWixProductName, completeAssemblyInformationalVersion);
}



Task("Full")
    .IsDependentOn("Clean")
    .IsDependentOn("Setaisenvironment")
    .IsDependentOn("Build")
    .IsDependentOn("Tagmaster");
RunTarget(target);
