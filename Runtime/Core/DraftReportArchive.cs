using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    [Serializable]
    internal sealed class DraftReportManifest
    {
        public int schemaVersion = 1;
        public string id;
        public string createdUtc;
        public string category;
        public string title;
        public string description;
        public string reporter;
        public string[] fieldKeys = new string[0];
        public string[] fieldValues = new string[0];
        public DraftAttachmentManifest[] attachments = new DraftAttachmentManifest[0];
        public string lastError;
    }

    [Serializable]
    internal sealed class DraftAttachmentManifest
    {
        public string kind;
        public string fileName;
        public string mimeType;
        public string altText;
        public bool included = true;
        public double durationSeconds;
        public int frameCount;
        public string encoderName;
        public int width;
        public int height;
    }

    internal static class DraftReportArchive
    {
        private const string RootFolderName = "MacacaBeacon";
        private const string DraftFolderName = "DraftReports";
        private const string ManifestFileName = "manifest.json";

        public static bool TryStage(
            BugReport report,
            int maximumRetainedDrafts,
            out string directory,
            out string error)
        {
            directory = null;
            error = null;
            if (report == null)
            {
                error = "The report is empty.";
                return false;
            }

            string stagingDirectory = null;
            try
            {
                var root = GetRootPath();
                Directory.CreateDirectory(root);
                stagingDirectory = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingDirectory);

                var manifest = CreateManifest(report);
                var stagedAttachments = new List<DraftAttachmentManifest>();
                var sourcesToDelete = new List<string>();
                foreach (var attachment in report.Attachments)
                {
                    if (attachment == null || attachment.Length <= 0)
                        continue;

                    var fileName = SafeName(attachment.FileName);
                    if (string.IsNullOrEmpty(fileName))
                        fileName = "attachment-" + (stagedAttachments.Count + 1);
                    fileName = UniqueFileName(stagingDirectory, fileName);
                    var destination = Path.Combine(stagingDirectory, fileName);
                    if (attachment.Data != null)
                        File.WriteAllBytes(destination, attachment.Data);
                    else
                        File.Copy(attachment.FilePath, destination, false);

                    var descriptor = CreateAttachmentManifest(attachment, fileName);
                    stagedAttachments.Add(descriptor);
                    if (attachment.DeleteSourceAfterStaging && !string.IsNullOrEmpty(attachment.FilePath))
                        sourcesToDelete.Add(attachment.FilePath);
                }

                manifest.attachments = stagedAttachments.ToArray();
                WriteManifest(stagingDirectory, manifest);

                directory = CreateUniqueDirectory(root, report.CreatedUtc, report.Id);
                Directory.Move(stagingDirectory, directory);
                stagingDirectory = null;
                foreach (var source in sourcesToDelete)
                    TryDeleteFile(source);
                Prune(root, Mathf.Max(1, maximumRetainedDrafts), directory);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                TryDeleteDirectory(stagingDirectory);
                return false;
            }
        }

        public static DraftReportManifest[] LoadAll()
        {
            var root = GetRootPath();
            if (!Directory.Exists(root))
                return new DraftReportManifest[0];

            var manifests = new List<DraftReportManifest>();
            foreach (var directory in Directory.GetDirectories(root))
            {
                DraftReportManifest manifest;
                if (TryRead(directory, out manifest))
                    manifests.Add(manifest);
            }
            manifests.Sort((left, right) => string.CompareOrdinal(right.createdUtc, left.createdUtc));
            return manifests.ToArray();
        }

        public static bool TryFindDirectory(string id, out string directory)
        {
            directory = null;
            if (string.IsNullOrEmpty(id))
                return false;

            var root = GetRootPath();
            if (!Directory.Exists(root))
                return false;
            foreach (var candidate in Directory.GetDirectories(root))
            {
                DraftReportManifest manifest;
                if (TryRead(candidate, out manifest) && string.Equals(manifest.id, id, StringComparison.Ordinal))
                {
                    directory = candidate;
                    return true;
                }
            }
            return false;
        }

        public static bool TryLoad(
            string directory,
            out DraftReportManifest manifest,
            out BugReport report,
            out byte[] screenshotBytes,
            out VideoCaptureResult videoCapture,
            out string error)
        {
            manifest = null;
            report = null;
            screenshotBytes = null;
            videoCapture = null;
            error = null;
            try
            {
                if (!TryRead(directory, out manifest))
                {
                    error = "Draft manifest could not be read.";
                    return false;
                }

                DateTime createdUtc;
                if (!DateTime.TryParse(manifest.createdUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out createdUtc))
                    createdUtc = DateTime.UtcNow;
                report = new BugReport
                {
                    Id = manifest.id,
                    CreatedUtc = createdUtc,
                    Category = manifest.category ?? "Other",
                    Title = manifest.title ?? "",
                    Description = manifest.description ?? "",
                    Reporter = manifest.reporter ?? ""
                };

                var fieldCount = Math.Min(
                    manifest.fieldKeys == null ? 0 : manifest.fieldKeys.Length,
                    manifest.fieldValues == null ? 0 : manifest.fieldValues.Length);
                for (var index = 0; index < fieldCount; index++)
                    report.Fields[manifest.fieldKeys[index] ?? ""] = manifest.fieldValues[index] ?? "";

                foreach (var attachment in manifest.attachments ?? new DraftAttachmentManifest[0])
                {
                    var path = Path.Combine(directory, SafeName(attachment.fileName));
                    if (!File.Exists(path))
                        continue;
                    if (string.Equals(attachment.kind, "screenshot", StringComparison.OrdinalIgnoreCase))
                    {
                        screenshotBytes = File.ReadAllBytes(path);
                        report.Attachments.Add(BugReportAttachment.FromFile(attachment.fileName, attachment.mimeType, path, attachment.altText));
                    }
                    else if (string.Equals(attachment.kind, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        videoCapture = new VideoCaptureResult(
                            path,
                            Path.GetExtension(attachment.fileName),
                            attachment.mimeType,
                            attachment.durationSeconds,
                            attachment.frameCount,
                            attachment.encoderName,
                            attachment.width,
                            attachment.height);
                        report.Attachments.Add(BugReportAttachment.FromFile(attachment.fileName, attachment.mimeType, path, attachment.altText));
                    }
                    else
                    {
                        report.Attachments.Add(BugReportAttachment.FromFile(attachment.fileName, attachment.mimeType, path, attachment.altText));
                    }
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                manifest = null;
                report = null;
                screenshotBytes = null;
                videoCapture = null;
                return false;
            }
        }

        public static bool TryUpdate(
            string directory,
            string category,
            string title,
            string description,
            string reporter,
            bool includeScreenshot,
            bool includeVideo,
            byte[] screenshotBytes,
            out string error)
        {
            error = null;
            try
            {
                DraftReportManifest manifest;
                if (!TryRead(directory, out manifest))
                {
                    error = "Draft manifest could not be read.";
                    return false;
                }
                manifest.category = category ?? "Other";
                manifest.title = title ?? "";
                manifest.description = description ?? "";
                manifest.reporter = reporter ?? "";
                foreach (var attachment in manifest.attachments ?? new DraftAttachmentManifest[0])
                {
                    if (string.Equals(attachment.kind, "screenshot", StringComparison.OrdinalIgnoreCase))
                        attachment.included = includeScreenshot;
                    else if (string.Equals(attachment.kind, "video", StringComparison.OrdinalIgnoreCase))
                        attachment.included = includeVideo;
                }
                if (screenshotBytes != null)
                {
                    foreach (var attachment in manifest.attachments ?? new DraftAttachmentManifest[0])
                    {
                        if (!string.Equals(attachment.kind, "screenshot", StringComparison.OrdinalIgnoreCase))
                            continue;
                        File.WriteAllBytes(Path.Combine(directory, SafeName(attachment.fileName)), screenshotBytes);
                        break;
                    }
                }
                manifest.lastError = null;
                WriteManifest(directory, manifest);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static void MarkFailed(string directory, string reason)
        {
            if (string.IsNullOrEmpty(directory))
                return;
            try
            {
                DraftReportManifest manifest;
                if (!TryRead(directory, out manifest))
                    return;
                manifest.lastError = reason ?? "Unknown upload error";
                WriteManifest(directory, manifest);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Macaca Beacon] Could not update draft error: " + exception.Message);
            }
        }

        public static void Discard(string directory) => TryDeleteDirectory(directory);

        public static void DiscardAll()
        {
            var root = GetRootPath();
            if (!Directory.Exists(root))
                return;

            foreach (var directory in Directory.GetDirectories(root))
                TryDeleteDirectory(directory);
        }

        private static DraftReportManifest CreateManifest(BugReport report)
        {
            var manifest = new DraftReportManifest
            {
                id = report.Id,
                createdUtc = report.CreatedUtc.ToString("O"),
                category = report.Category,
                title = report.Title,
                description = report.Description,
                reporter = report.Reporter
            };
            var keys = new List<string>();
            var values = new List<string>();
            foreach (var field in report.Fields)
            {
                keys.Add(field.Key);
                values.Add(field.Value);
            }
            manifest.fieldKeys = keys.ToArray();
            manifest.fieldValues = values.ToArray();
            return manifest;
        }

        private static DraftAttachmentManifest CreateAttachmentManifest(BugReportAttachment attachment, string fileName)
        {
            var mimeType = attachment.MimeType ?? "application/octet-stream";
            var kind = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? "screenshot"
                : mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                    ? "video"
                    : "attachment";
            var descriptor = new DraftAttachmentManifest
            {
                kind = kind,
                fileName = fileName,
                mimeType = mimeType,
                altText = attachment.AltText,
                included = true
            };
            return descriptor;
        }

        private static bool TryRead(string directory, out DraftReportManifest manifest)
        {
            manifest = null;
            try
            {
                var path = Path.Combine(directory, ManifestFileName);
                if (!File.Exists(path))
                    return false;
                manifest = JsonUtility.FromJson<DraftReportManifest>(File.ReadAllText(path, Encoding.UTF8));
                return manifest != null && !string.IsNullOrEmpty(manifest.id);
            }
            catch
            {
                return false;
            }
        }

        private static void WriteManifest(string directory, DraftReportManifest manifest)
        {
            var path = Path.Combine(directory, ManifestFileName);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(manifest, true), new UTF8Encoding(false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temporaryPath, path);
        }

        private static string GetRootPath()
        {
            return Path.Combine(Application.persistentDataPath, RootFolderName, DraftFolderName);
        }

        private static string CreateUniqueDirectory(string root, DateTime createdUtc, string id)
        {
            var baseName = createdUtc.ToString("yyyyMMdd-HHmmss") + "-" + SafeName(id);
            var directory = Path.Combine(root, baseName);
            var suffix = 2;
            while (Directory.Exists(directory))
                directory = Path.Combine(root, baseName + "-" + suffix++);
            return directory;
        }

        private static string UniqueFileName(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
                return fileName;
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            for (var index = 2; ; index++)
            {
                var candidate = stem + "-" + index + extension;
                if (!File.Exists(Path.Combine(directory, candidate)))
                    return candidate;
            }
        }

        private static string SafeName(string value)
        {
            var result = value ?? "";
            foreach (var character in Path.GetInvalidFileNameChars())
                result = result.Replace(character, '_');
            return result.Replace('/', '_').Replace('\\', '_');
        }

        private static void Prune(string root, int maximumRetainedDrafts, string currentDirectory)
        {
            var directories = Directory.GetDirectories(root);
            Array.Sort(directories, StringComparer.Ordinal);
            var excess = directories.Length - maximumRetainedDrafts;
            for (var index = 0; index < excess; index++)
            {
                if (!string.Equals(directories[index], currentDirectory, StringComparison.Ordinal))
                    TryDeleteDirectory(directories[index]);
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            try { File.Delete(path); }
            catch (Exception exception) { Debug.LogWarning("[Macaca Beacon] Could not remove temporary capture: " + exception.Message); }
        }

        private static void TryDeleteDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return;
            try { Directory.Delete(directory, true); }
            catch (Exception exception) { Debug.LogWarning("[Macaca Beacon] Could not remove draft: " + exception.Message); }
        }
    }
}
