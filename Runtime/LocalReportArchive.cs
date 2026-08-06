using System;
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

                File.WriteAllText(Path.Combine(directory, "report.txt"), BuildReportText(report), new UTF8Encoding(false));
                for (var index = 0; index < report.Attachments.Count; index++)
                {
                    var attachment = report.Attachments[index];
                    if (attachment == null || attachment.Data == null || attachment.Data.Length == 0)
                        continue;
                    var fileName = SafeName(attachment.FileName);
                    if (string.IsNullOrEmpty(fileName))
                        fileName = "attachment-" + (index + 1);
                    File.WriteAllBytes(UniquePath(directory, fileName), attachment.Data);
                }

                Prune(root, Mathf.Max(1, maximumRetainedReports), directory);
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
                builder.AppendLine(attachment.FileName + " | " + attachment.MimeType + " | " + (attachment.Data == null ? 0 : attachment.Data.Length) + " bytes");
            return builder.ToString();
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
