using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal static class LocalReportArchive
    {
        private const string RootFolderName = "MacacaBeacon";
        private const string PendingFolderName = "PendingReports";

        public static bool TryStage(BugReport report, int maximumRetainedReports, out string directory, out string error)
        {
            directory = null;
            error = null;
            try
            {
                var root = Path.Combine(Application.persistentDataPath, RootFolderName, PendingFolderName);
                Directory.CreateDirectory(root);
                directory = Path.Combine(root, report.CreatedUtc.ToString("yyyyMMdd-HHmmss") + "-" + SafeName(report.Id));
                Directory.CreateDirectory(directory);
                var stagedFiles = new List<StagedFile>();

                File.WriteAllText(Path.Combine(directory, "report.txt"), BuildReportText(report), new UTF8Encoding(false));
                for (var index = 0; index < report.Attachments.Count; index++)
                {
                    var attachment = report.Attachments[index];
                    if (attachment == null || attachment.Length <= 0)
                        continue;
                    var fileName = SafeName(attachment.FileName);
                    if (string.IsNullOrEmpty(fileName))
                        fileName = "attachment-" + (index + 1);
                    var stagedPath = UniquePath(directory, fileName);
                    if (attachment.Data != null)
                    {
                        File.WriteAllBytes(stagedPath, attachment.Data);
                    }
                    else
                    {
                        File.Copy(attachment.FilePath, stagedPath, false);
                        stagedFiles.Add(new StagedFile(attachment, attachment.FilePath, stagedPath, attachment.DeleteSourceAfterStaging));
                    }
                }

                Prune(root, Mathf.Max(1, maximumRetainedReports), directory);
                foreach (var stagedFile in stagedFiles)
                {
                    stagedFile.Attachment.FilePath = stagedFile.StagedPath;
                    stagedFile.Attachment.DeleteSourceAfterStaging = false;
                    if (stagedFile.DeleteSource)
                        TryDeleteFile(stagedFile.SourcePath);
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                TryDelete(directory);
                directory = null;
                return false;
            }
        }

        public static void MarkFailed(string directory, string reason)
        {
            if (string.IsNullOrEmpty(directory))
                return;
            try
            {
                File.WriteAllText(
                    Path.Combine(directory, "upload-error.txt"),
                    DateTime.UtcNow.ToString("O") + Environment.NewLine + (reason ?? "Unknown upload error"),
                    new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Macaca Beacon] Could not write local upload error: " + exception.Message);
            }
        }

        public static void Discard(string directory) => TryDelete(directory);

        private static string BuildReportText(BugReport report)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Macaca Beacon pending report");
            builder.AppendLine("ID: " + report.Id);
            builder.AppendLine("UTC: " + report.CreatedUtc.ToString("O"));
            builder.AppendLine("Category: " + report.Category);
            builder.AppendLine("Title: " + report.Title);
            builder.AppendLine("Reporter: " + report.Reporter);
            builder.AppendLine();
            builder.AppendLine("Description");
            builder.AppendLine("-----------");
            builder.AppendLine(report.Description);
            builder.AppendLine();
            builder.AppendLine("Fields");
            builder.AppendLine("------");
            foreach (var field in report.Fields)
                builder.AppendLine(field.Key + ": " + field.Value);
            builder.AppendLine();
            builder.AppendLine("Attachments");
            builder.AppendLine("-----------");
            foreach (var attachment in report.Attachments)
                builder.AppendLine(attachment.FileName + " | " + attachment.MimeType + " | " + attachment.Length + " bytes");
            return builder.ToString();
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            try
            {
                File.Delete(path);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Macaca Beacon] Could not remove temporary attachment: " + exception.Message);
            }
        }

        private sealed class StagedFile
        {
            public readonly BugReportAttachment Attachment;
            public readonly string SourcePath;
            public readonly string StagedPath;
            public readonly bool DeleteSource;

            public StagedFile(BugReportAttachment attachment, string sourcePath, string stagedPath, bool deleteSource)
            {
                Attachment = attachment;
                SourcePath = sourcePath;
                StagedPath = stagedPath;
                DeleteSource = deleteSource;
            }
        }

        private static string SafeName(string value)
        {
            var result = value ?? "";
            foreach (var character in Path.GetInvalidFileNameChars())
                result = result.Replace(character, '_');
            return result.Replace('/', '_').Replace('\\', '_');
        }

        private static string UniquePath(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
                return path;
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            for (var index = 2; ; index++)
            {
                path = Path.Combine(directory, stem + "-" + index + extension);
                if (!File.Exists(path))
                    return path;
            }
        }

        private static void Prune(string root, int maximumRetainedReports, string currentDirectory)
        {
            var directories = Directory.GetDirectories(root);
            Array.Sort(directories, StringComparer.Ordinal);
            var excess = directories.Length - maximumRetainedReports;
            for (var index = 0; index < excess; index++)
            {
                if (!string.Equals(directories[index], currentDirectory, StringComparison.Ordinal))
                    TryDelete(directories[index]);
            }
        }

        private static void TryDelete(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return;
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Macaca Beacon] Could not remove local report archive: " + exception.Message);
            }
        }
    }
}
