// ImportController.cs -- PORT (public build): first-run import screen.
//
// Shown by Activity1 when the public build has no validated content tree.  It
// uses plain Android widgets (so it works with touch on a bare phone), lets the
// user pick a folder (ACTION_OPEN_DOCUMENT_TREE) or a zip / XBLA package
// (ACTION_OPEN_DOCUMENT), verifies the selection by SHA-256, derives the
// textures/fonts off the UI thread (progress bar + status, no ANR), and only
// then offers START.
#if ANDROID
using System;
using System.IO;
using System.Text;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Dishwasher.Import;
using Path = System.IO.Path;
using Uri = Android.Net.Uri;

namespace Dishwasher.Import
{
    public sealed class ImportController
    {
        private const int RequestFolder = 7401;
        private const int RequestFile = 7402;

        private readonly Activity _activity;
        private readonly Action _onStartGame;

        private LinearLayout _root;
        private ScrollView _outer;
        private TextView _status;
        private TextView _details;
        private ProgressBar _progress;
        private Button _folderBtn;
        private Button _zipBtn;
        private Button _startBtn;

        private volatile bool _busy;
        private long _lastPosted;

        public bool Visible { get; private set; }

        public ImportController(Activity activity, Action onStartGame)
        {
            _activity = activity;
            _onStartGame = onStartGame;
            BuildUi();
        }

        // ------------------------------------------------------------- UI
        private int Dp(float v)
        {
            return (int)(v * _activity.Resources.DisplayMetrics.Density + 0.5f);
        }

        private void BuildUi()
        {
            _root = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            _root.SetBackgroundColor(Color.Black);
            _root.SetPadding(Dp(20), Dp(16), Dp(20), Dp(16));

            var title = new TextView(_activity) { Text = "The Dishwasher — Import Game Files" };
            title.SetTextColor(Color.White);
            title.SetTextSize(Android.Util.ComplexUnitType.Sp, 20f);
            _root.AddView(title);

            var blurb = new TextView(_activity)
            {
                Text =
                    "This public build contains no game content. To play, supply your own copy:\n\n" +
                    "• SELECT FOLDER — pick the extracted game folder.\n" +
                    "• SELECT ZIP / PACKAGE — pick the retail .zip or a .zip of the extracted files.\n\n" +
                    "Your files are compared with the bundled SHA-256 manifest, but the import is " +
                    "tolerant: files that differ or are missing are only warnings, so a different " +
                    "revision still works — a missing audio bank just means that sound is absent. " +
                    "Textures, fonts and audio (360 XMA) are derived on-device from your files; " +
                    "nothing is downloaded. Audio derivation can take a minute for the music."
            };
            blurb.SetTextColor(Color.Argb(255, 200, 200, 200));
            blurb.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
            var blurbLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,
                                                        ViewGroup.LayoutParams.WrapContent);
            blurbLp.TopMargin = Dp(8);
            _root.AddView(blurb, blurbLp);

            _folderBtn = MakeButton("SELECT FOLDER");
            _folderBtn.Click += (s, e) => PickFolder();
            _root.AddView(_folderBtn);

            _zipBtn = MakeButton("SELECT ZIP / PACKAGE");
            _zipBtn.Click += (s, e) => PickFile();
            _root.AddView(_zipBtn);

            _startBtn = MakeButton("START GAME");
            _startBtn.Enabled = false;
            _startBtn.Visibility = ViewStates.Gone;
            _startBtn.Click += (s, e) => _onStartGame?.Invoke();
            _root.AddView(_startBtn);

            _progress = new ProgressBar(_activity, null,
                Android.Resource.Attribute.ProgressBarStyleHorizontal)
            {
                Max = 100,
                Progress = 0,
                Visibility = ViewStates.Gone,
            };
            var progLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(8));
            progLp.TopMargin = Dp(10);
            _root.AddView(_progress, progLp);

