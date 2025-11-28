using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;
using UnityEditor.Experimental; // in case some APIs needed

public static class AutoVRCUploader
{
    // Defaults (can be overridden via CLI args)
    private static string scenePath = "Assets/scenes/main.unity";
    private static string thumbnailPath = "Assets/Editor/thumbnail.png";
    private static string worldName = "A whole new world";
    private static string worldId = "wrld_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
    private const int AgreementVersion = 1;
    private const string AgreementCode = "content.copyright.owned";
    private const string SessionKey = "VRCSdkControlPanel.CopyrightAgreement.ContentList";

    // Session persistence keys
    private const string Session_UploadPending = "AutoVRCUploader.Pending";
    private const string Session_CLIArgs = "AutoVRCUploader.CLIArgs";

    private static bool isCli = false;
    private static bool initialized = false;

    private enum BuildPlatform
    {
        PC,
        Android
    }

    private static BuildPlatform buildPlatform = BuildPlatform.PC;

    // Entry points
    [MenuItem("Tools/AutoVRCUploader/Upload World")]
    private static void MenuUploadWorld()
    {
        Debug.Log("[Uploader] Started from menu.");
        ApplyCLIArgs();
        PrepareAndStartUploader();
    }

    public static void UploadWorldCLI()
    {
        isCli = true;
        Debug.Log("[Uploader] CLI entry point reached.");
        ApplyCLIArgs();

        // persist the fact that a CLI upload started so we can resume after assembly reloads
        SessionState.SetString(Session_CLIArgs, string.Join("|",
            new[] { scenePath, thumbnailPath, worldName, worldId, buildPlatform.ToString() }));
        SessionState.SetBool(Session_UploadPending, true);

        PrepareAndStartUploader();
    }

