// Content-access layer for Android.
//
// The decompiled game reads its raw data (.zdx/.dqx/.dgx) with relative
// paths such as "data/levels.zdx" and "gfx/maps/maps.zdx", relying on the
// console host's working directory being the shipped content root. On
// Android those files live inside the APK as assets, which File.Open cannot
// see. This bootstrap extracts the packaged asset tree into the app's private
// files directory on first launch and points the process working directory at
// it, so every existing File.Open/StreamReader/BinaryReader call site keeps
// working unchanged.
//
// NOTE: the actual assets are owned by the asset-conversion workstream. This
// layer is a no-op until raw content is placed under the project Content/
// folder (packaged as Android assets). MonoGame's ContentManager.Load<T>
// continues to resolve .xnb through TitleContainer directly.
using System;
using System.IO;

namespace Dishwasher
{
    public static class AndroidContentBootstrap
    {
        public static string ContentRoot { get; private set; }
        public static bool Ready { get; private set; }
        public static int ExtractedFiles { get; private set; }
        public static int FailedFiles { get; private set; }

#if ANDROID
        public static void Prepare()
        {
            if (Ready)
            {
                return;
            }

            try
            {
                var context = Android.App.Application.Context;
                string root = Path.Combine(context.FilesDir.AbsolutePath, "content");
                string stampPath = Path.Combine(context.FilesDir.AbsolutePath, "content.stamp");

                // PORT (perf): skip the asset extraction when this exact
                // install has already unpacked the content. The extraction is
                // only needed for the raw File.Open data (.zdx/.dqx/.resources),
                // which persists in the app files dir; re-copying all ~724
                // files on every launch costs ~1.1-1.5 s on the UI thread.
                // The stamp changes with the APK (versionCode + last update
                // time), so an app update triggers one fresh extraction.
                string stamp = BuildStamp(context);
                if (stamp != null && Directory.Exists(root) && File.Exists(stampPath))
                {
                    try
                    {
                        if (File.ReadAllText(stampPath) == stamp)
                        {
                            Environment.CurrentDirectory = root;
                            ContentRoot = root;
                            Ready = true;
                            Log.Info("bootstrap: content up to date (stamp " + stamp
                                     + "), skipped extraction");
                            return;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                Directory.CreateDirectory(root);

                CopyAssetTree(context.Assets, "", root);
                if (stamp != null)
                {
                    try
                    {
                        File.WriteAllText(stampPath, stamp);
                    }
                    catch (Exception)
                    {
                    }
                }

                Environment.CurrentDirectory = root;
                ContentRoot = root;
                Ready = true;
                Log.Info("bootstrap: extracted " + ExtractedFiles + " file(s), " + FailedFiles
                         + " failure(s) into " + root);
            }
            catch (Exception ex)
            {
                // Best effort: if extraction fails, fall back to the default
                // working directory rather than crashing boot.
                Log.Exception("bootstrap", ex);
            }
        }

        // PORT (perf): identity of the currently installed APK, used to decide
        // whether the extracted content is still current.
        private static string BuildStamp(Android.Content.Context context)
        {
            try
            {
                Android.Content.PM.PackageInfo pi = context.PackageManager.GetPackageInfo(context.PackageName, 0);
                long update = pi.LastUpdateTime;
                long code = (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.P)
                    ? pi.LongVersionCode
                    : pi.VersionCode;
                return code + ":" + update;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void CopyAssetTree(Android.Content.Res.AssetManager assets, string assetPath, string destDir)
        {
            string[] children;
            try
            {
                children = assets.List(assetPath);
            }
            catch (Exception)
            {
                return;
            }

            if (children == null || children.Length == 0)
            {
                return;
            }

            Directory.CreateDirectory(destDir);
            foreach (string child in children)
            {
                string childAsset = string.IsNullOrEmpty(assetPath) ? child : assetPath + "/" + child;
                string childDest = Path.Combine(destDir, child);
                string[] grandChildren;
                try
                {
                    grandChildren = assets.List(childAsset);
                }
                catch (Exception)
                {
                    grandChildren = null;
                }

                bool isDirectory = grandChildren != null && grandChildren.Length > 0;
                if (isDirectory)
                {
                    CopyAssetTree(assets, childAsset, childDest);
                }
                else
                {
                    try
                    {
                        using Stream input = assets.Open(childAsset);
                        using FileStream output = File.Create(childDest);
                        input.CopyTo(output);
                        ExtractedFiles++;
                    }
                    catch (Exception ex)
                    {
                        // Skip individual unreadable assets.
                        FailedFiles++;
                        Log.Info("bootstrap: skip " + childAsset + " (" + ex.Message + ")");
                    }
                }
            }
        }
#else
        public static void Prepare()
        {
            Ready = true;
        }
#endif
    }
}