            _status = new TextView(_activity) { Text = "Waiting for your game files…" };
            _status.SetTextColor(Color.White);
            _status.SetTextSize(Android.Util.ComplexUnitType.Sp, 14f);
            var statusLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,
                                                         ViewGroup.LayoutParams.WrapContent);
            statusLp.TopMargin = Dp(8);
            _root.AddView(_status, statusLp);

            _details = new TextView(_activity) { Text = "" };
            _details.SetTextColor(Color.Argb(255, 230, 170, 170));
            _details.SetTextSize(Android.Util.ComplexUnitType.Sp, 11f);
            var detLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,
                                                      ViewGroup.LayoutParams.WrapContent);
            detLp.TopMargin = Dp(8);
            _root.AddView(_details, detLp);
        }

        private Button MakeButton(string text)
        {
            var b = new Button(_activity) { Text = text };
            b.SetTextSize(Android.Util.ComplexUnitType.Sp, 15f);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(52));
            lp.TopMargin = Dp(10);
            b.LayoutParameters = lp;
            return b;
        }

        public void Show()
        {
            Visible = true;
            // Wrap the whole screen so the verification report (which can list
            // many missing/mismatched files) is always reachable by scrolling.
            _outer = new ScrollView(_activity);
            _outer.AddView(_root, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent,
                                                             ViewGroup.LayoutParams.WrapContent));
            _activity.SetContentView(_outer);
            Log.Info("[import] public import screen shown");
        }

        // ----------------------------------------------------- pick intents
        private void PickFolder()
        {
            if (_busy) return;
            try
            {
                var intent = new Intent(Intent.ActionOpenDocumentTree);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission
                              | ActivityFlags.GrantPersistableUriPermission
                              | ActivityFlags.GrantWriteUriPermission);
                _activity.StartActivityForResult(intent, RequestFolder);
            }
            catch (Exception ex) { ShowError("folder picker unavailable: " + ex.Message); }
        }

        private void PickFile()
        {
            if (_busy) return;
            try
            {
                var intent = new Intent(Intent.ActionOpenDocument);
                intent.AddCategory(Intent.CategoryOpenable);
                intent.SetType("*/*");
                intent.PutExtra(Intent.ExtraMimeTypes, new[]
                {
                    "application/zip", "application/x-zip-compressed",
                    "application/octet-stream", "*/*",
                });
                // Open at Downloads when the provider supports it (a courtesy:
                // it is where users normally put a downloaded .zip).
                try
                {
                    var dl = Android.Provider.DocumentsContract.BuildDocumentUri(
                        "com.android.externalstorage.documents", "primary:Download");
                    intent.PutExtra("android.provider.extra.INITIAL_URI", dl);
                }
                catch { }
                intent.AddFlags(ActivityFlags.GrantReadUriPermission
                              | ActivityFlags.GrantPersistableUriPermission);
                _activity.StartActivityForResult(intent, RequestFile);
            }
            catch (Exception ex) { ShowError("file picker unavailable: " + ex.Message); }
        }

        public void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            if (requestCode != RequestFolder && requestCode != RequestFile) return;
            if (resultCode != Result.Ok || data?.Data == null) return;

            Uri uri = data.Data;
            try
            {
                _activity.ContentResolver.TakePersistableUriPermission(
                    uri, ActivityFlags.GrantReadUriPermission);
            }
            catch { /* not all providers grant persistable; the copy is immediate anyway */ }

            StartImport(uri, requestCode == RequestFolder);
        }

        // ------------------------------------------------------- import work
        private void StartImport(Uri uri, bool isTree)
        {
            if (_busy) return;
            _busy = true;
            SetButtonsEnabled(false);
            _startBtn.Visibility = ViewStates.Gone;
            _startBtn.Enabled = false;
            _status.Text = "Preparing…";
            _details.Text = "";
            _progress.Visibility = ViewStates.Visible;
            _progress.Progress = 0;

            var thread = new Thread(() => RunImport(uri, isTree)) { IsBackground = true };
            thread.Start();
        }

        private void RunImport(Uri uri, bool isTree)
        {
            IGameContentSource source = null;
            try
            {
                string tempDir = _activity.CacheDir.AbsolutePath;
                TryDelete(Path.Combine(tempDir, "picked-import.bin"));
                TryDelete(Path.Combine(tempDir, "package.stfs"));

                Post(() => _status.Text = "Reading selected files…");
                source = isTree
                    ? AndroidContentSourceFactory.FromTree(_activity, uri)
                    : AndroidContentSourceFactory.FromDocument(_activity, uri, tempDir);

                ImportManifest manifest = PublicBuild.LoadManifest();
                _lastPosted = 0;

                ImportReport report = ContentImporter.Validate(source, manifest, OnProgress);
                // Full per-file detail always goes to the log; the screen gets a
                // concise summary only.
                LogReport(report);

                if (PublicBuild.StrictContentValidation && !report.AllOk)
                {
                    Post(() => ShowFailure(report));
                    return;
                }
                if (report.NothingUsable)
                {
                    Post(() => ShowNothingUsable(report));
                    return;
                }

                string dest = PublicBuild.ContentDir;
                AudioImportResult audio = ContentImporter.StageAndDerive(source, manifest, dest, OnProgress);
                PublicBuild.WriteCompletionMarker(manifest.Version, manifest.Files.Count, dest);
                Post(() => ShowSuccess(report, audio));
            }
            catch (Exception ex)
            {
                Log.Exception("import", ex);
                Post(() => ShowError(ex.Message));
            }
            finally
            {
                try { source?.Dispose(); } catch { }
            }
        }

        private void OnProgress(ImportProgress p)
        {
            // Throttle UI traffic: every 4 files, plus the final one.
            if (p.Done != p.Total && p.Done % 4 != 0) return;
            int pct = p.Total > 0 ? (int)((long)p.Done * 100 / p.Total) : 0;
            string phase = p.Phase;
            string path = p.Path;
            int done = p.Done, total = p.Total;
            Post(() =>
            {
                _progress.Progress = pct;
                _status.Text = phase + "  " + done + "/" + total + "  " + ShortName(path);
            });
        }

        private static string ShortName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        /// <summary>Dump the full per-file report to the log.  The screen only
        /// ever shows a concise summary: a 458-line dump is noise.</summary>
        private static void LogReport(ImportReport report)
        {
            Log.Info("[import] report: " + report.Summary()
                     + " core_present=" + report.CorePresent);
            if (report.MissingFiles.Count > 0)
            {
                Log.Info("[import] missing files (" + report.MissingFiles.Count + "):");
                foreach (string p in report.MissingFiles)
                    Log.Info("[import]   missing: " + p);
            }
            if (report.DifferedFiles.Count > 0)
            {
                Log.Info("[import] differed files (" + report.DifferedFiles.Count + "):");
                foreach (string p in report.DifferedFiles)
                    Log.Info("[import]   differed: " + p);
            }
            foreach (string n in report.Notes)
                Log.Info("[import] note: " + n);
        }

        /// <summary>Strict-mode rejection (PublicBuild.StrictContentValidation).</summary>
        private void ShowFailure(ImportReport report)
        {
            _busy = false;
            _progress.Visibility = ViewStates.Gone;
            _status.Text = "✗ Strict validation failed — selection rejected.";
            var sb = new StringBuilder();
            sb.AppendLine(report.Summary());
            sb.AppendLine();
            if (report.MissingFiles.Count > 0)
            {
                sb.AppendLine("Missing:");
                AppendList(sb, report.MissingFiles, 8);
            }
            if (report.DifferedFiles.Count > 0)
            {
                sb.AppendLine("Differed:");
                AppendList(sb, report.DifferedFiles, 8);
            }
            sb.AppendLine();
            sb.AppendLine("Strict content validation is enabled in this build.");
            sb.AppendLine("Full per-file detail is in the log (logcat tag \"Dishwasher\").");
            _details.Text = sb.ToString();
            SetButtonsEnabled(true);
            Log.Error("[import] FAILED (strict) " + report.Summary());
        }

        /// <summary>The only tolerant-mode block: nothing usable to derive.</summary>
        private void ShowNothingUsable(ImportReport report)
        {
            _busy = false;
            _progress.Visibility = ViewStates.Gone;
            _status.Text = "✗ Nothing usable found — is this the game?";
            var sb = new StringBuilder();
            sb.AppendLine("No usable Dishwasher content was found in your selection.");
            sb.AppendLine(report.Summary());
            sb.AppendLine();
            if (!report.CorePresent)
                sb.AppendLine("The core boot files are absent: "
                    + string.Join(", ", ContentImporter.CoreAnchors) + ".");
            sb.AppendLine();
            sb.AppendLine("Pick one of:");
            sb.AppendLine("  • the retail XBLA .zip (the package inside it),");
            sb.AppendLine("  • a .zip of the extracted game files, or");
            sb.AppendLine("  • the extracted game folder itself.");
            _details.Text = sb.ToString();
            SetButtonsEnabled(true);
            Log.Info("[import] NOTHING USABLE " + report.Summary()
                     + " core_present=" + report.CorePresent);
        }

        private static void AppendList(StringBuilder sb, System.Collections.Generic.List<string> items, int max)
        {
            if (items.Count == 0) { sb.AppendLine("  (none)"); return; }
            int n = Math.Min(items.Count, max);
            for (int i = 0; i < n; i++) sb.AppendLine("  • " + items[i]);
            if (items.Count > n) sb.AppendLine("  … and " + (items.Count - n) + " more");
        }

        private static bool AnyMissingPrefix(ImportReport report, string prefix)
        {
            foreach (string p in report.MissingFiles)
                if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private void ShowSuccess(ImportReport report, AudioImportResult audio)
        {
            _busy = false;
            _progress.Progress = 100;
            _progress.Visibility = ViewStates.Gone;
            _status.Text = "✓ Import complete — continuing";
            var sb = new StringBuilder();
            sb.AppendLine(report.Summary());
            sb.AppendLine();
            if (report.Differed > 0)
                sb.AppendLine("• " + report.Differed + " file(s) differ from the reference copy — "
                              + "a different game revision. They were imported as-is. (fine, continuing)");
            if (report.Missing > 0)
            {
                sb.AppendLine("• " + report.Missing + " file(s) were not found — the features that "
                              + "use them may be unavailable.");
                if (AnyMissingPrefix(report, "sfx/"))
                    sb.AppendLine("   ↳ some audio banks are absent: those sounds/music will be silent.");
            }
            if (report.Differed == 0 && report.Missing == 0)
                sb.AppendLine("Every file matched the reference copy exactly.");
            sb.AppendLine();
            string audioLine = audio == null
                ? "Audio: not derived."
                : "Audio: " + audio.WavesDecoded + "/" + (audio.WavesDecoded + audio.WavesFailed)
                  + " waves (" + audio.WavFiles + " WAV, " + audio.OggFiles + " OGG), "
                  + audio.CueCount + " cues" + (audio.NativeAvailable ? "" : " [decoder unavailable]")
                  + (string.IsNullOrEmpty(audio.Notes) ? "" : "\n" + audio.Notes);
            if (audio != null && (audio.StageFailed > 0))
                sb.AppendLine("Some files could not be derived and were skipped (" + audio.StageFailed + ").");
            sb.AppendLine(audioLine);
            sb.AppendLine();
            sb.AppendLine("Full per-file detail (matched / differed / missing) is in the log:");
            sb.AppendLine("logcat tag \"Dishwasher\", lines starting with [import].");
            _details.Text = sb.ToString();
            SetButtonsEnabled(true);
            _startBtn.Visibility = ViewStates.Visible;
            _startBtn.Enabled = true;
            Log.Info("[import] SUCCESS " + report.Summary() + " | " + (audioLine ?? ""));
        }

        private void ShowError(string message)
        {
            _busy = false;
            _progress.Visibility = ViewStates.Gone;
            _status.Text = "✗ Import error";
            _details.Text = message;
            SetButtonsEnabled(true);
            Log.Error("[import] error: " + message);
        }

        private void SetButtonsEnabled(bool enabled)
        {
            _activity.RunOnUiThread(() =>
            {
                _folderBtn.Enabled = enabled;
                _zipBtn.Enabled = enabled;
            });
        }

        private void Post(Action action)
        {
            _activity.RunOnUiThread(action);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
#endif
