namespace TRC_Bot;

public enum ManageState
{
    None,
    MainMenu,
    CreateName,
    CreateDayOfWeek,
    CreateHour,
    CreateMinuteFrom,
    CreateMinuteTo,
    CreateStartLat,
    CreateStartLng,
    CreateRadiusKm,
    CreateWindowStart,
    CreateWindowEnd,
    CreateTargetStart,
    CreateReportChat,
    EditSelectRun,
    EditMenu,
    EditName,
    EditDayOfWeek,
    EditHour,
    EditMinuteFrom,
    EditMinuteTo,
    EditStartLat,
    EditStartLng,
    EditRadiusKm,
    EditWindowStart,
    EditWindowEnd,
    EditTargetStart,
    EditReportChat,
    MemberMenu,
    AddMember,
    RemoveMember
}

public sealed class ManageSession
{
    public ManageState State { get; set; }
    public ClubRunDraft Draft { get; } = new();
    public long? SelectedClubRunId { get; set; }
}

public sealed class ClubRunDraft
{
    public string Name { get; set; } = string.Empty;
    public int DayOfWeek { get; set; }
    public int Hour { get; set; }
    public int MinuteFrom { get; set; }
    public int MinuteTo { get; set; }
    public double StartLat { get; set; }
    public double StartLng { get; set; }
    public double RadiusKm { get; set; } = 3;
    public string WindowStartLocal { get; set; } = "05:00";
    public string WindowEndLocal { get; set; } = "10:00";
    public string TargetStartLocal { get; set; } = "08:00";
    public long? ReportChatId { get; set; }

    public void Load(ClubRunRecord clubRun)
    {
        Name = clubRun.Name;
        DayOfWeek = clubRun.DayOfWeek;
        Hour = clubRun.Hour;
        MinuteFrom = clubRun.MinuteFrom;
        MinuteTo = clubRun.MinuteTo;
        StartLat = clubRun.StartLat;
        StartLng = clubRun.StartLng;
        RadiusKm = clubRun.RadiusKm;
        WindowStartLocal = clubRun.WindowStartLocal;
        WindowEndLocal = clubRun.WindowEndLocal;
        TargetStartLocal = clubRun.TargetStartLocal;
        ReportChatId = clubRun.ReportChatId;
    }
}
