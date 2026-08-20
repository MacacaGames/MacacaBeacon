using System;
using System.Collections.Generic;
using System.IO;

namespace MacacaGames.RuntimeBugReporter
{
    public sealed class BugReport
    {
        public string Id;
        public DateTime CreatedUtc;
        public string Reporter;
        public string Category;
        public string Title;
        public string Description;
        public readonly Dictionary<string, string> Fields = new Dictionary<string, string>();
        public readonly List<BugReportAttachment> Attachments = new List<BugReportAttachment>();
    }

    public sealed class BugReportAttachment
    {
        public string FileName;
        public string MimeType;
        public byte[] Data;
        public string FilePath;
        public string AltText;
        internal bool DeleteSourceAfterStaging;

        public long Length
        {
            get
            {
                if (Data != null)
                    return Data.LongLength;
                if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
                    return new FileInfo(FilePath).Length;
                return 0;
            }
        }

        public BugReportAttachment(string fileName, string mimeType, byte[] data, string altText = "")
        {
            FileName = fileName;
            MimeType = mimeType;
            Data = data;
            AltText = altText;
        }

        public static BugReportAttachment FromFile(string fileName, string mimeType, string filePath, string altText = "")
        {
            return new BugReportAttachment(fileName, mimeType, null, altText)
            {
                FilePath = filePath
            };
        }
    }

    public struct BugReportSendResult
    {
        public bool Success;
        public string Message;

        public static BugReportSendResult Ok(string message) => new BugReportSendResult { Success = true, Message = message };
        public static BugReportSendResult Fail(string message) => new BugReportSendResult { Success = false, Message = message };
    }

    public interface IBugReportDataProvider
    {
        void Collect(BugReport report);
    }

    public interface IBugReportTransport
    {
        System.Collections.IEnumerator Send(BugReport report, Action<BugReportSendResult> completed);
    }
}
