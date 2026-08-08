using System.ComponentModel;
using MacacaGames.RuntimeBugReporter;

public partial class SROptions
{
    [Category("Macaca Beacon")]
    [DisplayName("Rolling Video")]
    public bool MacacaBeaconRollingVideo
    {
        get { return BugReporter.IsVideoRecordingEnabled; }
        set
        {
            BugReporter.SetVideoRecordingEnabled(value);
        }
    }
}
