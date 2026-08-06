using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace MacacaGames.RuntimeBugReporter
{
    public sealed class SlackBugReportTransport : IBugReportTransport
    {
        private const string PostMessageEndpoint = "https://slack.com/api/chat.postMessage";
        private const string UploadUrlEndpoint = "https://slack.com/api/files.getUploadURLExternal";
        private const string CompleteUploadEndpoint = "https://slack.com/api/files.completeUploadExternal";
        private readonly string botToken;
        private readonly string channelId;

        public SlackBugReportTransport(string botToken, string channelId)
        {
            this.botToken = botToken == null ? "" : botToken.Trim();
            this.channelId = channelId == null ? "" : channelId.Trim();
        }

        public IEnumerator Send(BugReport report, Action<BugReportSendResult> completed)
        {
            if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(channelId))
            {
                completed?.Invoke(BugReportSendResult.Fail("Configure both Slack Bot Token and Channel ID."));
                yield break;
            }

            string parentMessageTs = null;
            var postPayload = "{\"channel\":\"" + JsonEscape(channelId) + "\",\"text\":\"" + JsonEscape(BuildSlackMessage(report)) + "\"}";
            using (var request = AuthorizedJsonRequest(PostMessageEndpoint, postPayload))
            {
                yield return request.SendWebRequest();
                var response = request.downloadHandler == null ? "" : request.downloadHandler.text;
                var parsed = string.IsNullOrEmpty(response) ? null : JsonUtility.FromJson<SlackPostMessageResponse>(response);
                if (request.result != UnityWebRequest.Result.Success || parsed == null || !parsed.ok || string.IsNullOrEmpty(parsed.ts))
                {
                    var error = parsed != null && !string.IsNullOrEmpty(parsed.error) ? parsed.error : RequestError(request);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.needed))
                    {
                        error += " (needed: " + parsed.needed;
                        if (!string.IsNullOrEmpty(parsed.provided))
                            error += "; provided: " + parsed.provided;
                        error += ")";
                    }
                    completed?.Invoke(BugReportSendResult.Fail("Slack could not create the report thread. Ensure the bot has chat:write and is in the channel: " + error));
                    yield break;
                }
                parentMessageTs = parsed.ts;
            }

            if (report.Attachments.Count == 0)
            {
                completed?.Invoke(BugReportSendResult.Ok("Report sent to Slack."));
                yield break;
            }

            var uploadedFiles = new List<UploadedFile>();
            foreach (var attachment in report.Attachments)
            {
                if (attachment.Length <= 0)
                    continue;
                UploadedFile uploaded = null;
                string uploadError = null;
                yield return UploadAttachment(attachment, (file, error) => { uploaded = file; uploadError = error; });
                if (uploaded == null)
                {
                    completed?.Invoke(BugReportSendResult.Fail("Report text sent, but attachment upload failed: " + uploadError));
                    yield break;
                }
                uploadedFiles.Add(uploaded);
            }

            if (uploadedFiles.Count == 0)
            {
                completed?.Invoke(BugReportSendResult.Ok("Report sent to Slack."));
                yield break;
            }

            var completePayload = BuildCompleteUploadPayload(uploadedFiles, channelId, parentMessageTs, "Attachments for report " + report.Id);
            using (var request = AuthorizedJsonRequest(CompleteUploadEndpoint, completePayload))
            {
                yield return request.SendWebRequest();
                var response = request.downloadHandler == null ? "" : request.downloadHandler.text;
                var parsed = string.IsNullOrEmpty(response) ? null : JsonUtility.FromJson<SlackBasicResponse>(response);
                if (request.result != UnityWebRequest.Result.Success || parsed == null || !parsed.ok)
                {
                    var error = parsed != null && !string.IsNullOrEmpty(parsed.error) ? parsed.error : RequestError(request);
                    completed?.Invoke(BugReportSendResult.Fail("Report text sent, but Slack could not finalize attachments: " + error));
                    yield break;
                }
            }

            completed?.Invoke(BugReportSendResult.Ok("Report and " + uploadedFiles.Count + " attachment(s) sent to Slack."));
        }

        private IEnumerator UploadAttachment(BugReportAttachment attachment, Action<UploadedFile, string> completed)
        {
            var form = "filename=" + UnityWebRequest.EscapeURL(attachment.FileName) + "&length=" + attachment.Length;
            using (var request = new UnityWebRequest(UploadUrlEndpoint, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(form));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
                request.SetRequestHeader("Authorization", "Bearer " + botToken);
                request.timeout = 30;
                yield return request.SendWebRequest();
                var response = request.downloadHandler == null ? "" : request.downloadHandler.text;
                var parsed = string.IsNullOrEmpty(response) ? null : JsonUtility.FromJson<SlackUploadUrlResponse>(response);
                if (request.result != UnityWebRequest.Result.Success || parsed == null || !parsed.ok)
                {
                    completed(null, parsed != null && !string.IsNullOrEmpty(parsed.error) ? parsed.error : RequestError(request));
                    yield break;
                }

                using (var upload = new UnityWebRequest(parsed.upload_url, UnityWebRequest.kHttpVerbPOST))
                {
                    upload.uploadHandler = attachment.Data != null
                        ? (UploadHandler)new UploadHandlerRaw(attachment.Data)
                        : new UploadHandlerFile(attachment.FilePath);
                    upload.downloadHandler = new DownloadHandlerBuffer();
                    upload.SetRequestHeader("Content-Type", string.IsNullOrEmpty(attachment.MimeType) ? "application/octet-stream" : attachment.MimeType);
                    upload.timeout = 60;
                    yield return upload.SendWebRequest();
                    if (upload.result != UnityWebRequest.Result.Success)
                    {
                        completed(null, RequestError(upload));
                        yield break;
                    }
                }

                completed(new UploadedFile(parsed.file_id, attachment.FileName), null);
            }
        }

        private UnityWebRequest AuthorizedJsonRequest(string url, string json)
        {
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            request.SetRequestHeader("Authorization", "Bearer " + botToken);
            request.timeout = 30;
            return request;
        }

        private static string BuildSlackMessage(BugReport report)
        {
            var builder = new StringBuilder();
            builder.Append(":beetle: *").Append(SlackEscape(report.Title)).Append("*\n");
            builder.Append("*ID:* `").Append(report.Id).Append("`  *Category:* ").Append(SlackEscape(report.Category)).Append('\n');
            if (!string.IsNullOrWhiteSpace(report.Reporter))
                builder.Append("*Reporter:* ").Append(SlackEscape(report.Reporter)).Append('\n');
            builder.Append("*Description*\n").Append(SlackEscape(report.Description)).Append('\n');
            foreach (var field in report.Fields)
                builder.Append("*").Append(SlackEscape(field.Key)).Append(":* ").Append(SlackEscape(field.Value)).Append('\n');
            if (report.Attachments.Count > 0)
                builder.Append("*Attachments:* ").Append(report.Attachments.Count).Append(" (uploaded by the configured Slack app)");
            return builder.ToString();
        }

        private static string BuildCompleteUploadPayload(List<UploadedFile> files, string channel, string threadTs, string comment)
        {
            var builder = new StringBuilder("{\"files\":[");
            for (var index = 0; index < files.Count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append("{\"id\":\"").Append(JsonEscape(files[index].Id)).Append("\",\"title\":\"")
                    .Append(JsonEscape(files[index].Title)).Append("\"}");
            }
            builder.Append("],\"channel_id\":\"").Append(JsonEscape(channel)).Append("\",\"thread_ts\":\"")
                .Append(JsonEscape(threadTs)).Append("\",\"initial_comment\":\"")
                .Append(JsonEscape(comment)).Append("\"}");
            return builder.ToString();
        }

        private static string SlackEscape(string value) => (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var builder = new StringBuilder(value.Length + 16);
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 32) builder.Append("\\u").Append(((int)character).ToString("x4"));
                        else builder.Append(character);
                        break;
                }
            }
            return builder.ToString();
        }

        private static string RequestError(UnityWebRequest request)
        {
            var body = request.downloadHandler == null ? "" : request.downloadHandler.text;
            return string.IsNullOrWhiteSpace(body) ? request.error : body;
        }

        [Serializable]
        private sealed class SlackBasicResponse { public bool ok; public string error; }
        [Serializable]
        private sealed class SlackPostMessageResponse
        {
            public bool ok;
            public string error;
            public string needed;
            public string provided;
            public string channel;
            public string ts;
        }
        [Serializable]
        private sealed class SlackUploadUrlResponse { public bool ok; public string upload_url; public string file_id; public string error; }
        private sealed class UploadedFile
        {
            public readonly string Id;
            public readonly string Title;
            public UploadedFile(string id, string title) { Id = id; Title = title; }
        }
    }
}