    // This runs after domain reloads as well — Resume if pending
    [InitializeOnLoadMethod]
    private static void InitializeOnLoad()
    {
        // If we were left mid-upload due to assembly reload, resume
        if (SessionState.GetBool(Session_UploadPending, false))
        {
            var saved = SessionState.GetString(Session_CLIArgs, null);
            if (!string.IsNullOrEmpty(saved))
            {
                try
                {
                    var parts = saved.Split('|');
                    if (parts.Length >= 5)
                    {
                        scenePath = parts[0];
                        thumbnailPath = parts[1];
                        worldName = parts[2];
                        worldId = parts[3];
                        var plat = parts[4];
                        buildPlatform = plat.Equals("Android", StringComparison.OrdinalIgnoreCase)
                            ? BuildPlatform.Android
                            : BuildPlatform.PC;
                        isCli = true;
                        Debug.Log("[Uploader] Resuming pending CLI upload after assembly reload.");
                        PrepareAndStartUploader();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[Uploader] Failed to restore session: " + ex);
                    SessionState.EraseString(Session_CLIArgs);
                    SessionState.SetBool(Session_UploadPending, false);
                }
            }
            else
            {
                SessionState.SetBool(Session_UploadPending, false);
            }
        }
    }

    private static async Task EnsureBuildTargetAsync()
    {
        Debug.Log($"[Uploader] Ensuring build target for platform: {buildPlatform}");

        switch (buildPlatform)
        {
            case BuildPlatform.PC:
                await SetBuildTargetAsync(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
                break;

            case BuildPlatform.Android:
                await SetBuildTargetAsync(BuildTargetGroup.Android, BuildTarget.Android);
                break;
        }
    }

    private static async Task SetBuildTargetAsync(BuildTargetGroup group, BuildTarget target)
    {
        if (EditorUserBuildSettings.activeBuildTarget == target)
        {
            Debug.Log("[Uploader] Build target already correct.");
            return;
        }

        Debug.Log($"[Uploader] Switching Unity build target to {target}...");

        bool result = EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);

        if (!result)
            throw new Exception($"[Uploader] Failed to switch Unity build target to: {target}");

        Debug.Log($"[Uploader] Build target switched to {target}.");
    }

    private static void PrepareAndStartUploader()
    {
        if (initialized) return;
        initialized = true;

        Debug.Log("[Uploader] Waiting for VRChat SDK to initialize...");
        EditorApplication.update += WaitForSdkMenuAndBuilder;
    }

    private static async void WaitForSdkMenuAndBuilder()
    {
        if (EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel"))
        {
            EditorApplication.update -= WaitForSdkMenuAndBuilder;

            Debug.Log("[Uploader] VRChat SDK menu detected, waiting for builder API...");

            // Poll until builder API is ready
            IVRCSdkWorldBuilderApi builderApi = null;
            int timeoutMs = 30000;
            int elapsed = 0;
            int pollInterval = 500;

            while (builderApi == null && elapsed < timeoutMs)
            {
                if (VRCSdkControlPanel.TryGetBuilder(out builderApi))
                    break;

                await Task.Delay(pollInterval);
                elapsed += pollInterval;
            }

            if (builderApi == null)
            {
                Debug.LogError("[Uploader] Builder API not available after timeout. Aborting.");
                // clear session pending so we don't spin forever
                SessionState.SetBool(Session_UploadPending, false);
                if (isCli)
                {
                    EditorApplication.Exit(1);
                    return;
                }
            }

            Debug.Log("[Uploader] Builder API ready. Starting upload...");

            RunUploadAsync();
        }
    }

    private static async void RunUploadAsync()
    {
        try
        {
            await UploadWorldAsync();
            Debug.Log("[Uploader] Upload completed — exiting Unity.");

            // clear pending marker
            SessionState.SetBool(Session_UploadPending, false);
            SessionState.EraseString(Session_CLIArgs);

            if (isCli)
            {
                EditorApplication.Exit(0);
            }

        }
        catch (Exception ex)
        {
            Debug.LogError("[Uploader] Upload FAILED:\n" + ex);
            SessionState.SetBool(Session_UploadPending, false);
            SessionState.EraseString(Session_CLIArgs);

            if (isCli)
            {
                EditorApplication.Exit(1);
            }
        }
    }

    private static async Task PreAgreeForContentId(string contentId)
    {
        if (string.IsNullOrEmpty(contentId))
        {
            Debug.LogError("[Uploader] contentId is null/empty – cannot pre-agree");
            return;
        }

        VRCAgreementCheckResponse check = default;
        try
        {
            check = await VRCApi.CheckContentUploadConsent(new VRCAgreementCheckRequest
            {
                AgreementCode = AgreementCode,
                ContentId = contentId,
                Version = AgreementVersion
            });
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Uploader] Failed to check server consent for {contentId}: {ex}");
            throw;
        }

        if (!check.Agreed)
        {
            try
            {
                var resp = await VRCApi.ContentUploadConsent(new VRCAgreement
                {
                    AgreementCode = AgreementCode,
                    AgreementFulltext = VRC.SDKBase.VRCCopyrightAgreement.AgreementText,
                    ContentId = contentId,
                    Version = AgreementVersion
                });

                if (resp.ContentId != contentId ||
                    resp.Version != AgreementVersion ||
                    resp.AgreementCode != AgreementCode)
                {
                    Debug.LogError($"[Uploader] Server rejected consent for {contentId}");
                    throw new Exception("Server did not accept consent");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Uploader] Failed to consent on server for {contentId}: {ex}");
                throw;
            }
        }

        string current = SessionState.GetString(SessionKey, string.Empty);
        var list = string.IsNullOrEmpty(current)
            ? new System.Collections.Generic.List<string>()
            : new System.Collections.Generic.List<string>(current.Split(';'));

        if (!list.Contains(contentId))
        {
            list.Add(contentId);
            SessionState.SetString(SessionKey, string.Join(";", list));
        }

        Debug.Log($"[Uploader] Pre-agreed for {contentId}");
    }

    private static async Task UploadWorldAsync()
    {
        Debug.Log($"[Uploader] Using: Scene: {scenePath}, Thumbnail: {thumbnailPath}, World Name: {worldName}, World ID: {worldId}");

        await EnsureBuildTargetAsync();

        if (!System.IO.File.Exists(scenePath)) throw new Exception($"Scene not found: {scenePath}");
        if (!System.IO.File.Exists(thumbnailPath)) throw new Exception($"Thumbnail not found: {thumbnailPath}");

        // Open scene
        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        if (!scene.IsValid()) throw new Exception("Failed to open scene.");
        Debug.Log("[Uploader] Scene opened.");

        // Wait for login
        while (VRC.Core.APIUser.CurrentUser == null)
        {
            Debug.Log("[Uploader] Waiting for VRChat login...");
            await Task.Delay(500);  // wait 0.5s
        }

        // Get builder API
        var builderApi = await GetBuilderApiWithRetry();
        Debug.Log("[Uploader] Builder API acquired.");

        // Pre-agree copyright
        await PreAgreeForContentId(worldId);

        // Create world object
        VRCWorld world = new VRCWorld
        {
            Name = worldName,
            ID = worldId
        };

        Debug.Log("[Uploader] Starting Build & Upload...");
        await SafeBuildAndUpload(builderApi, world, thumbnailPath);
        Debug.Log("[Uploader] Build & upload SUCCESS.");
    }

    private static async Task SafeBuildAndUpload(IVRCSdkWorldBuilderApi api, VRCWorld world, string thumbnailPath)
    {
        const int maxAttempts = 3;
        for (int i = 1; i <= maxAttempts; i++)
        {
            Debug.Log($"[Uploader] Build attempt {i}/{maxAttempts}...");
            CancellationTokenSource cts = new CancellationTokenSource();

            // Start heartbeat logger to ensure something is logged periodically during long uploads.
            var hbTask = Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        Debug.Log("[Uploader] Heartbeat: upload in progress...");
                        await Task.Delay(30000, cts.Token); // log every 30s
                    }
                }
                catch (TaskCanceledException) { }
            });

            try
            {
                await api.BuildAndUpload(world, thumbnailPath, CancellationToken.None);
                // stop heartbeat
                cts.Cancel();
                await hbTask;
                return;
            }
            catch (Exception ex)
            {
                cts.Cancel();
                try { await hbTask; } catch { }
                Debug.LogError($"[Uploader] Build attempt {i} failed: {ex}");

                if (i == maxAttempts)
                    throw;

                Debug.Log("[Uploader] Retrying in 5 seconds...");
                await Task.Delay(5000);
            }
        }
    }

    private static async Task<IVRCSdkWorldBuilderApi> GetBuilderApiWithRetry(int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (VRCSdkControlPanel.TryGetBuilder(out IVRCSdkWorldBuilderApi api))
                return api;

            await Task.Delay(500);
        }

        throw new TimeoutException("Timed out waiting for IVRCSdkWorldBuilderApi.");
    }

private static void ApplyCLIArgs()
    {
        string[] args = Environment.GetCommandLineArgs();

        string GetArg(string key)
        {
            var match = args.FirstOrDefault(a => a.StartsWith($"--{key}="));
            return match?.Substring(key.Length + 3);
        }

        scenePath = GetArg("scene") ?? scenePath;
        thumbnailPath = GetArg("thumbnail") ?? thumbnailPath;
        worldName = GetArg("name") ?? worldName;
        worldId = GetArg("id") ?? worldId;

        string platformArg = GetArg("platform")?.ToLowerInvariant();
        switch (platformArg)
        {
            case "android":
                buildPlatform = BuildPlatform.Android;
                break;

            case "pc":
            case null:
                buildPlatform = BuildPlatform.PC;
                break;

            default:
                Debug.LogWarning($"[Uploader] Unknown platform '{platformArg}', defaulting to PC.");
                buildPlatform = BuildPlatform.PC;
                break;
        }

        Debug.Log("[Uploader] CLI arguments applied.");
    }
}
