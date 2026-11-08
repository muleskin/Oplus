namespace Oplus.Core;

public enum JobState { Pending = 3, Held = 4, Processing = 5, Stopped = 6, Canceled = 7, Aborted = 8, Completed = 9 }

public sealed class PrintJob
{
    public int Id;
    public required PrinterEntry Printer;
    public string Name = "Untitled";
    public string User = "anonymous";
    public string Format = "application/octet-stream";
    public string FilePath = "";
    public int Copies = 1;
    public bool TwoSided;
    public string ClientIp = "";
    public string Mac = "";
    public JobState State = JobState.Pending;
    public string StateReason = "none";
    public DateTime Created = DateTime.Now;
    public DateTime? Started, Finished;
    public int Pages;
    public long Bytes;
    public readonly CancellationTokenSource Cancel = new();

    public bool IsDone => State >= JobState.Canceled;
}
