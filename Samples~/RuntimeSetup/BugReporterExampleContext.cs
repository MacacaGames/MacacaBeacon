using MacacaGames.RuntimeBugReporter;
using UnityEngine;

public sealed class BugReporterExampleContext : MonoBehaviour, IBugReportDataProvider
{
    [SerializeField] private string playerId = "anonymous";

    private void OnEnable() => BugReporter.RegisterDataProvider(this);
    private void OnDisable() => BugReporter.UnregisterDataProvider(this);

    public void Collect(BugReport report)
    {
        report.Fields["Player ID"] = playerId;
        report.Fields["Position"] = transform.position.ToString("F2");
    }

    [ContextMenu("Open Macaca Beacon")]
    private void OpenReporter() => BugReporter.Open();
}
